<#
.SYNOPSIS
    Packs a published tianwen-fits tree into an unsigned MSIX.

.DESCRIPTION
    The package is deliberately left unsigned: it is submitted to the Microsoft Store, and the Store
    re-signs it after certification. That is the whole reason this app is packaged as MSIX rather
    than shipped as a bare tarball, and it means no certificate is bought, stored or rotated.

    The consequence is that the output of this script cannot be installed by double-clicking it. An
    unsigned MSIX is a hard refusal, not a warning, and there is no "run anyway". To test locally you
    need Developer Mode and Add-AppxPackage -AllowUnsigned; see README.md.

.EXAMPLE
    dotnet publish src/TianWen.UI.FitsViewer/TianWen.UI.FitsViewer.csproj -r win-arm64 -c Release
    ./packaging/windows/msix/build-msix.ps1 `
        -PublishDir src/TianWen.UI.FitsViewer/bin/Release/net10.0/win-arm64/publish `
        -Arch arm64 -Version 6.2.1315.0 -OutFile artifacts/AstroPhotoViewer-arm64.msix

.EXAMPLE
    # Cheap correctness check with no publish and no packing: what CI runs on every push.
    ./packaging/windows/msix/build-msix.ps1 -ValidateOnly
#>
[CmdletBinding(DefaultParameterSetName = 'Pack')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Pack')][string]$PublishDir,
    [Parameter(Mandatory, ParameterSetName = 'Pack')][ValidateSet('x64', 'arm64')][string]$Arch,
    [Parameter(Mandatory, ParameterSetName = 'Pack')][string]$Version,
    [Parameter(Mandatory, ParameterSetName = 'Pack')][string]$OutFile,

    [Parameter(Mandatory, ParameterSetName = 'Validate')][switch]$ValidateOnly,

    # Bundle mode: combine the per-architecture .msix files in a directory into one .msixbundle,
    # which is what Partner Center takes for a multi-arch submission. Here rather than in a second
    # script so makeappx is resolved (and pinned) in exactly one place.
    [Parameter(Mandatory, ParameterSetName = 'Bundle')][string]$BundleDir,
    [Parameter(Mandatory, ParameterSetName = 'Bundle')][string]$BundleVersion,
    [Parameter(Mandatory, ParameterSetName = 'Bundle')][string]$BundleOut,

    # Pinned, for the same reason pdf-viewer pins WiX: a build tool whose version is decided by
    # whatever the runner image ships is a build that changes under you without a commit.
    [string]$SdkBuildToolsVersion = '10.0.26100.8249',
    # Not $env:LOCALAPPDATA directly: it is null on a Linux runner, where Join-Path then throws
    # during parameter binding -- before any code runs, and -ValidateOnly is meant to run there.
    [string]$ToolCache = (Join-Path ($env:LOCALAPPDATA ?? [IO.Path]::GetTempPath()) 'SharpAstro/msix-tools')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$manifestTemplate = Join-Path $here 'AppxManifest.xml'
$assetsDir = Join-Path $here 'Assets'

function Assert-Version {
    param([string]$V)
    # Three rules, all of which produce an unhelpful error much later if broken: makeappx rejects a
    # malformed quad with a schema complaint that does not name the version, and Partner Center
    # rejects a non-zero revision at upload time, long after CI went green.
    $parts = $V -split '\.'
    if ($parts.Count -ne 4) {
        throw "Version must have four parts (Major.Minor.Build.Revision), got '$V'."
    }
    foreach ($p in $parts) {
        if ($p -notmatch '^\d+$') { throw "Version part '$p' is not a number in '$V'." }
        if ([int64]$p -gt 65535) { throw "Version part '$p' exceeds the MSIX maximum of 65535 in '$V'." }
    }
    if ([int]$parts[3] -ne 0) {
        throw "Version revision must be 0 in '$V'; the Store reserves the fourth part for its own use."
    }
}

function Get-PngSize {
    param([string]$Path)
    # PNG IHDR: 8 byte signature, then a 4 byte length and the 'IHDR' tag, then width and height as
    # big endian uint32 at offsets 16 and 20.
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 24) { throw "Not a PNG (too short): $Path" }
    $w = [int]$bytes[16] * 16777216 + [int]$bytes[17] * 65536 + [int]$bytes[18] * 256 + [int]$bytes[19]
    $h = [int]$bytes[20] * 16777216 + [int]$bytes[21] * 65536 + [int]$bytes[22] * 256 + [int]$bytes[23]
    return @{ Width = $w; Height = $h }
}

function Test-ManifestAssets {
    # Every image the manifest names must exist, and the square logos must actually be square at the
    # size their name claims. A renamed or mis-sized asset is accepted by makeappx and rejected by
    # Store certification, which is the slowest possible place to find out.
    [xml]$xml = Get-Content -Raw -LiteralPath $manifestTemplate
    $referenced = @($xml.SelectNodes('//@*') | Where-Object { $_.Value -like 'Assets\*.png' } | ForEach-Object { $_.Value })
    $referenced += @($xml.GetElementsByTagName('Logo') | ForEach-Object { $_.InnerText })
    $referenced += @($xml.GetElementsByTagName('uap:Logo') | ForEach-Object { $_.InnerText })
    $referenced = $referenced | Where-Object { $_ } | Sort-Object -Unique

    if ($referenced.Count -eq 0) { throw 'The manifest references no assets, which cannot be right.' }

    $problems = @()
    foreach ($rel in $referenced) {
        $path = Join-Path $here $rel.Replace('\', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $path)) { $problems += "missing: $rel"; continue }
        $size = Get-PngSize $path
        # Nominal size is in the file name for the square logos (SquareWxH), so it is checkable.
        if ((Split-Path -Leaf $rel) -match '^Square(\d+)x(\d+)Logo\.png$') {
            $want = [int]$Matches[1]
            if ($size.Width -ne $want -or $size.Height -ne $want) {
                $problems += ("$rel is {0}x{1}, expected {2}x{2}" -f $size.Width, $size.Height, $want)
            }
        }
        Write-Host ("  ok  {0,-34} {1}x{2}" -f $rel, $size.Width, $size.Height)
    }
    if ($problems.Count) { throw ("Asset problems:`n  " + ($problems -join "`n  ")) }
}

function Resolve-MakeAppx {
    $pkgDir = Join-Path $ToolCache "microsoft.windows.sdk.buildtools.$SdkBuildToolsVersion"
    if (-not (Test-Path -LiteralPath $pkgDir)) {
        New-Item -ItemType Directory -Force -Path $ToolCache | Out-Null
        $nupkg = Join-Path $ToolCache "sdkbt.$SdkBuildToolsVersion.zip"
        $url = "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$SdkBuildToolsVersion/microsoft.windows.sdk.buildtools.$SdkBuildToolsVersion.nupkg"
        Write-Host "Fetching Windows SDK build tools $SdkBuildToolsVersion"
        Invoke-WebRequest -Uri $url -OutFile $nupkg
        Expand-Archive -LiteralPath $nupkg -DestinationPath $pkgDir -Force
        Remove-Item -LiteralPath $nupkg -Force
    }
    # The bin folder carries the SDK version, which is NOT the package version (the package is
    # 10.0.26100.8249 and the folder 10.0.26100.0), so this globs rather than computing the path.
    # Prefer the host architecture: an x64 tool runs on arm64 under emulation, but slowly.
    $hostArch = if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    $candidates = @(
        Get-ChildItem -Path $pkgDir -Recurse -Filter 'makeappx.exe' -File |
            Sort-Object { if ($_.Directory.Name -eq $hostArch) { 0 } elseif ($_.Directory.Name -eq 'x64') { 1 } else { 2 } }
    )
    if (-not $candidates) { throw "makeappx.exe not found under $pkgDir" }
    return $candidates[0].FullName
}

# ---------------------------------------------------------------------------------------------

if ($PSCmdlet.ParameterSetName -eq 'Bundle') {
    Assert-Version $BundleVersion
    $packages = @(Get-ChildItem -LiteralPath $BundleDir -Filter '*.msix' -File)
    if ($packages.Count -eq 0) { throw "No .msix files in $BundleDir to bundle." }
    Write-Host ("Bundling {0} package(s): {1}" -f $packages.Count, ($packages.Name -join ', '))

    $outDir = Split-Path -Parent $BundleOut
    if ($outDir -and -not (Test-Path -LiteralPath $outDir)) {
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    }

    $makeappx = Resolve-MakeAppx
    & $makeappx bundle /d $BundleDir /p $BundleOut /bv $BundleVersion /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx bundle failed with exit code $LASTEXITCODE" }

    $mb = [math]::Round((Get-Item -LiteralPath $BundleOut).Length / 1MB, 1)
    Write-Host "Built $BundleOut ($mb MB, $BundleVersion, unsigned)"
    exit 0
}

Write-Host 'Checking manifest assets'
Test-ManifestAssets

if ($ValidateOnly) {
    # Prove the version rules are enforced, using the shapes that have actually caused trouble,
    # so this lane fails when the checks themselves rot rather than only when a real build breaks.
    foreach ($bad in @('6.2.1315', '6.2.1315.1', '6.2.70000.0')) {
        try { Assert-Version $bad; throw "Assert-Version accepted '$bad', which it must not." }
        catch { if ($_.Exception.Message -like '*must not*') { throw } }
    }
    Assert-Version '6.2.1315.0'
    [xml]$null = Get-Content -Raw -LiteralPath $manifestTemplate   # well formed XML
    Write-Host 'Manifest and assets validate.'
    exit 0
}

Assert-Version $Version

if (-not (Test-Path -LiteralPath $PublishDir)) { throw "PublishDir not found: $PublishDir" }
$exe = Join-Path $PublishDir 'tianwen-fits.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "tianwen-fits.exe not found in $PublishDir" }

$stage = Join-Path ([IO.Path]::GetTempPath()) ("msix-" + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    # Harvest the tree whole rather than naming files, so a newly bundled font or native ships
    # without anyone remembering to update this script. Symbols are dropped because nothing on a
    # user's machine reads them and they are a large share of the payload.
    Write-Host "Staging $PublishDir"
    Copy-Item -Path (Join-Path $PublishDir '*') -Destination $stage -Recurse -Force
    # .lib is a link-time import library: it is what an unmanaged COMPILER links against and is
    # dead weight in a package. onnxruntime.lib was shipping at 3 KB purely because it sits in the
    # publish tree next to the DLL it describes.
    Get-ChildItem -Path $stage -Recurse -Include '*.pdb', '*.lib' -File | Remove-Item -Force

    Copy-Item -Path $assetsDir -Destination (Join-Path $stage 'Assets') -Recurse -Force

    (Get-Content -Raw -LiteralPath $manifestTemplate).
        Replace('$VERSION$', $Version).
        Replace('$ARCH$', $Arch) |
        Set-Content -LiteralPath (Join-Path $stage 'AppxManifest.xml') -Encoding utf8 -NoNewline

    $outDir = Split-Path -Parent $OutFile
    if ($outDir -and -not (Test-Path -LiteralPath $outDir)) {
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    }

    $makeappx = Resolve-MakeAppx
    Write-Host "Packing with $makeappx"
    & $makeappx pack /d $stage /p $OutFile /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE" }

    $mb = [math]::Round((Get-Item -LiteralPath $OutFile).Length / 1MB, 1)
    Write-Host "Built $OutFile ($mb MB, $Arch, $Version, unsigned)"
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
