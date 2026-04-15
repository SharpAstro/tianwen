#!/usr/bin/env pwsh
# Generates TianWen.local.slnx at the repo root with sibling project references,
# then opens it in Visual Studio. This gives Go To Definition into DIR.Lib,
# Console.Lib, and SdlVulkan.Renderer source code.
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
    <Project Path="../Console.Lib/src/Console.Lib/Console.Lib.csproj" />
    <Project Path="../SdlVulkan.Renderer/src/SdlVulkan.Renderer/SdlVulkan.Renderer.csproj" />
  </Folder>
"@

$content = $content -replace '</Solution>', "$siblings</Solution>"

[IO.File]::WriteAllText($localSlnx, $content)
Write-Host "Generated $localSlnx"

# Open in Visual Studio
Start-Process $localSlnx
