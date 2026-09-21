<#
.SYNOPSIS
    Bakes the draft Store submission record (release-notes/NEXT.txt) into a finished one, named
    for the version the dispatch actually produced.

.DESCRIPTION
    The MSIX package version is Major.Minor.<run_number>, and run_number is not knowable until
    the run exists. Writing it into the record by hand afterwards is how 8.0.1716's record came
    to be committed with a placeholder in it, and how a record can end up naming a package
    nobody has. So the record is a TEMPLATE in the repo and the run bakes it.

    Two things this does that a find-and-replace would not:

    1. It MEASURES the What's New block against Partner Center's 1500-character cap and fails
       when it is over. The cap is silent at the far end: 7.0.1568's record carries a 4716
       character block, and to this day nobody knows whether it was trimmed by hand at the
       browser or cropped by the field, which would have left that listing ending mid-sentence
       for a month. A measurement beside the block is the only thing that closes that question,
       and a measurement nothing enforces is the one that gets skipped.

    2. It refuses to write a file that still contains a placeholder. A substitution that
       silently does nothing produces a plausible-looking record with {{VERSION}} in the middle
       of it, which is exactly the failure this script exists to prevent.

    Failing is safe here: the `release` job does not depend on `msix`, so a rejected record
    cannot block a GitHub Release. It blocks the Store lane only, which is the lane it is about.

.PARAMETER Version
    The package version without the MSIX revision, i.e. VERSION_PREFIX: Major.Minor.<run_number>,
    e.g. 8.0.1716. The MSIX itself carries this plus a reserved .0.

.PARAMETER RunId
    The workflow run id, so the record's own download command is copy-pasteable.

.PARAMETER Template
    The draft to bake. Defaults to release-notes/NEXT.txt beside this script.

.PARAMETER OutFile
    Where to write the baked record. Defaults to release-notes/<Version>.txt beside this script,
    which is the name it should be committed under.

.PARAMETER PasteFile
    Optional. Writes the What's New block ALONE to this path, exactly the bytes the Partner Center
    field should receive. Selecting a block out of a 20 KB record by eye is its own error: take in
    a marker line or the sentence above it and the listing carries it. Off by default because the
    record is the repo's single copy and a second one there would be free to drift; CI passes it,
    so the artifact carries both and nothing has to be selected by hand.

.PARAMETER MaxWhatsNewChars
    Partner Center's cap. Only a parameter so the failure path can be tested.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [string] $RunId = '',
    [string] $Template = '',
    [string] $OutFile = '',
    [string] $PasteFile = '',
    [int] $MaxWhatsNewChars = 1500
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$notesDir = Join-Path $PSScriptRoot 'release-notes'
if (-not $Template) { $Template = Join-Path $notesDir 'NEXT.txt' }
if (-not $OutFile)  { $OutFile  = Join-Path $notesDir "$Version.txt" }

if (-not (Test-Path -LiteralPath $Template)) {
    throw "No draft to bake at $Template. The record is written BEFORE the dispatch, not after."
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be Major.Minor.Build (the run number), got '$Version'."
}
$runNumber = $Version.Split('.')[-1]

# The previous release is whichever record in this directory sorts highest below the one being
# written. Derived rather than passed: a hand-passed "supersedes" is one more thing to get wrong,
# and the directory already knows. NEXT.txt is skipped by the version-shaped filter.
$previous = Get-ChildItem -LiteralPath $notesDir -Filter '*.txt' |
    Where-Object { $_.BaseName -match '^\d+\.\d+\.\d+$' -and $_.BaseName -ne $Version } |
    Sort-Object { [version] $_.BaseName } |
    Select-Object -Last 1

$previousVersion = if ($previous) { $previous.BaseName } else { 'none' }
$previousPackage = if ($previous) { "$($previous.BaseName).0" } else { 'none' }

# Best effort, and deliberately non-fatal. The span is provenance, not correctness, and a shallow
# checkout or a missing tag must never cost a release its package.
$spanCommits = 'an unrecorded number of'
if ($previous) {
    try {
        $count = & git rev-list --count "v$previousVersion..HEAD" 2>$null
        if ($LASTEXITCODE -eq 0 -and $count -match '^\d+$') { $spanCommits = $count }
    } catch {
        Write-Warning "Could not count commits since v$previousVersion (shallow checkout or missing tag)."
    }
    # git's exit code would otherwise become the SCRIPT's, because PowerShell hands back the last
    # native command's status and nothing here overrides it. A clone without the previous tag exits
    # 128, which would fail the step and, in a job that has already packed the bundle, report a
    # broken packaging lane for a record that baked perfectly. Caught exactly that way.
    $global:LASTEXITCODE = 0
}

$text = [System.IO.File]::ReadAllText($Template) -replace "`r`n", "`n"

$substitutions = [ordered] @{
    '{{VERSION}}'          = $Version
    '{{PACKAGE_VERSION}}'  = "$Version.0"
    '{{RUN_NUMBER}}'       = $runNumber
    '{{RUN_ID}}'           = $(if ($RunId) { $RunId } else { '<run-id>' })
    '{{TAG}}'              = "v$Version"
    '{{PREV_VERSION}}'     = $previousVersion
    '{{PREV_PACKAGE}}'     = $previousPackage
    '{{SPAN_COMMITS}}'     = $spanCommits
    '{{DATE}}'             = (Get-Date -Format 'yyyy-MM-dd')
}
foreach ($k in $substitutions.Keys) { $text = $text.Replace($k, $substitutions[$k]) }

# The block is delimited explicitly rather than by counting the ==== rules around it. The rules
# are decoration a future edit is free to move; these two lines are a contract, and they read as
# an instruction to the human doing the pasting besides.
$beginMarker = "----- BEGIN WHAT'S NEW -----"
$endMarker   = "----- END WHAT'S NEW -----"

$begin = $text.IndexOf($beginMarker, [System.StringComparison]::Ordinal)
$end   = $text.IndexOf($endMarker, [System.StringComparison]::Ordinal)
if ($begin -lt 0 -or $end -lt 0 -or $end -lt $begin) {
    throw "The draft must delimit its Partner Center copy with '$beginMarker' and '$endMarker'."
}

$blockStart = $begin + $beginMarker.Length
$block = $text.Substring($blockStart, $end - $blockStart).Trim("`n")

# What the field receives. A paste through a browser may turn each newline into CRLF, which costs
# one character per line against the same cap, so both are reported and the CRLF figure is the one
# enforced: it is the larger, and which one the browser applies is not ours to decide.
$lfChars   = $block.Length
# The @() is load bearing under Set-StrictMode -Version Latest. A pipeline that yields nothing is
# $null and one that yields a single item is that item, and neither answers .Count there, so this
# threw for any block with FEWER THAN TWO newlines. Written copy always has many, which is why it
# never fired; the RESET draft is a one-line sentinel, so it fired the moment NEXT.txt went back to
# its placeholder and turned the documented "an unwritten record only warns" into a failed msix job.
$crlfChars = $lfChars + @($block.ToCharArray() | Where-Object { $_ -eq "`n" }).Count

$text = $text.Replace('{{WHATSNEW_CHARS}}', "$lfChars").Replace('{{WHATSNEW_CHARS_CRLF}}', "$crlfChars")

$leftover = [regex]::Matches($text, '\{\{[A-Z_]+\}\}') | ForEach-Object { $_.Value } | Sort-Object -Unique
if ($leftover) {
    throw "The baked record still contains placeholders: $($leftover -join ', '). Either the draft names one this script does not supply, or a name is misspelt."
}

Write-Host "What's new block: $lfChars chars, $crlfChars as CRLF, cap $MaxWhatsNewChars."

# An UNWRITTEN record only warns, while an over-cap one fails, and the asymmetry is deliberate.
# Most dispatches are a binary release that owes the Store nothing, and the msix job runs on all of
# them; failing those would train everyone to ignore a red packaging lane. An over-cap block is the
# opposite: somebody wrote copy that cannot be pasted, and they are about to find out at the browser.
$unwritten = $block.Contains('WRITE THIS BEFORE DISPATCHING')
if ($unwritten) {
    Write-Warning "The Store record for this run was not written: NEXT.txt still carries its placeholder copy. The package is fine; its release notes are not. If this dispatch was only a binary release, that is expected."
}

if ($crlfChars -gt $MaxWhatsNewChars) {
    throw @"
The What's New block is $crlfChars characters as CRLF, over Partner Center's $MaxWhatsNewChars cap by $($crlfChars - $MaxWhatsNewChars).

Cut it in $Template and re-dispatch. The record itself says which way to cut; the
short version is that the headed sections are the release and the bullets are the tail.

This is a hard failure on purpose. The field does not report what it did with an
over-length paste, so an unmeasured block is a listing that may be live and
truncated mid-sentence with nothing to say so.
"@
}

$outDir = Split-Path -Parent $OutFile
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

# LF, like everything else in this repo (.gitattributes `* text=auto eol=lf`), so the baked file
# can be committed without a renormalisation diff.
[System.IO.File]::WriteAllText($OutFile, $text, (New-Object System.Text.UTF8Encoding $false))
Write-Host "Baked $Template -> $OutFile (supersedes $previousPackage, $spanCommits commits)."

# Written only after the record itself, so the two can never disagree about what was approved: this
# is the same $block the cap was measured on, not a re-read of the file.
if ($PasteFile) {
    [System.IO.File]::WriteAllText($PasteFile, $block, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "Partner Center copy alone -> $PasteFile ($lfChars chars)."
}

# Explicit, so the script's success is stated rather than inherited from whatever ran last.
exit 0
