[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string[]] $Architecture = @('x64'),
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The WinUI 3 application must be built on Windows.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 10 SDK, then reopen your terminal and rerun this script.'
}

$project = Join-Path $PSScriptRoot 'TwinkleTray.WinUI\TwinkleTray.WinUI.csproj'
$tests = Join-Path $PSScriptRoot 'TwinkleTray.Core.Tests\TwinkleTray.Core.Tests.csproj'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }

function Invoke-DotNet {
    param([string[]] $DotNetArguments)
    & dotnet @DotNetArguments
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'If a build error mentions MAX_PATH, PathTooLong, or a path over 260 characters, retry with a shorter writable NUGET_PACKAGES path and a short checkout path. Do not edit the installed SDK.'
        throw "dotnet $($DotNetArguments[0]) failed with exit code $LASTEXITCODE."
    }
}

Push-Location $PSScriptRoot
try {
    $selectedSdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $selectedSdk -notmatch '^10\.') {
        throw "This port requires the .NET 10 SDK. The selected SDK is '$selectedSdk'. Check dotnet --list-sdks and any parent global.json."
    }
    # XAML build tools still contain components affected by legacy Windows path limits.
    if ($nugetRoot.Length -gt 70 -or $PSScriptRoot.Length -gt 120) {
        Write-Warning "A long checkout or NuGet cache path can produce dependency paths beyond 260 characters. Use a shorter writable NUGET_PACKAGES directory if restore or XAML compilation fails. Current NuGet root: $nugetRoot"
    }

    if (-not $SkipTests) {
        Invoke-DotNet @('run', '--project', $tests, '--configuration', $Configuration)
    }

    foreach ($targetArchitecture in ($Architecture | Select-Object -Unique)) {
        $runtime = 'win-' + $targetArchitecture.ToLowerInvariant()
        $platform = if ($targetArchitecture -ieq 'ARM64') { 'ARM64' } else { 'x64' }
        $publishDirectory = Join-Path $artifacts $runtime
        Invoke-DotNet @(
            'publish', $project,
            '--configuration', $Configuration,
            '--runtime', $runtime,
            '--self-contained', 'true',
            "-p:Platform=$platform",
            '-p:WindowsAppSDKSelfContained=true',
            '--output', $publishDirectory
        )
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'TwinkleTray.WinUI.exe'))) {
            throw "Publishing did not produce TwinkleTray.WinUI.exe in $publishDirectory."
        }
        Write-Host "Published $runtime to $publishDirectory"
    }
    Write-Host 'Distribute the entire publish directory. Keep the executable, DLLs, Assets, and Localization folders together.'
}
finally {
    Pop-Location
}
