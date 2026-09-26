<#
.SYNOPSIS
    Regenerates a baked-icon table from a .recipe manifest.

.DESCRIPTION
    The manifest is DATA, not this script: see src/TianWen.UI.Abstractions/icons.recipe, which carries
    the font, the output path, the namespace and the glyph list with a note on what each is for. Keeping
    it out of here is what lets the build watch it (warning TWIC0001 fires when the recipe's SHA-256 is
    not the one its generated table records in its header, or the table is missing; each IconRecipe item
    in TianWen.UI.Abstractions.csproj names its table) and what would let a generator read the same file
    later. The check compares CONTENT, never modification times: a time check fired for ever on a clone
    that pulled a recipe edit which left the table byte-identical (#792).

    The baking itself is DIR.Lib.IconBaker, run via `dnx` straight from nuget.org, so there is nothing to
    build first and no copy of the generator in this repo.

    The bake is byte-reproducible: ManagedFontRasterizer is pure managed (no FreeType, no DirectWrite),
    so the same recipe and font give identical output on any host. That is what makes a CI step which
    re-bakes and COMPARES possible, which is a stronger check than a timestamp: a timestamp catches a
    forgotten re-bake, a comparison also catches a hand-edited table.

    Line endings are the REPO's, not the baker's: the table is written and compared with LF, which is
    what .gitattributes checks out (eol=lf everywhere since 2026-09-11). The baker emits CRLF on every
    host, so a raw byte hash of its output against the checked-out table failed on ubuntu the moment
    the checkout stopped being CRLF, with the table itself unchanged.

.EXAMPLE
    pwsh tools/bake-icons.ps1
.EXAMPLE
    pwsh tools/bake-icons.ps1 -Recipe src/TianWen.UI.Abstractions/icons.recipe -Verify
#>
[CmdletBinding()]
param(
    [string] $Recipe = 'src/TianWen.UI.Abstractions/icons.recipe',

    # Bake to a temp file and compare instead of overwriting, exiting non-zero on any difference.
    # For CI, and for checking a table has not been hand-edited.
    [switch] $Verify
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Recipe)) { throw "recipe not found: $Recipe" }
$recipeDir = Split-Path -Parent (Resolve-Path $Recipe)

$settings = @{}
$glyphs = [ordered]@{}
$known = @('font', 'output', 'namespace', 'sizes', 'levels', 'class', 'access', 'baker')

foreach ($line in Get-Content $Recipe) {
    # Strip trailing comments, then blanks. A '#' inside a value would be a codepoint, never a comment.
    $text = ($line -replace '#.*$', '').Trim()
    if ($text.Length -eq 0) { continue }

    $parts = $text -split '=', 2
    if ($parts.Count -ne 2) { throw "recipe line is not 'key = value': $line" }
    $key = $parts[0].Trim()
    $value = $parts[1].Trim()

    if ($known -contains $key.ToLowerInvariant()) {
        $settings[$key.ToLowerInvariant()] = $value
    }
    elseif ($value -match '^(U\+|0x)?[0-9A-Fa-f]+$') {
        # Anything whose value is a codepoint is a glyph, so adding one needs no change here.
        $glyphs[$key] = $value
    }
    else {
        throw "recipe key '$key' is neither a known setting nor a codepoint: $line"
    }
}

foreach ($required in 'font', 'output', 'namespace', 'baker') {
    if (-not $settings.ContainsKey($required)) { throw "recipe is missing '$required'" }
}
if ($glyphs.Count -eq 0) { throw 'recipe declares no glyphs' }

# Recipe paths are relative to the recipe, which is what makes it self-contained.
$fontPath = Join-Path $recipeDir $settings['font']
$outputPath = Join-Path $recipeDir $settings['output']
if (-not (Test-Path $fontPath)) { throw "font not found: $fontPath" }

$target = if ($Verify) { Join-Path ([System.IO.Path]::GetTempPath()) 'bake-icons-verify.g.cs' } else { $outputPath }

$toolArgs = @('--font', $fontPath, '--out', $target, '--namespace', $settings['namespace'])
foreach ($opt in 'sizes', 'levels', 'class', 'access') {
    if ($settings.ContainsKey($opt)) { $toolArgs += @("--$opt", $settings[$opt]) }
}
foreach ($name in $glyphs.Keys) { $toolArgs += @('--glyph', "$name=$($glyphs[$name])") }

Write-Host "baking $($glyphs.Count) glyphs from $(Split-Path -Leaf $fontPath) with baker $($settings['baker'])"
& dotnet dnx DIR.Lib.IconBaker --version $settings['baker'] --yes -- @toolArgs
if ($LASTEXITCODE -ne 0) { throw "bake-icons failed with exit code $LASTEXITCODE" }

# The repo's line endings (see the header): LF, whatever the baker wrote.
$baked = [System.IO.File]::ReadAllText($target).Replace("`r`n", "`n")

# Record WHICH recipe this table was baked from, as the SHA-256 of the recipe's bytes the way git stores
# them (LF), so the build's TWIC0001 check compares content rather than modification times. A time
# comparison fired for ever on a clone that pulled a recipe edit the table did not change (#792). The
# check hashes the checked-out file with MSBuild's GetFileHash, which is the same bytes on every host
# because the working copy is LF everywhere (.gitattributes).
$recipeBytes = [System.IO.File]::ReadAllText((Resolve-Path $Recipe)).Replace("`r`n", "`n")
$recipeHash = [System.Convert]::ToHexString(
    [System.Security.Cryptography.SHA256]::HashData([System.Text.UTF8Encoding]::new($false).GetBytes($recipeBytes))).ToLowerInvariant()
$marker = '// </auto-generated>'
$at = $baked.IndexOf($marker)
if ($at -lt 0) { throw "baked table has no '$marker' header to record the recipe hash in" }
$baked = $baked.Insert($at, "//     Recipe SHA-256: $recipeHash`n")
[System.IO.File]::WriteAllText($target, $baked, [System.Text.UTF8Encoding]::new($false))

if ($Verify) {
    if (-not (Test-Path $outputPath)) { throw "nothing to verify against: $outputPath is missing" }
    $checkedIn = [System.IO.File]::ReadAllText($outputPath).Replace("`r`n", "`n")
    if ($baked -ne $checkedIn) {
        Write-Error "$outputPath does not match its recipe. Run: pwsh tools/bake-icons.ps1"
        exit 1
    }
    Write-Host "verified: $(Split-Path -Leaf $outputPath) matches its recipe"
}
else {
    Write-Host "wrote $outputPath. Commit it: the table is checked in so a build needs no font and no network."
}
