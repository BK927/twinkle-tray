[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')][string] $Architecture = 'x64',
    [string] $Version = '0.3.3',
    [ValidateRange(0, 65535)][int] $MsixRevision = 0,
    [string] $Publisher = 'CN=BK927',
    [string] $PublishDirectory,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\packages'),
    [switch] $SkipMsix,
    [string] $CertificatePath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Version -cnotmatch '^(?<base>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw 'Version must be a semantic version, such as 0.3.0 or 0.4.0-beta.1.'
}
$numericVersion = $Matches['base']
$prerelease = $Matches['pre']
if ($prerelease -and @($prerelease.Split('.') | Where-Object { $_ -match '^0[0-9]+$' }).Count -gt 0) { throw 'Numeric prerelease identifiers cannot have leading zeroes.' }
if (@($numericVersion.Split('.') | Where-Object { [decimal]$_ -gt 65535 }).Count -gt 0) { throw 'MSIX version components cannot exceed 65535.' }
$msixVersion = $numericVersion + '.' + $MsixRevision
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $PSScriptRoot ('artifacts\win-' + $Architecture.ToLowerInvariant()) }
$source = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $source 'TwinkleTray.WinUI.exe'))) { throw 'Publish the application with build.ps1 first.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($source.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package output must be outside the application publish directory.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$name = "TwinkleTray-Native-$Version-$Architecture"
$zip = Join-Path $output ($name + '.zip')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$deliverables = [Collections.Generic.List[string]]::new()
$stagingRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'TwinkleTray-Packaging'))
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
$stage = Join-Path $stagingRoot ([Guid]::NewGuid().ToString('N'))
$pendingMsix = $null
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    # Use the same filtered payload for both formats. Runtime smoke reports and
    # debugging symbols are build evidence, not part of the installed application.
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
        if ($file.Extension -ieq '.pdb' -or $file.Name -in @('smoke-test.json', 'automation-regression.json', 'ui-layout-test.json')) { continue }
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        if ($relative.StartsWith('test-fixtures' + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $destination = Join-Path $stage $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    $pendingZip = Join-Path $stagingRoot ($name + '-' + [Guid]::NewGuid().ToString('N') + '.zip')
    try {
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $pendingZip, [IO.Compression.CompressionLevel]::Optimal, $false)
        Move-Item -LiteralPath $pendingZip -Destination $zip -Force
    } finally { if (Test-Path -LiteralPath $pendingZip) { Remove-Item -LiteralPath $pendingZip -Force } }
    $deliverables.Add($zip)
    if (-not $SkipMsix) {
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $tools = Get-ChildItem -LiteralPath $sdkRoot -Directory | Where-Object { $_.Name -match '^10\.0\.' } | Sort-Object { [version]$_.Name } -Descending
    $sdk = $tools | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'x64\makeappx.exe') } | Select-Object -First 1
    if (-not $sdk) { throw 'The Windows SDK makeappx.exe tool is required for MSIX packaging.' }
    Add-Type -AssemblyName System.Drawing
    $logo = [Drawing.Image]::FromFile((Join-Path $source 'Assets\logo.png'))
    try {
        foreach ($size in @(44, 50, 150)) {
            $bitmap = [Drawing.Bitmap]::new($size, $size)
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.DrawImage($logo, 0, 0, $size, $size)
                $bitmap.Save((Join-Path $stage "Assets\Square$size.png"), [Drawing.Imaging.ImageFormat]::Png)
            } finally { $graphics.Dispose(); $bitmap.Dispose() }
        }
    } finally { $logo.Dispose() }
    $publisherXml = [Security.SecurityElement]::Escape($Publisher)
    $arch = $Architecture.ToLowerInvariant()
    @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
 xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
 IgnorableNamespaces="uap desktop rescap">
 <Identity Name="BK927.TwinkleTray.WinUI" Publisher="$publisherXml" Version="$msixVersion" ProcessorArchitecture="$arch" />
 <Properties><DisplayName>Twinkle Tray Native</DisplayName><PublisherDisplayName>BK927</PublisherDisplayName><Logo>Assets\Square50.png</Logo><Description>Twinkle Tray Native — community Windows brightness control built with WinUI 3</Description></Properties>
 <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
 <Resources><Resource Language="en-us" /></Resources>
 <Applications><Application Id="TwinkleTray" Executable="TwinkleTray.WinUI.exe" EntryPoint="Windows.FullTrustApplication">
  <uap:VisualElements DisplayName="Twinkle Tray Native" Description="Control display brightness" BackgroundColor="transparent" Square44x44Logo="Assets\Square44.png" Square150x150Logo="Assets\Square150.png" />
  <Extensions><desktop:Extension Category="windows.startupTask" Executable="TwinkleTray.WinUI.exe" EntryPoint="Windows.FullTrustApplication"><desktop:StartupTask TaskId="TwinkleTrayStartup" Enabled="false" DisplayName="Twinkle Tray Native" /></desktop:Extension></Extensions>
 </Application></Applications>
 <Capabilities><rescap:Capability Name="runFullTrust" /><DeviceCapability Name="location" /></Capabilities>
</Package>
"@ | Set-Content -LiteralPath (Join-Path $stage 'AppxManifest.xml') -Encoding utf8
    $msix = Join-Path $stagingRoot ($name + '-' + [Guid]::NewGuid().ToString('N') + '.msix')
    $pendingMsix = $msix
    & (Join-Path $sdk.FullName 'x64\makeappx.exe') pack /d $stage /p $msix /o
    if ($LASTEXITCODE -ne 0) { throw "MSIX validation/packaging failed ($LASTEXITCODE)." }
    if ($CertificatePath) {
        $certificate = (Resolve-Path -LiteralPath $CertificatePath).Path
        if ($env:TWINKLE_CERTIFICATE_PASSWORD) { throw 'Password-protected signing is intentionally left to your signing service; do not pass secrets on a command line.' }
        & (Join-Path $sdk.FullName 'x64\signtool.exe') sign /fd SHA256 /f $certificate $msix
        if ($LASTEXITCODE -ne 0) { throw "MSIX signing failed ($LASTEXITCODE)." }
    }
    $destinationMsix = Join-Path $output ($name + '.msix')
    Move-Item -LiteralPath $msix -Destination $destinationMsix -Force
    $pendingMsix = $null
    $deliverables.Add($destinationMsix)
    }
} finally {
    # Delete only this GUID staging directory inside the resolved temporary root.
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if (-not $resolvedStage.StartsWith($stagingRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staging path.' }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    if ($pendingMsix -and (Test-Path -LiteralPath $pendingMsix)) { Remove-Item -LiteralPath $pendingMsix -Force }
}
$sums = Join-Path $output ('SHA256SUMS-' + $Architecture + '.txt')
$deliverables | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_) } | Set-Content -LiteralPath $sums -Encoding ascii
Write-Host "Created $($deliverables.Count) packages in $output"
