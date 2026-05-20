#!/usr/bin/env bash
# Registers the TianWen MCP server with Claude Code.
# Mirrors register.ps1 for non-Windows hosts.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
binary="${1:-$script_dir/tianwen-mcp}"

if [[ ! -x "$binary" ]]; then
    echo "tianwen-mcp not found or not executable at: $binary" >&2
    echo "Run 'dotnet publish TianWen.AI.MCP -c Release -r <rid>' first, or pass the path as \$1." >&2
    exit 1
fi

echo "Registering 'tianwen' MCP server -> $binary"
claude mcp add tianwen "$binary"
echo "Registered. Restart Claude Code to load the server."
