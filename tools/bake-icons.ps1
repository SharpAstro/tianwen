<#
.SYNOPSIS
    Regenerates a baked-icon table from a .recipe manifest.

.DESCRIPTION
    The manifest is DATA, not this script: see src/TianWen.UI.Abstractions/icons.recipe, which carries
    the font, the output path, the namespace and the glyph list with a note on what each is for. Keeping
    it out of here is what lets the build watch it (warning TWIC0001 fires when the recipe is newer than
    the generated table) and what would let a generator read the same file later.

    The baking itself is DIR.Lib.IconBaker, run via `dnx` straight from nuget.org, so there is nothing to
    build first and no copy of the generator in this repo.

    The bake is byte-reproducible: ManagedFontRasterizer is pure managed (no FreeType, no DirectWrite),
    so the same recipe and font give identical output on any host. That is what makes a CI step which
    re-bakes and COMPARES possible, which is a stronger check than a timestamp: a timestamp catches a
    forgotten re-bake, a comparison also catches a hand-edited table.

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

if ($Verify) {
    if (-not (Test-Path $outputPath)) { throw "nothing to verify against: $outputPath is missing" }
    $a = Get-FileHash $target -Algorithm SHA256
    $b = Get-FileHash $outputPath -Algorithm SHA256
    if ($a.Hash -ne $b.Hash) {
        Write-Error "$outputPath does not match its recipe. Run: pwsh tools/bake-icons.ps1"
        exit 1
    }
    Write-Host "verified: $(Split-Path -Leaf $outputPath) matches its recipe"
}
else {
    Write-Host "wrote $outputPath. Commit it: the table is checked in so a build needs no font and no network."
}
