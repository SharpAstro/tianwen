#!/usr/bin/env pwsh
# Generates TianWen.local.slnx at the repo root with sibling project references,
# then opens it in Visual Studio. This gives Go To Definition into every sibling
# that Directory.Build.props' UseLocalSiblings switch project-references.
#
# THIS LIST MUST MATCH THAT SWITCH'S Exists(...) CONJUNCTION. It had drifted badly,
# in a way nothing could catch: the codec projects pointed at ../StbImageSharp, a repo
# that no longer exists (it is ../Codecs now), so seven of the entries below resolved
# to nothing and VS silently loaded an unloadable-project solution. LAN.Lib,
# SharpAstro.Codecs and WebGl.Renderer were simply missing. A generated file is not
# self-checking, so re-read Directory.Build.props when you touch either one.
#
# NB: the stb_image port itself (StbImageSharp.csproj) is deliberately not listed.
# TianWen consumes only the SharpAstro.* codecs, not the port.
#
# The base TianWen.slnx in src/ stays untouched (used by CI and dotnet build). It
# INCLUDES TianWen.UI.Web, so this generated solution picks it up by re-rooting -- which
# is what makes a rename in TianWen.UI.Abstractions fail on the .razor call sites that
# consume it, locally, instead of in CI. It still omits TianWen.UI.Web.E2E on purpose:
# that suite needs a browser plus a running dev server, so it must not be swept up by a
# solution-wide `dotnet test`.

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
    <!-- The WebGL2 backend for the same DIR.Lib Renderer. Its only consumer,
         TianWen.UI.Web, is not in this solution, but the project is here so a
         sibling-wide refactor can see and rebuild it. -->
    <Project Path="../WebGl.Renderer/src/WebGl.Renderer/WebGl.Renderer.csproj" />
    <!-- SharpAstro.* codec projects from the Codecs repo. The stb_image port
         (StbImageSharp.csproj) is intentionally omitted: nothing in the
         solution references it (Fonts decodes CBDT PNGs via SharpAstro.Png). -->
    <Project Path="../Codecs/src/SharpAstro.Tiff/SharpAstro.Tiff.csproj" />
    <Project Path="../Codecs/src/SharpAstro.Exif/SharpAstro.Exif.csproj" />
    <Project Path="../Codecs/src/SharpAstro.Png/SharpAstro.Png.csproj" />
    <Project Path="../Codecs/src/SharpAstro.Color.Icc/SharpAstro.Color.Icc.csproj" />
    <Project Path="../Codecs/src/SharpAstro.Jxr/SharpAstro.Jxr.csproj" />
    <Project Path="../Codecs/src/SharpAstro.Jpeg.IccInjector/SharpAstro.Jpeg.IccInjector.csproj" />
    <Project Path="../Codecs/src/SharpAstro.Exr/SharpAstro.Exr.csproj" />
    <Project Path="../Codecs/src/SharpAstro.Codecs/SharpAstro.Codecs.csproj" />
    <Project Path="../QHYCCD.SDK/QHYCCD.SDK.csproj" />
    <Project Path="../FITS.Lib/CSharpFITS/CSharpFITS.csproj" />
    <Project Path="../SER.Lib/src/SER.Lib/SER.Lib.csproj" />
    <Project Path="../Lzip.Lib/src/Lzip.Lib/Lzip.Lib.csproj" />
    <Project Path="../LAN.Lib/src/LAN.Lib/LAN.Lib.csproj" />
    <Project Path="../AppShell/src/SharpAstro.AppShell/SharpAstro.AppShell.csproj" />
  </Folder>
"@

$content = $content -replace '</Solution>', "$siblings</Solution>"

[IO.File]::WriteAllText($localSlnx, $content)
Write-Host "Generated $localSlnx"

# Open in Visual Studio
Start-Process $localSlnx
