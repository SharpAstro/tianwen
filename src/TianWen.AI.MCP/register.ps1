<#
.SYNOPSIS
    Registers the TianWen MCP server with Claude Code.
.DESCRIPTION
    Calls `claude mcp add` pointing at the AOT-published tianwen-mcp.exe alongside
    this script. Assumes the binary lives in the same directory as the script
    (i.e. you ran this from the publish output dir). For dev iterations against
    `bin/Release/net10.0/win-arm64/publish/tianwen-mcp.exe` pass --binary-path.
.PARAMETER BinaryPath
    Override path to tianwen-mcp.exe. Defaults to next to this script.
.EXAMPLE
    ./register.ps1
.EXAMPLE
    ./register.ps1 -BinaryPath "C:\src\tianwen\src\TianWen.AI.MCP\bin\Release\net10.0\win-arm64\publish\tianwen-mcp.exe"
#>
param(
    [string]$BinaryPath = (Join-Path $PSScriptRoot 'tianwen-mcp.exe')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $BinaryPath)) {
    Write-Error "tianwen-mcp.exe not found at: $BinaryPath. Run 'dotnet publish TianWen.AI.MCP -c Release -r win-arm64' first, or pass -BinaryPath."
    return
}

Write-Host "Registering 'tianwen' MCP server -> $BinaryPath" -ForegroundColor Cyan
& claude mcp add tianwen $BinaryPath
if ($LASTEXITCODE -ne 0) {
    Write-Error "claude mcp add returned exit code $LASTEXITCODE"
    return
}

Write-Host "Registered. Restart Claude Code to load the server." -ForegroundColor Green
