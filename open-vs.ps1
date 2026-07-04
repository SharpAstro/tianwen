#!/usr/bin/env pwsh
# Generates TianWen.local.slnx at the repo root with sibling project references,
# then opens it in Visual Studio. This gives Go To Definition into every sibling
# that Directory.Build.props' UseLocalSiblings switch project-references (DIR.Lib,
# Console.Lib, SdlVulkan.Renderer, the StbImageSharp family, QHYCCD.SDK, FITS.Lib,
# SER.Lib, Lzip.Lib). Keep this list in sync with Directory.Build.props.
#
# The base TianWen.slnx in src/ stays untouched (used by CI and dotnet build).

$repoRoot = $PSScriptRoot
$baseSlnx = Join-Path $repoRoot 'src' 'TianWen.slnx'
$localSlnx = Join-Path $repoRoot 'TianWen.local.slnx'

# Read base and re-root paths from src/ to repo root
$content = Get-Content $baseSlnx -Raw

# Re-root <Project Path="X"> to <Project Path="src/X">
$content = $content -replace '<Project Path="', '<Project Path="src/'

# Re-root <File Path="X"> entries (solution items) and normalize ../X to X
$content = $content -replace '<File Path="', '<File Path="src/'
$content = $content -replace 'Path="src/\.\./', 'Path="'

# Add sibling projects
$siblings = @"
  <Folder Name="/Siblings/">
    <Project Path="../DIR.Lib/src/DIR.Lib/DIR.Lib.csproj" />
    <!-- Fonts.Lib is transitive via DIR.Lib's own UseLocalFontsLib switch, but VS
         needs it explicitly in the solution to resolve DIR.Lib's MathLayout code
         (SharpAstro.Fonts.Tables.OpenTypeMath) against source instead of the older
         SharpAstro.Fonts NuGet package. -->
    <Project Path="../Fonts.Lib/src/SharpAstro.Fonts/SharpAstro.Fonts.csproj" />
    <Project Path="../Console.Lib/src/Console.Lib/Console.Lib.csproj" />
    <Project Path="../SdlVulkan.Renderer/src/SdlVulkan.Renderer/SdlVulkan.Renderer.csproj" />
    <Project Path="../StbImageSharp/src/StbImageSharp/StbImageSharp.csproj" />
    <Project Path="../StbImageSharp/src/SharpAstro.Tiff/SharpAstro.Tiff.csproj" />
    <Project Path="../StbImageSharp/src/SharpAstro.Exif/SharpAstro.Exif.csproj" />
    <Project Path="../StbImageSharp/src/SharpAstro.Png/SharpAstro.Png.csproj" />
    <Project Path="../StbImageSharp/src/SharpAstro.Color.Icc/SharpAstro.Color.Icc.csproj" />
    <Project Path="../StbImageSharp/src/SharpAstro.Jxr/SharpAstro.Jxr.csproj" />
    <Project Path="../StbImageSharp/src/SharpAstro.Jpeg.IccInjector/SharpAstro.Jpeg.IccInjector.csproj" />
    <Project Path="../StbImageSharp/src/SharpAstro.Exr/SharpAstro.Exr.csproj" />
    <Project Path="../QHYCCD.SDK/QHYCCD.SDK.csproj" />
    <Project Path="../FITS.Lib/CSharpFITS/CSharpFITS.csproj" />
    <Project Path="../SER.Lib/src/SER.Lib/SER.Lib.csproj" />
    <Project Path="../Lzip.Lib/src/Lzip.Lib/Lzip.Lib.csproj" />
  </Folder>
"@

$content = $content -replace '</Solution>', "$siblings</Solution>"

[IO.File]::WriteAllText($localSlnx, $content)
Write-Host "Generated $localSlnx"

# Open in Visual Studio
Start-Process $localSlnx
