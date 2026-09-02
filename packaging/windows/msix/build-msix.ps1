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

    # Sign mode: put a SELF-SIGNED signature on an already-built .msix/.msixbundle so it can be
    # installed on this machine. Nothing to do with shipping -- the Store re-signs on
    # certification and this signature is discarded -- but it is the only way to install one
    # locally, and -AllowUnsigned is NOT the way (see the README: that flag requires the
    # publisher to sit in the unsigned namespace, which a Store identity by definition does not,
    # so it fails 0x80073D2C no matter how much Developer Mode is on).
    [Parameter(Mandatory, ParameterSetName = 'Sign')][string]$SignPackage,

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

function Test-ManifestHandlers {
    # The Explorer thumbnail handler is ONE guid written in TWO places (desktop2:ThumbnailHandler/@Clsid
    # and com:Class/@Id) plus a DLL the com:Class names by package-relative path. A mismatch installs a
    # package whose thumbnails silently never appear, and a missing DLL does the same, so both are
    # checked here: the guid pair on every push, the DLL whenever there is a publish tree to look in.
    param([string]$PublishDir)

    [xml]$xml = Get-Content -Raw -LiteralPath $manifestTemplate
    $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace('com', 'http://schemas.microsoft.com/appx/manifest/com/windows10')
    $ns.AddNamespace('desktop2', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/2')

    $classes = @($xml.SelectNodes('//com:SurrogateServer/com:Class', $ns))
    $handlers = @($xml.SelectNodes('//desktop2:ThumbnailHandler', $ns))
    if ($handlers.Count -eq 0) { throw 'The manifest declares no desktop2:ThumbnailHandler; Explorer thumbnails are a shipped feature.' }

    foreach ($handler in $handlers) {
        $clsid = $handler.GetAttribute('Clsid')
        $class = $classes | Where-Object { $_.GetAttribute('Id') -ieq $clsid } | Select-Object -First 1
        if (-not $class) { throw "desktop2:ThumbnailHandler Clsid $clsid has no com:SurrogateServer/com:Class with that Id." }
        Write-Host ("  ok  ThumbnailHandler {0} -> {1}" -f $clsid, $class.GetAttribute('Path'))
    }

    foreach ($class in $classes) {
        $rel = $class.GetAttribute('Path')
        if ($class.GetAttribute('ThreadingModel') -ne 'Both') {
            throw "com:Class $rel must declare ThreadingModel=Both; the shell's surrogate activates handlers from either apartment."
        }
        if ($PublishDir) {
            $path = Join-Path $PublishDir $rel
            if (-not (Test-Path -LiteralPath $path)) {
                throw "The manifest names $rel but $PublishDir has no such file. Publish TianWen.Shell.Thumbnails for this architecture and copy the DLL into the viewer's publish tree (CI does this in publish-apps)."
            }
            Write-Host ("  ok  {0} ({1:N0} bytes)" -f $rel, (Get-Item -LiteralPath $path).Length)
        }
    }
}

function Resolve-SdkTool {
    # makeappx for packing, signtool for the local-test signature: one pinned package serves
    # both, so a second tool cannot drift onto a second version.
    param([Parameter(Mandatory)][string]$Name)
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
        Get-ChildItem -Path $pkgDir -Recurse -Filter $Name -File |
            Sort-Object { if ($_.Directory.Name -eq $hostArch) { 0 } elseif ($_.Directory.Name -eq 'x64') { 1 } else { 2 } }
    )
    if (-not $candidates) { throw "$Name not found under $pkgDir" }
    return $candidates[0].FullName
}

function Get-PackagePublisher {
    # Read the publisher out of the ARTIFACT, never out of AppxManifest.xml beside this script. The
    # entire failure class here is a subject that does not match the package's publisher byte for
    # byte -- signtool rejects a mismatch, and a template that has moved on since the package was
    # built would reintroduce exactly that. A .msix/.msixbundle is a zip; XmlDocument.Load respects
    # whichever encoding the manifest declares, which differs between the two manifest kinds.
    param([Parameter(Mandatory)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.Entries | Where-Object {
            $_.FullName -eq 'AppxManifest.xml' -or $_.FullName -eq 'AppxMetadata/AppxBundleManifest.xml'
        } | Select-Object -First 1
        if (-not $entry) { throw "No AppxManifest.xml or AppxBundleManifest.xml inside $Path" }

        $stream = $entry.Open()
        try {
            $xml = New-Object System.Xml.XmlDocument
            $xml.Load($stream)
        } finally { $stream.Dispose() }

        $identity = $xml.DocumentElement.SelectSingleNode("*[local-name()='Identity']")
        if (-not $identity) { throw "No Identity element in $($entry.FullName)" }
        return [pscustomobject]@{
            Name      = $identity.GetAttribute('Name')
            Publisher = $identity.GetAttribute('Publisher')
            Version   = $identity.GetAttribute('Version')
        }
    } finally { $zip.Dispose() }
}

# ---------------------------------------------------------------------------------------------

if ($PSCmdlet.ParameterSetName -eq 'Sign') {
    if (-not (Test-Path -LiteralPath $SignPackage)) { throw "No such package: $SignPackage" }
    $SignPackage = (Resolve-Path -LiteralPath $SignPackage).Path

    $identity = Get-PackagePublisher -Path $SignPackage
    Write-Host "Package  $($identity.Name) $($identity.Version)"
    Write-Host "Publisher $($identity.Publisher)"

    # Reuse a matching certificate rather than minting one per run: every new certificate has to be
    # trusted again by hand (an elevated step), so a script that made one each time would turn a
    # one-off into a per-build chore.
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $identity.Publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1

    if ($cert) {
        Write-Host "Reusing certificate $($cert.Thumbprint) (expires $($cert.NotAfter.ToString('yyyy-MM-dd')))"
    } else {
        Write-Host 'Creating a self-signed code-signing certificate'
        # Basic Constraints must say end-entity and the EKU must be code signing (1.3.6.1.5.5.7.3.3):
        # without both, signtool signs happily and deployment then rejects the signature, which reads
        # as a broken package rather than a broken certificate.
        $cert = New-SelfSignedCertificate `
            -Type Custom -Subject $identity.Publisher `
            -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 2048 `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -FriendlyName "$($identity.Name) local test signing" `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
        Write-Host "Created $($cert.Thumbprint)"
    }

    # /sha1 picks the certificate straight out of the store, so no .pfx and no password ever exist
    # on disk. /fd SHA256 because SHA-1 file digests are refused by deployment.
    $signtool = Resolve-SdkTool 'signtool.exe'
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $SignPackage
    if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }

    # The PUBLIC half is what has to be trusted, and exporting it needs no elevation; importing it
    # into the machine store does. Per-user Trusted People is not consulted by app deployment, so
    # Cert:\CurrentUser\TrustedPeople is not a way around the elevated step.
    $cerPath = [IO.Path]::ChangeExtension($SignPackage, '.cer')
    Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null

    Write-Host ''
    Write-Host "Signed $SignPackage"
    Write-Host "Certificate exported to $cerPath"
    Write-Host ''
    Write-Host 'Trust it once, from an ELEVATED prompt:'
    Write-Host "  Import-Certificate -FilePath '$cerPath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    Write-Host ''
    Write-Host 'Then install (no elevation, and no -AllowUnsigned -- it is signed now):'
    Write-Host "  Add-AppxPackage '$SignPackage'"
    exit 0
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

    $makeappx = Resolve-SdkTool 'makeappx.exe'
    & $makeappx bundle /d $BundleDir /p $BundleOut /bv $BundleVersion /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx bundle failed with exit code $LASTEXITCODE" }

    $mb = [math]::Round((Get-Item -LiteralPath $BundleOut).Length / 1MB, 1)
    Write-Host "Built $BundleOut ($mb MB, $BundleVersion, unsigned)"
    exit 0
}

Write-Host 'Checking manifest assets'
Test-ManifestAssets
Write-Host 'Checking manifest shell handlers'
Test-ManifestHandlers

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
Test-ManifestHandlers -PublishDir $PublishDir

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

    # Build the resource index BEFORE packing. Without a resources.pri in the package, Windows
    # cannot resolve a single qualifier, so every scale-*, targetsize-* and altform-unplated asset
    # is inert payload and the shell uses only the literal path the manifest names -- which is the
    # 44x44 Square44x44Logo.png. That is why the file-type icon drew at 44px in a 256px cell while
    # a targetsize-256 sat right beside it in the package, and why ADDING assets changed nothing.
    # Nothing warns about this: makeappx packs happily, the package installs, the app runs, and the
    # only symptom is icons at the wrong size.
    $makepri = Resolve-SdkTool 'makepri.exe'
    $priConfig = Join-Path ([IO.Path]::GetDirectoryName($stage)) ("priconfig-" + [IO.Path]::GetFileName($stage) + ".xml")
    try {
        & $makepri createconfig /cf $priConfig /dq en-US /o | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "makepri createconfig failed with exit code $LASTEXITCODE" }

        # Drop the <packaging> block, which by default carries autoResourcePackage entries for
        # Language, Scale and DXFeatureLevel. Those split the scale variants out into a SEPARATE
        # resources.scale-200.pri intended for a resource package in a bundle -- ship the main .pri
        # alone, as this lane does, and the split-out assets become unresolvable again. Removing the
        # element rather than emptying it also avoids PRI230, which an empty node warns about.
        $cfg = [xml](Get-Content -Raw -LiteralPath $priConfig)
        $packaging = $cfg.resources.SelectSingleNode('packaging')
        if ($packaging) { $cfg.resources.RemoveChild($packaging) | Out-Null }
        $cfg.Save($priConfig)

        # /pr is the project root, so it must be the stage: makepri reads AppxManifest.xml from there
        # to learn which resources are referenced, then indexes candidates by filename qualifier.
        & $makepri new /pr $stage /cf $priConfig /of (Join-Path $stage 'resources.pri') /o | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "makepri new failed with exit code $LASTEXITCODE" }
    } finally {
        # Outside the stage on purpose: a config file inside it would be packed into the msix.
        if (Test-Path -LiteralPath $priConfig) { Remove-Item -LiteralPath $priConfig -Force }
    }

    $pri = Join-Path $stage 'resources.pri'
    if (-not (Test-Path -LiteralPath $pri)) { throw 'makepri reported success but wrote no resources.pri' }
    $split = @(Get-ChildItem -LiteralPath $stage -Filter 'resources.*.pri' -File)
    if ($split.Count) { throw ("makepri split resources into {0}; the packaging config was not stripped" -f ($split.Name -join ', ')) }
    Write-Host ("Indexed resources.pri ({0} bytes)" -f (Get-Item -LiteralPath $pri).Length)

    $makeappx = Resolve-SdkTool 'makeappx.exe'
    Write-Host "Packing with $makeappx"
    & $makeappx pack /d $stage /p $OutFile /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE" }

    $mb = [math]::Round((Get-Item -LiteralPath $OutFile).Length / 1MB, 1)
    Write-Host "Built $OutFile ($mb MB, $Arch, $Version, unsigned)"
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
