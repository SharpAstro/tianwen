# Generates TianWen.local.slnx with sibling project references for local development.
# Called automatically by Directory.Build.targets when UseLocalSiblings is true.
param([string]$BaseSlnx, [string]$LocalSlnx)

# Guard against parallel MSBuild nodes all invoking this script concurrently
if (Test-Path $LocalSlnx) { return }

$siblings = @"
  <Folder Name="/Siblings/">
    <Project Path="../../DIR.Lib/src/DIR.Lib/DIR.Lib.csproj" />
    <Project Path="../../Console.Lib/src/Console.Lib/Console.Lib.csproj" />
    <Project Path="../../SdlVulkan.Renderer/src/SdlVulkan.Renderer/SdlVulkan.Renderer.csproj" />
  </Folder>
"@

$content = [IO.File]::ReadAllText($BaseSlnx)
$content = $content.Replace("</Solution>", "$siblings</Solution>")
[IO.File]::WriteAllText($LocalSlnx, $content)
Write-Host "Generated $LocalSlnx with sibling projects"
