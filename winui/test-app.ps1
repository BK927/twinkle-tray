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
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $sid = $identity.User.Value } finally { $identity.Dispose() }
$sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
$demoMutexName = "Local\TwinkleTray.WinUI.$sid.$sessionId.Demo"
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
    param([string] $Name, [string[]] $Arguments, [switch] $AllowFailure)
    $invocation = Start-TestProcess -Name $Name -Arguments $Arguments
    if (-not $invocation.Process.WaitForExit(30000)) {
        throw "'$Name' did not exit within 30 seconds. See $runDirectory."
    }
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
    $null = Invoke-TestCommand -Name 'smoke' -Arguments @('--smoke-test')
    Assert-Test (Test-Path -LiteralPath $smokePath -PathType Leaf) 'The application did not write smoke-test.json.'
    Assert-Test ((Get-Item -LiteralPath $smokePath).LastWriteTimeUtc -ge $smokeStarted) 'The smoke-test report is stale.'
    $smoke = Get-Content -Raw -LiteralPath $smokePath | ConvertFrom-Json
    Assert-Test ($smoke.Passed -and $smoke.NativeWinUI -and $smoke.SettingsWindow -and $smoke.DemoDisplays -eq 2 -and $smoke.SettingsPages -eq 11 -and $smoke.RuntimeChecks.Count -ge 9 -and $smoke.HardwareWrites -eq 0) 'The smoke report must confirm two demo displays, eleven settings pages, runtime checks, and zero hardware writes.'
    Copy-Item -LiteralPath $smokePath -Destination (Join-Path $runDirectory 'smoke-test.json')
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

    $help = Invoke-TestCommand -Name 'help' -Arguments @('--demo', '--help')
    Assert-Test ($help.ExitCode -eq 0 -and $help.Output.Contains('--MonitorNum') -and $help.Output.Contains('--Set')) 'The help command did not return the expected usage text.'
    $passedChecks.Add('Help returns usage and exit code zero')

    $time = Invoke-TestCommand -Name 'schedule' -Arguments @('--demo', '--UseTime')
    Assert-Test ($time.Output -eq 'OK') 'UseTime did not acknowledge current schedule evaluation.'
    $passedChecks.Add('IPC schedule evaluation')

    $overlay = Invoke-TestCommand -Name 'overlay' -Arguments @('--demo', '--All', '--Set=50', '--Overlay')
    Assert-Test ($overlay.Output -eq 'OK') 'The dedicated brightness overlay command failed.'
    $passedChecks.Add('Dedicated native OSD and brightness update')

    [ordered]@{
        Passed = $true
        Application = $application
        CompletedUtc = [DateTime]::UtcNow.ToString('O')
        SimulatedMonitors = 2
        SettingsPages = $smoke.SettingsPages
        RuntimeChecks = $smoke.RuntimeChecks
        HardwareWrites = 0
        Checks = $passedChecks.ToArray()
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runDirectory 'integration-test.json') -Encoding utf8
    Write-Host "PASS $($passedChecks.Count) integration checks. Results: $runDirectory"
}
catch {
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
