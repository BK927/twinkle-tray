[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AppPath,
    [string] $TestOutput = (Join-Path $PSScriptRoot 'artifacts\test-results')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The native application integration test requires Windows.'
}
if (-not [Environment]::UserInteractive) {
    throw 'The GUI integration test requires an interactive desktop. Core tests can run without one.'
}

$application = (Resolve-Path -LiteralPath $AppPath).Path
if (-not (Test-Path -LiteralPath $application -PathType Leaf)) {
    throw "Application executable not found: $application"
}
$applicationDirectory = Split-Path -Parent $application
$outputRoot = [IO.Path]::GetFullPath($TestOutput)
$runDirectory = Join-Path $outputRoot ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff') + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null

$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$passedChecks = [Collections.Generic.List[string]]::new()
$script:invocationIndex = 0
$smokeStarted = [DateTime]::MaxValue
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $sid = $identity.User.Value } finally { $identity.Dispose() }
$sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
$demoMutexName = "Local\TwinkleTray.WinUI.$sid.$sessionId.Demo"
$demoPipeName = "TwinkleTray.WinUI.$sid.$sessionId.Demo"
$testMutex = [Threading.Mutex]::new($false, "$demoMutexName.IntegrationTests")
$ownsTestMutex = $false

function Start-TestProcess {
    param([string] $Name, [string[]] $Arguments)
    $script:invocationIndex++
    $prefix = '{0:D2}-{1}' -f $script:invocationIndex, $Name
    $stdout = Join-Path $runDirectory ($prefix + '.stdout.txt')
    $stderr = Join-Path $runDirectory ($prefix + '.stderr.txt')
    $process = Start-Process -FilePath $application -ArgumentList $Arguments -WorkingDirectory $applicationDirectory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $ownedProcesses.Add($process)
    return [pscustomobject]@{ Process = $process; StdoutPath = $stdout; StderrPath = $stderr }
}

function Invoke-TestCommand {
    param([string] $Name, [string[]] $Arguments, [switch] $AllowFailure, [ValidateRange(1, 180)][int] $TimeoutSeconds = 30)
    $invocation = Start-TestProcess -Name $Name -Arguments $Arguments
    try { $null = $invocation.Process.WaitForExitAsync().WaitAsync([TimeSpan]::FromSeconds($TimeoutSeconds)).GetAwaiter().GetResult() }
    catch [TimeoutException] { throw "'$Name' did not exit within $TimeoutSeconds seconds. See $runDirectory." }
    # Complete redirected stream delivery before reading the files.
    $invocation.Process.WaitForExit()
    $output = [IO.File]::ReadAllText($invocation.StdoutPath)
    $errorOutput = [IO.File]::ReadAllText($invocation.StderrPath)
    $exitCode = $invocation.Process.ExitCode
    if (-not $AllowFailure -and $exitCode -ne 0) {
        throw "'$Name' failed with exit code ${exitCode}: $output $errorOutput"
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output.Trim(); Error = $errorOutput.Trim() }
}

function Assert-Test {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Copy-UiTestEvidence {
    param([string] $ReportPath)
    $report = Get-Content -Raw -LiteralPath $ReportPath | ConvertFrom-Json
    Copy-Item -LiteralPath $ReportPath -Destination (Join-Path $runDirectory 'ui-layout-test.json')
    $previewRoot = [IO.Path]::GetFullPath((Join-Path $applicationDirectory 'test-fixtures\ui-previews')) + [IO.Path]::DirectorySeparatorChar
    $previewOutput = Join-Path $runDirectory 'ui-previews'
    New-Item -ItemType Directory -Path $previewOutput -Force | Out-Null
    foreach ($preview in $report.Previews) {
        $previewPath = [IO.Path]::GetFullPath($preview.Path)
        Assert-Test ($previewPath.StartsWith($previewRoot, [StringComparison]::OrdinalIgnoreCase)) 'A UI preview escaped the isolated fixture directory.'
        Copy-Item -LiteralPath $previewPath -Destination (Join-Path $previewOutput ([IO.Path]::GetFileName($previewPath)))
    }
    return $report
}

function Invoke-TestPipeRequest {
    param([string] $Request)
    # Address only the isolated demo server started by this script.
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $demoPipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    $reader = $null
    $writer = $null
    try {
        $pipe.Connect(5000)
        $reader = [IO.StreamReader]::new($pipe)
        $writer = [IO.StreamWriter]::new($pipe)
        $writer.AutoFlush = $true
        $writer.WriteLine($Request)
        $responseTask = $reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5))
        $response = $responseTask.GetAwaiter().GetResult()
        Assert-Test (-not [string]::IsNullOrWhiteSpace($response)) 'The demo IPC server returned no response.'
        return $response | ConvertFrom-Json
    }
    finally {
        if ($null -ne $writer) { $writer.Dispose() }
        if ($null -ne $reader) { $reader.Dispose() }
        $pipe.Dispose()
    }
}

try {
    try { $ownsTestMutex = $testMutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsTestMutex = $true }
    if (-not $ownsTestMutex) {
        throw 'Another integration test is already running in this Windows session.'
    }

    # A pre-existing demo uses the same isolated pipe. Do not modify or terminate it.
    $existingDemo = $null
    try {
        $existingDemo = [Threading.Mutex]::OpenExisting($demoMutexName)
    }
    catch [Threading.WaitHandleCannotBeOpenedException] { }
    if ($null -ne $existingDemo) {
        $existingDemo.Dispose()
        throw 'A demo or smoke-test instance is already running. Close that instance before running this test; it has not been changed.'
    }

    $smokePath = Join-Path $applicationDirectory 'smoke-test.json'
    $smokeStarted = [DateTime]::UtcNow
    $null = Invoke-TestCommand -Name 'smoke' -Arguments @('--smoke-test') -TimeoutSeconds 180
    Assert-Test (Test-Path -LiteralPath $smokePath -PathType Leaf) 'The application did not write smoke-test.json.'
    Assert-Test ((Get-Item -LiteralPath $smokePath).LastWriteTimeUtc -ge $smokeStarted) 'The smoke-test report is stale.'
    $smoke = Get-Content -Raw -LiteralPath $smokePath | ConvertFrom-Json
    Assert-Test ($smoke.Passed -and $smoke.NativeWinUI -and $smoke.SettingsWindow -and $smoke.DemoDisplays -eq 2 -and $smoke.SettingsPages -eq 11 -and $smoke.RuntimeChecks.Count -ge 41 -and $smoke.HardwareWrites -eq 0) 'The smoke report must confirm two demo displays, eleven settings pages, runtime checks, and zero hardware writes.'
    $automationPath = Join-Path $applicationDirectory 'automation-regression.json'
    Assert-Test (Test-Path -LiteralPath $automationPath -PathType Leaf) 'The application did not produce automation regression results.'
    Assert-Test ((Get-Item -LiteralPath $automationPath).LastWriteTimeUtc -ge $smokeStarted) 'The automation regression report is stale.'
    $automation = Get-Content -Raw -LiteralPath $automationPath | ConvertFrom-Json
    Assert-Test ($automation.Passed -and $automation.Failures.Count -eq 0 -and $automation.Observations.Count -ge 20) 'The scheduling, profile, sensor, idle, and manual-control automation regressions did not all pass.'
    Copy-Item -LiteralPath $automationPath -Destination (Join-Path $runDirectory 'automation-regression.json')
    Copy-Item -LiteralPath $smokePath -Destination (Join-Path $runDirectory 'smoke-test.json')
    $uiPath = Join-Path $applicationDirectory 'ui-layout-test.json'
    Assert-Test (Test-Path -LiteralPath $uiPath -PathType Leaf) 'The application did not produce UI layout results.'
    Assert-Test ((Get-Item -LiteralPath $uiPath).LastWriteTimeUtc -ge $smokeStarted) 'The UI layout report is stale.'
    $ui = Copy-UiTestEvidence -ReportPath $uiPath
    Assert-Test ($ui.Passed -and $ui.NativeWinUI -and $ui.MatrixCases -ge 144 -and $ui.Errors.Count -eq 0 -and $ui.HardwareWrites -eq 0 -and $ui.UserSettingsWrites -eq 0 -and -not $ui.WindowsDpiChanged) 'The arranged UI matrix, interaction checks, and isolated preview capture must all pass.'
    Assert-Test ($ui.Previews.Count -ge 11) 'The UI test must capture general, dark and compact settings, monitors, profiles, inline errors, tray, OSD, and render-scale previews.'
    $passedChecks.Add('Native startup, two demo displays, eleven settings pages, and runtime controls')

    $demo = Start-TestProcess -Name 'demo-server' -Arguments @('--demo', '--background')
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(10)
    $demoReady = $false
    while (-not $demoReady -and [DateTime]::UtcNow -lt $readyDeadline) {
        Assert-Test (-not $demo.Process.HasExited) 'The demo server exited during startup.'
        $readyMutex = $null
        try { $readyMutex = [Threading.Mutex]::OpenExisting($demoMutexName); $demoReady = $true }
        catch [Threading.WaitHandleCannotBeOpenedException] { Start-Sleep -Milliseconds 100 }
        finally { if ($null -ne $readyMutex) { $readyMutex.Dispose() } }
    }
    Assert-Test $demoReady 'The demo server did not initialize within ten seconds.'
    # The client waits up to ten seconds for initial discovery and the server pipe.
    $initialResponse = Invoke-TestCommand -Name 'list-initial' -Arguments @('--demo', '--List')
    Assert-Test (-not $demo.Process.HasExited) 'The demo server exited before the IPC tests could run.'
    $initial = @($initialResponse.Output | ConvertFrom-Json)
    Assert-Test ($initial.Count -eq 2 -and $initial[0].Id -eq 'demo:external' -and $initial[1].Id -eq 'demo:internal') 'The list command did not return the two simulated monitors.'
    $passedChecks.Add('Demo instance and JSON monitor listing')

    $set = Invoke-TestCommand -Name 'set-all' -Arguments @('--demo', '--All', '--Set=65')
    Assert-Test ($set.Output -eq 'OK') 'Set-all did not acknowledge success.'
    $offset = Invoke-TestCommand -Name 'offset-first' -Arguments @('--demo', '--MonitorNum=1', '--Offset=-5')
    Assert-Test ($offset.Output -eq 'OK') 'Monitor offset did not acknowledge success.'
    $levelsResponse = Invoke-TestCommand -Name 'list-after-writes' -Arguments @('--demo', '--List')
    $levels = @($levelsResponse.Output | ConvertFrom-Json)
    Assert-Test ($levels.Count -eq 2 -and $levels[0].Brightness -eq 60 -and $levels[1].Brightness -eq 65) 'Expected simulated brightness values 60 and 65 after set-all and offset commands.'
    $passedChecks.Add('IPC set-all and per-monitor relative brightness: 60/65')

    $invalid = Invoke-TestCommand -Name 'invalid-monitor' -Arguments @('--demo', '--MonitorNum=999', '--Set=50') -AllowFailure
    Assert-Test ($invalid.ExitCode -ne 0 -and ($invalid.Output + $invalid.Error) -match 'Monitor 999.*not found') 'An invalid monitor must return a nonzero exit code and a useful error message.'
    $passedChecks.Add('Invalid monitor produces an error response')

    foreach ($malformedRequest in @('{broken-json', '{}', '["--demo","--not-a-real-option"]')) {
        $malformedResponse = Invoke-TestPipeRequest -Request $malformedRequest
        Assert-Test (-not $malformedResponse.Success -and -not [string]::IsNullOrWhiteSpace($malformedResponse.Message)) 'Malformed IPC requests must produce a useful failure response.'
    }
    $recoveredResponse = Invoke-TestCommand -Name 'list-after-malformed-ipc' -Arguments @('--demo', '--List')
    $recovered = @($recoveredResponse.Output | ConvertFrom-Json)
    Assert-Test ($recovered.Count -eq 2 -and $recovered[0].Brightness -eq 60 -and $recovered[1].Brightness -eq 65) 'The IPC server did not recover without changing brightness after malformed requests.'
    $passedChecks.Add('IPC survives malformed JSON, wrong request types, and unsupported options')

    $idSet = Invoke-TestCommand -Name 'set-by-monitor-id' -Arguments @('--demo', '--MonitorID=demo:internal', '--Set=42')
    Assert-Test ($idSet.Output -eq 'OK') 'The stable monitor-ID selector failed.'
    $idLevelsResponse = Invoke-TestCommand -Name 'list-after-id-set' -Arguments @('--demo', '--List')
    $idLevels = @($idLevelsResponse.Output | ConvertFrom-Json)
    Assert-Test ($idLevels[0].Brightness -eq 60 -and $idLevels[1].Brightness -eq 42) 'The monitor-ID selector changed the wrong display.'
    $passedChecks.Add('IPC stable monitor-ID selection changes only the requested display')

    $null = Invoke-TestCommand -Name 'set-minimum' -Arguments @('--demo', '--All', '--Set=0')
    $null = Invoke-TestCommand -Name 'offset-below-minimum' -Arguments @('--demo', '--All', '--Offset=-5')
    $minResponse = Invoke-TestCommand -Name 'list-at-minimum' -Arguments @('--demo', '--List')
    $minimumLevels = @($minResponse.Output | ConvertFrom-Json)
    Assert-Test (@($minimumLevels | Where-Object Brightness -ne 0).Count -eq 0) 'IPC brightness offsets did not clamp to the lower limit.'
    $null = Invoke-TestCommand -Name 'set-maximum' -Arguments @('--demo', '--All', '--Set=100')
    $null = Invoke-TestCommand -Name 'offset-above-maximum' -Arguments @('--demo', '--All', '--Offset=5')
    $maxResponse = Invoke-TestCommand -Name 'list-at-maximum' -Arguments @('--demo', '--List')
    $maximumLevels = @($maxResponse.Output | ConvertFrom-Json)
    Assert-Test (@($maximumLevels | Where-Object Brightness -ne 100).Count -eq 0) 'IPC brightness offsets did not clamp to the upper limit.'
    $passedChecks.Add('IPC brightness offsets clamp at both physical limits')

    $help = Invoke-TestCommand -Name 'help' -Arguments @('--demo', '--help')
    Assert-Test ($help.ExitCode -eq 0 -and $help.Output.Contains('--MonitorNum') -and $help.Output.Contains('--Set')) 'The help command did not return the expected usage text.'
    $passedChecks.Add('Help returns usage and exit code zero')

    $time = Invoke-TestCommand -Name 'schedule' -Arguments @('--demo', '--UseTime')
    Assert-Test ($time.Output -eq 'OK') 'UseTime did not acknowledge current schedule evaluation.'
    $passedChecks.Add('IPC schedule evaluation')

    $overlay = Invoke-TestCommand -Name 'overlay' -Arguments @('--demo', '--All', '--Set=50', '--Overlay')
    Assert-Test ($overlay.Output -eq 'OK') 'The dedicated brightness overlay command failed.'
    $overlayLevelsResponse = Invoke-TestCommand -Name 'list-after-overlay' -Arguments @('--demo', '--List')
    $overlayLevels = @($overlayLevelsResponse.Output | ConvertFrom-Json)
    Assert-Test (@($overlayLevels | Where-Object Brightness -ne 50).Count -eq 0) 'The overlay command did not apply its requested brightness.'
    $passedChecks.Add('Dedicated native OSD and brightness update')

    $settingsResponse = Invoke-TestCommand -Name 'settings' -Arguments @('--demo', '--settings')
    Assert-Test ($settingsResponse.Output -eq 'OK' -and -not $demo.Process.HasExited) 'The settings-window command failed or terminated the demo server.'
    $passedChecks.Add('IPC opens the native settings window without ending the server')

    [ordered]@{
        Passed = $true
        Application = $application
        ApplicationSha256 = (Get-FileHash -LiteralPath $application -Algorithm SHA256).Hash
        ManagedAssemblySha256 = [ordered]@{
            'TwinkleTray.WinUI.dll' = (Get-FileHash -LiteralPath (Join-Path $applicationDirectory 'TwinkleTray.WinUI.dll') -Algorithm SHA256).Hash
            'TwinkleTray.Core.dll' = (Get-FileHash -LiteralPath (Join-Path $applicationDirectory 'TwinkleTray.Core.dll') -Algorithm SHA256).Hash
            'TwinkleTray.Hardware.dll' = (Get-FileHash -LiteralPath (Join-Path $applicationDirectory 'TwinkleTray.Hardware.dll') -Algorithm SHA256).Hash
        }
        CompletedUtc = [DateTime]::UtcNow.ToString('O')
        SimulatedMonitors = 2
        SettingsPages = $smoke.SettingsPages
        RuntimeChecks = $smoke.RuntimeChecks
        UiLayoutCases = $ui.MatrixCases
        HardwareWrites = 0
        Checks = $passedChecks.ToArray()
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runDirectory 'integration-test.json') -Encoding utf8
    Write-Host "PASS $($passedChecks.Count) integration checks. Results: $runDirectory"
}
catch {
    $smokePath = Join-Path $applicationDirectory 'smoke-test.json'
    if ((Test-Path -LiteralPath $smokePath -PathType Leaf) -and (Get-Item -LiteralPath $smokePath).LastWriteTimeUtc -ge $smokeStarted) {
        Copy-Item -LiteralPath $smokePath -Destination (Join-Path $runDirectory 'smoke-test.json')
    }
    $uiPath = Join-Path $applicationDirectory 'ui-layout-test.json'
    if ((Test-Path -LiteralPath $uiPath -PathType Leaf) -and (Get-Item -LiteralPath $uiPath).LastWriteTimeUtc -ge $smokeStarted) {
        try { $null = Copy-UiTestEvidence -ReportPath $uiPath } catch { Write-Warning "Could not copy all UI evidence: $($_.Exception.Message)" }
    }
    $automationPath = Join-Path $applicationDirectory 'automation-regression.json'
    if ((Test-Path -LiteralPath $automationPath -PathType Leaf) -and (Get-Item -LiteralPath $automationPath).LastWriteTimeUtc -ge $smokeStarted) {
        Copy-Item -LiteralPath $automationPath -Destination (Join-Path $runDirectory 'automation-regression.json')
    }
    [ordered]@{
        Passed = $false
        Application = $application
        CompletedUtc = [DateTime]::UtcNow.ToString('O')
        Error = $_.Exception.Message
        CompletedChecks = $passedChecks.ToArray()
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runDirectory 'integration-test.json') -Encoding utf8
    throw
}
finally {
    # Process objects refer only to children started by this invocation; never kill by name.
    foreach ($owned in $ownedProcesses) {
        try {
            if (-not $owned.HasExited) {
                $owned.Kill()
                $null = $owned.WaitForExit(5000)
            }
        }
        catch [InvalidOperationException] { }
        finally { $owned.Dispose() }
    }
    if ($ownsTestMutex) { $testMutex.ReleaseMutex() }
    $testMutex.Dispose()
}
