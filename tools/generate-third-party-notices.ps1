# Regenerates THIRD-PARTY-NOTICES.txt at the repository root.
#
# WHY THIS EXISTS
#
# The four release binaries are AOT-published, so every dependency is statically linked into the
# executable we hand people. MIT, Apache-2.0 and BSD all require their licence text and copyright
# notice to accompany that redistribution, and until now the .tar.gz assets carried no licence text
# at all -- not even TianWen's own. NOTICE covers methods, data and the external programs we invoke
# as separate processes; it deliberately says nothing about the compiled-in package graph, which is
# what this file is for.
#
# HOW THE PACKAGE SET IS DERIVED
#
# From each publishable project's obj/project.assets.json, taking the resolved graph for the
# framework target -- not Directory.Packages.props, which lists central PINS including test-only
# tooling (xunit, NSubstitute, BenchmarkDotNet, Playwright) that is never in a shipped binary.
# Restore must have run; the script says so rather than emitting a short file.
#
# THE GRAPH IS PLATFORM-SENSITIVE, which drives where this runs.
#
# TianWen.AI picks its ONNX Runtime execution provider by host OS and RID: DirectML on Windows,
# Microsoft.ML.OnnxRuntime.Gpu.Linux for linux-x64, the plain CPU package otherwise. So a file
# generated on one platform names packages another does not link, AND omits ones it does. There is
# no single correct file, which is why:
#
#   - .github/workflows/dotnet.yml runs this in EVERY publish-apps matrix leg, before the publishes,
#     so each platform's .tar.gz carries notices for the graph actually linked into it. That is the
#     authoritative copy for anything we release.
#   - The committed THIRD-PARTY-NOTICES.txt is what a LOCAL publish uses, and it has to exist: the
#     Content items in src/Directory.Build.targets point at it, and a Content item whose file is
#     missing fails the publish. Regenerate it when the dependency graph moves.
#
# -Check exists for the local case and verifies only that the committed file is a SUPERSET of what
# resolved here. It is deliberately not an equality check, because on any other platform equality is
# unachievable by construction and would fail on principle rather than on a real problem.

[CmdletBinding()]
param(
    [string] $RepoRoot = (Split-Path $PSScriptRoot -Parent),
    [string] $OutFile,
    # Verify instead of writing. Fails when a package resolved HERE is missing from the committed
    # file, which is the property that actually matters: the file must never under-attribute.
    #
    # Deliberately NOT an equality check. The graph is RID-sensitive (DirectML on Windows, the
    # CUDA/Linux runtime package on Linux), so a file generated on one platform can never byte-match
    # what another platform resolves, and an equality check would fail every CI run on principle.
    # Superset holds on every platform and still catches the case this exists for: a dependency bump
    # that adds a package nobody re-generated for.
    [switch] $Check
)

$ErrorActionPreference = 'Stop'

if (-not $OutFile) { $OutFile = Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.txt' }
$src = Join-Path $RepoRoot 'src'
# Cross-platform: this runs on the ubuntu CI runner as well as here, so no USERPROFILE and no
# backslash separators anywhere below.
$homeDir = if ($env:USERPROFILE) { $env:USERPROFILE } else { $env:HOME }
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $homeDir '.nuget/packages' }

# The six projects that set PublishAot. Two of them (tianwen-mcp, tianwen-ascomhost) are built but
# not shipped as release assets; they are included anyway so the file stays correct if that changes,
# and because over-attributing is the safe direction.
$projects = @(
    'TianWen.Cli', 'TianWen.Server', 'TianWen.UI.Gui',
    'TianWen.UI.FitsViewer', 'TianWen.AI.MCP', 'TianWen.AscomHost'
)

# Packages predating SPDX expressions in nuspec carry only a licenceUrl, which a script cannot
# classify. Each entry here is a HAND VERIFICATION with the evidence that settled it, so a reviewer
# can re-check it rather than trust the table. Keyed on package id.
$legacyUrlLicenses = @{
    # nuspec has neither a <license> nor a copyright, and the package ships no licence file. Its own
    # bundled Readme.md carries an LGPLv3 badge (line 4), and the licenceUrl points at the repo's
    # LICENSE. LGPL-3.0 material may be incorporated into an AGPL-3.0 work by the same route NOTICE
    # sets out for ASTAP: LGPL-3.0 is GPL-3.0 plus additional permissions, those permissions may be
    # dropped to yield GPL-3.0, and AGPL-3.0 section 13 expressly permits combining GPL-3.0 material.
    # Static linking under AOT is not a problem here either: LGPL section 4 wants the user able to
    # relink, and an AGPL work hands them the complete Corresponding Source, which is strictly more.
    'LibUsbDotNet' = 'LGPL-3.0-or-later (verified from the package Readme badge and licence URL)'
}

$seen = @{}
$restored = 0
foreach ($p in $projects) {
    $assets = Join-Path $src "$p/obj/project.assets.json"
    if (-not (Test-Path $assets)) { continue }
    $restored++
    $json = Get-Content $assets -Raw | ConvertFrom-Json
    # The framework-only target ("net10.0"); RID-qualified ones ("net10.0/win-x64") repeat it.
    $target = $json.targets.PSObject.Properties | Where-Object { $_.Name -notmatch '/' } | Select-Object -First 1
    if (-not $target) { continue }
    foreach ($lib in ($target.Value.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' })) {
        $seen[$lib.Name] = $true
    }
}

if ($restored -eq 0) {
    throw "No project.assets.json found under $src. Run 'dotnet restore' first: the package set is read from the resolved graph, not from Directory.Packages.props."
}

function Get-PackageInfo([string] $key) {
    $id, $ver = $key -split '/', 2
    $dir = Join-Path $nugetRoot ("$id/$ver".ToLowerInvariant())
    $nuspec = Join-Path $dir "$($id.ToLowerInvariant()).nuspec"
    $info = [ordered]@{
        Id = $id; Version = $ver; Authors = ''; Copyright = ''; ProjectUrl = ''
        License = '<unknown>'; LicenseText = ''; LicenseSource = ''
    }
    if (-not (Test-Path $nuspec)) { return [pscustomobject]$info }

    [xml]$x = Get-Content $nuspec -Raw
    $m = $x.package.metadata
    $info.Authors = [string]$m.authors
    $info.Copyright = [string]$m.copyright
    $info.ProjectUrl = if ($m.projectUrl) { [string]$m.projectUrl } elseif ($m.repository.url) { [string]$m.repository.url } else { '' }

    $licNode = $m.license
    $licType = if ($licNode) { [string]$licNode.type } else { '' }
    $licVal = if ($licNode -is [string]) { [string]$licNode } else { [string]$licNode.'#text' }

    if ($licType -eq 'expression' -and $licVal) {
        $info.License = $licVal
    }
    elseif ($licType -eq 'file' -and $licVal) {
        # The nuspec path is package-relative and uses either slash convention.
        $rel = $licVal -replace '\\', '/'
        $path = Join-Path $dir $rel
        if (Test-Path $path) {
            $info.LicenseText = (Get-Content $path -Raw).TrimEnd()
            $info.LicenseSource = $rel
            $info.License = '(see bundled text)'
        }
        else {
            $info.License = "file:$licVal (not found in package)"
        }
    }
    elseif ($m.licenseUrl) {
        # Pre-SPDX packages carry only a URL, which a script cannot classify. Use the hand-verified
        # table if the package is in it, and otherwise surface the URL loudly so the gap is visible
        # rather than silently attributed.
        if ($legacyUrlLicenses.ContainsKey($id)) {
            $info.License = $legacyUrlLicenses[$id]
        }
        else {
            $info.License = "UNCLASSIFIED, see $([string]$m.licenseUrl)"
        }
    }
    return [pscustomobject]$info
}

$packages = foreach ($k in ($seen.Keys | Sort-Object)) { Get-PackageInfo $k }
$packages = $packages | Sort-Object Id, Version

# Group the bundled licence texts so each distinct text is printed once, with the packages that
# share it listed against it. Eleven packages ship a file and several of those files are identical
# (the same MIT or Apache text), so printing per package would trible the file for no added notice.
$texts = @{}
foreach ($p in $packages) {
    if (-not $p.LicenseText) { continue }
    $norm = ($p.LicenseText -replace "`r`n", "`n").Trim()
    if (-not $texts.ContainsKey($norm)) { $texts[$norm] = New-Object System.Collections.ArrayList }
    [void]$texts[$norm].Add("$($p.Id) $($p.Version)")
}

$mitTemplate = @'
Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
'@

$sb = New-Object System.Text.StringBuilder
function Add-Line([string] $s = '') { [void]$sb.AppendLine($s) }

Add-Line 'TianWen -- third-party notices for the distributed binaries'
Add-Line '=========================================================='
Add-Line ''
Add-Line 'GENERATED FILE. Do not edit by hand; run tools/generate-third-party-notices.ps1.'
Add-Line ''
Add-Line 'TianWen is offered under the GNU Affero General Public License version 3 or later'
Add-Line '(LICENSE), with one additional permission under section 7 covering the proprietary'
Add-Line 'hardware-vendor SDKs it must link to talk to cameras and mounts (LICENSE.EXCEPTION).'
Add-Line 'Attributions for methods, embedded data and the external programs TianWen invokes as'
Add-Line 'separate processes are in NOTICE. This file covers something different and narrower:'
Add-Line 'the third-party packages COMPILED INTO the released executables.'
Add-Line ''
Add-Line 'The release binaries are native-AOT published, so these are statically linked rather'
Add-Line 'than shipped as separate assemblies. MIT, Apache-2.0 and BSD each require their terms'
Add-Line 'and copyright notice to travel with that redistribution, which is what follows.'
Add-Line ''
Add-Line 'Scope note: the package graph is RID-sensitive at the edges, because the native ONNX'
Add-Line 'Runtime execution providers differ per platform. This is the UNION of what was resolved'
Add-Line 'locally, so it over-attributes rather than under-attributes. A package listed here may'
Add-Line 'not be present in every platform build.'
Add-Line ''
Add-Line ("Packages: {0}" -f $packages.Count)
Add-Line ''
Add-Line ''
Add-Line 'Inventory'
Add-Line '---------'
Add-Line ''

foreach ($p in $packages) {
    Add-Line ("  {0} {1}" -f $p.Id, $p.Version)
    Add-Line ("      licence   {0}" -f $p.License)
    if ($p.Copyright) { Add-Line ("      copyright {0}" -f $p.Copyright) }
    elseif ($p.Authors) { Add-Line ("      authors   {0}" -f $p.Authors) }
    if ($p.ProjectUrl) { Add-Line ("      project   {0}" -f $p.ProjectUrl) }
    Add-Line ''
}

Add-Line ''
Add-Line 'The MIT License'
Add-Line '---------------'
Add-Line ''
Add-Line 'Applies to every package above whose licence reads MIT. The copyright holder is the one'
Add-Line 'recorded against that package in the inventory.'
Add-Line ''
foreach ($line in ($mitTemplate -split "`r?`n")) { Add-Line $line }
Add-Line ''

if ($texts.Count -gt 0) {
    Add-Line ''
    Add-Line 'Licences bundled with individual packages'
    Add-Line '----------------------------------------'
    Add-Line ''
    Add-Line 'Reproduced verbatim. Where several packages ship an identical text it is printed once,'
    Add-Line 'with every package it applies to named above it.'
    Add-Line ''
    foreach ($key in ($texts.Keys | Sort-Object { $texts[$_][0] })) {
        Add-Line ('=' * 78)
        foreach ($who in ($texts[$key] | Sort-Object)) { Add-Line ("  {0}" -f $who) }
        Add-Line ('=' * 78)
        Add-Line ''
        foreach ($line in ($key -split "`n")) { Add-Line $line.TrimEnd() }
        Add-Line ''
    }
}

$content = $sb.ToString() -replace "`r`n", "`n"

if ($Check) {
    if (-not (Test-Path $OutFile)) {
        Write-Error "$OutFile does not exist. Run tools/generate-third-party-notices.ps1 and commit the result."
        exit 1
    }
    $existing = (Get-Content $OutFile -Raw) -replace "`r`n", "`n"
    # Match on the inventory line, "  <Id> <Version>", so a package present at a DIFFERENT version
    # counts as missing. A bump is exactly when the copyright and licence may have changed, which is
    # the thing worth re-checking.
    $missing = @($packages | Where-Object { $existing -notmatch [regex]::Escape("  $($_.Id) $($_.Version)`n") })
    if ($missing.Count -gt 0) {
        Write-Host "Packages resolved here but absent from $($OutFile):"
        foreach ($m in $missing) { Write-Host ("  {0} {1}" -f $m.Id, $m.Version) }
        Write-Error "$OutFile is missing $($missing.Count) package(s). Run tools/generate-third-party-notices.ps1 and commit the result."
        exit 1
    }
    "THIRD-PARTY-NOTICES.txt covers all $($packages.Count) package(s) resolved on this platform."
    exit 0
}

# LF endings: this ships inside .tar.gz assets consumed on Linux and macOS as well as Windows.
[System.IO.File]::WriteAllText($OutFile, $content, (New-Object System.Text.UTF8Encoding($false)))
"Wrote $OutFile ($($packages.Count) packages, $($texts.Count) bundled licence text(s))."
