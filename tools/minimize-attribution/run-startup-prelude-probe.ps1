param([string]$RunName='startup-prelude-001',[int]$StartupCount=4,[string]$EtwCollector='.runtime\minimize-attribution-consumer-v5d\Win32kAttributionCollector.exe',[switch]$RequireNativeProbe,[switch]$PreludePicker)
$ErrorActionPreference='Stop'
$projectPath=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
Set-Location -LiteralPath $projectPath
if($RunName -notmatch '^[a-z0-9-]+$' -or $StartupCount -lt 1 -or $StartupCount -gt $(if($PreludePicker){4}else{24})){throw 'Fresh simple run name and at most 4 prelude pairs or 24 direct prefixes required.'}
$root=Join-Path $projectPath ('artifacts\minimize-attribution\'+$RunName)
if(Test-Path -LiteralPath $root){throw 'Fresh run directory required.'}
New-Item -ItemType Directory -Path $root|Out-Null
$watch=Join-Path $root 'targets.json';$receiverRoot=Join-Path $root 'coordinator';$etwRoot=Join-Path $root 'etw'
$receiverExe=Join-Path $projectPath '.runtime\tests\NativeTests.exe'
if(-not [IO.Path]::IsPathRooted($EtwCollector)){$EtwCollector=Join-Path $projectPath $EtwCollector}
$EtwCollector=[IO.Path]::GetFullPath($EtwCollector)
if(-not $EtwCollector.StartsWith($projectPath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Project collector only.'}
Add-Type -TypeDefinition (Get-Content 'tools\common\InputDesktopProbe.cs' -Raw)
Add-Type -TypeDefinition (Get-Content 'tools\common\AlphaObscurationProbe.cs' -Raw)
$previousNative=[Environment]::GetEnvironmentVariable('CODEX_ISLAND_REQUIRE_NATIVE_MINIMIZE_PROBE')
$result=[ordered]@{StartedUtc=[DateTime]::UtcNow.ToString('o');PreludePicker=[bool]$PreludePicker;Full79Executed=$false;RequestedIterations=$StartupCount;AutomaticRetries=0;Completed=$false;HistoricalSenderProven=$false;TestExecutableSHA256=(Get-FileHash $receiverExe -Algorithm SHA256).Hash;CollectorSHA256=(Get-FileHash $EtwCollector -Algorithm SHA256).Hash;NativeProbeRequired=[bool]$RequireNativeProbe}
$collector=$null;$receiver=$null;$appStopped=$false
function Save-Desktop {
    $sample=[ordered]@{Utc=[DateTime]::UtcNow.ToString('o');InputDesktop=[OwnedInputDesktopProbe]::Read();Foreground=[AlphaObscurationProbe]::Foreground();Points=@([AlphaObscurationProbe]::At(100,100),[AlphaObscurationProbe]::At(3000,600))}
    $sample|ConvertTo-Json -Compress -Depth 6|Add-Content -LiteralPath (Join-Path $root 'input-desktop.jsonl') -Encoding UTF8
    return $sample
}
try {
    $initial=Save-Desktop
    if($initial.InputDesktop -ne 'Default' -or $initial.Foreground.ProcessName -eq 'LockApp' -or $initial.Foreground.Class -like 'LockScreen*'){throw 'Desktop obscured; no UI started.'}
    $existing=@(Get-ChildItem Env:CODEX_ISLAND* -ErrorAction SilentlyContinue)
    if($existing.Count){throw 'Unexpected existing Codex Island diagnostic/data environment.'}
    $shutdown=Start-Process -FilePath (Join-Path $projectPath 'packaging\app\CodexIsland.exe') -ArgumentList '--shutdown' -WindowStyle Hidden -Wait -PassThru
    if($shutdown.ExitCode -ne 0){throw 'Graceful app shutdown failed.'}
    $appStopped=$true
    $appClock=[Diagnostics.Stopwatch]::StartNew()
    do{$remaining=@(Get-Process -Name CodexIsland -ErrorAction SilentlyContinue|Where-Object{$_.Path -eq (Join-Path $projectPath 'packaging\app\CodexIsland.exe')});if(-not $remaining.Count){break};Start-Sleep -Milliseconds 100}while($appClock.Elapsed.TotalSeconds -lt 8)
    if($remaining.Count){throw 'Installed app did not finish shutdown.'}
    [Environment]::SetEnvironmentVariable('CODEX_ISLAND_REQUIRE_NATIVE_MINIMIZE_PROBE',$(if($RequireNativeProbe){'1'}else{$null}))
    $collector=Start-Process -FilePath $EtwCollector -ArgumentList @('--watch',$watch,'--output',$etwRoot,'--seconds','900') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'collector.stdout.log') -RedirectStandardError (Join-Path $root 'collector.stderr.log')
    $null=$collector.Handle;$readyClock=[Diagnostics.Stopwatch]::StartNew()
    while(-not (Test-Path (Join-Path $etwRoot 'ready.json')) -and -not $collector.HasExited -and $readyClock.Elapsed.TotalSeconds -lt 12){Start-Sleep -Milliseconds 50}
    if(-not (Test-Path (Join-Path $etwRoot 'ready.json'))){throw 'Collector not ready.'}
    $receiverArguments=@('--startup-count',[string]$StartupCount,'--output',$receiverRoot,'--watchfile',$watch)
    if($PreludePicker){$receiverArguments+=@('--prelude-picker','1')}
    $receiver=Start-Process -FilePath $receiverExe -ArgumentList $receiverArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'coordinator.stdout.log') -RedirectStandardError (Join-Path $root 'coordinator.stderr.log')
    $null=$receiver.Handle;$result.CoordinatorId=$receiver.Id;$nextSample=[DateTime]::UtcNow
    while(-not $receiver.WaitForExit(1000)){if([DateTime]::UtcNow -ge $nextSample){$null=Save-Desktop;$nextSample=[DateTime]::UtcNow.AddSeconds(5)}}
    $receiver.WaitForExit();$result.CoordinatorExit=$receiver.ExitCode
    if($receiver.ExitCode -ne 0){throw 'Startup prefix stopped at first failure; original records retained, no retry.'}
    $result.Completed=$true
}catch{$result.Error=$_.Exception.ToString();throw}
finally {
    try {
        if($receiver -and -not $receiver.HasExited){$result.Completed=$false;$result.CleanupWaitedForOwnedCoordinator=$true;$receiver.WaitForExit()}
        if($collector){
            [IO.File]::WriteAllText(($watch+'.stop.tmp'),'{"Version":1,"Targets":[],"Stop":true}')
            [IO.File]::Move(($watch+'.stop.tmp'),$watch,$true)
            $collector.WaitForExit();$result.CollectorExit=$collector.ExitCode
            $summary=Get-Content (Join-Path $etwRoot 'summary.json') -Raw|ConvertFrom-Json
            if($collector.ExitCode -ne 0 -or -not $summary.CleanupConfirmed -or $summary.DataLossDetected -or $summary.CallbackErrors -or $summary.WatchParseErrors -or $summary.LogLimitReached -or $summary.StopReason -ne 'watch-stop-request'){$result.Completed=$false;$result.ObserverError='ETW quality/cleanup/coverage failure.'}
        }
    }catch{$result.Completed=$false;$result.ObserverError=$_.Exception.ToString()}
    finally {
        [Environment]::SetEnvironmentVariable('CODEX_ISLAND_REQUIRE_NATIVE_MINIMIZE_PROBE',$previousNative)
        try{if($appStopped){Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList 'shell:AppsFolder\CodexIsland.Notifications_vg32xay9edax2!Island' -WindowStyle Hidden};$result.AppRestartRequested=$appStopped}
        finally{$result.EndedUtc=[DateTime]::UtcNow.ToString('o');$result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $root 'launch.json') -Encoding UTF8}
    }
}
Get-Content -LiteralPath (Join-Path $root 'launch.json')
if(-not $result.Completed){throw 'Prefix experiment or observer did not complete; logs retained.'}
