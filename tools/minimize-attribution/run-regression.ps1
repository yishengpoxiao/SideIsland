param([string]$RunName='full-regression-001')
$ErrorActionPreference='Stop'
$projectDirectory=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
if($RunName -notmatch '^[a-z0-9-]+$'){throw 'Use a simple fresh run name.'}
$runDirectory=Join-Path $projectDirectory ('artifacts\minimize-attribution\'+$RunName)
if(Test-Path -LiteralPath $runDirectory){throw 'Fresh evidence directory required.'}
New-Item -ItemType Directory -Path $runDirectory|Out-Null
Set-Location -LiteralPath $projectDirectory
Add-Type -TypeDefinition (Get-Content -LiteralPath 'tools\common\InputDesktopProbe.cs' -Raw)
Add-Type -TypeDefinition (Get-Content -LiteralPath 'tools\common\AlphaObscurationProbe.cs' -Raw)
$testExecutable=Join-Path $projectDirectory '.runtime\tests\NativeTests.exe'
$appExecutable=Join-Path $projectDirectory 'packaging\app\CodexIsland.exe'
$collectorExecutable=Join-Path $projectDirectory '.runtime\native-diagnostics\WerOwnDumpCollector.exe'
$etwExecutable=Join-Path $projectDirectory '.runtime\minimize-attribution-consumer-v4\Win32kAttributionCollector.exe'
$captureDirectory=Join-Path $runDirectory 'coordinator'
$nativeDirectory=Join-Path $runDirectory 'native-trace'
$watch=Join-Path $nativeDirectory 'targets.json'
$runResult=[ordered]@{StartedUtc=[DateTime]::UtcNow.ToString('o');SingleCoordinatorInvocation=$true;AutomaticRetries=0;Completed=$false;HistoricalSenderProven=$false}
$appStopped=$false;$etw=$null;$collector=$null
$previousDiagnostic= [Environment]::GetEnvironmentVariable('CODEX_ISLAND_MINIMIZE_TRACE_DIR')
function Save-DesktopSample {
    $sample=[ordered]@{Utc=[DateTime]::UtcNow.ToString('o');InputDesktop=[OwnedInputDesktopProbe]::Read();Foreground=[AlphaObscurationProbe]::Foreground();Points=@([AlphaObscurationProbe]::At(100,100),[AlphaObscurationProbe]::At(3000,600))}
    $sample|ConvertTo-Json -Compress -Depth 6|Add-Content -LiteralPath (Join-Path $runDirectory 'input-desktop.jsonl') -Encoding UTF8
    return $sample
}
try {
    $initial=Save-DesktopSample
    if($initial.InputDesktop -ne 'Default' -or $initial.Foreground.ProcessName -eq 'LockApp' -or $initial.Foreground.Class -like 'LockScreen*'){throw 'Interactive desktop is obscured; no regression started.'}
    $observerVariables=@(Get-ChildItem Env:CODEX_ISLAND* -ErrorAction SilentlyContinue|Select-Object Name,Value)
    $observerVariables|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $runDirectory 'inherited-observer-environment.json') -Encoding UTF8
    if($observerVariables.Count){throw 'Unexpected inherited observer/data overrides.'}
    foreach($entry in @(@('TestExecutableSHA256',$testExecutable),@('AppExecutableSHA256',$appExecutable),@('CollectorSHA256',$collectorExecutable),@('EtwCollectorSHA256',$etwExecutable))){$runResult[$entry[0]]=(Get-FileHash -LiteralPath $entry[1] -Algorithm SHA256).Hash}
    $runResult.CandidateAppSHA256=(Get-FileHash -LiteralPath 'dist\CodexIsland.exe' -Algorithm SHA256).Hash
    $runResult.DiagnosticEnvironment=[ordered]@{CODEX_ISLAND_MINIMIZE_TRACE_DIR=$nativeDirectory}
    $files=@(Get-ChildItem -LiteralPath src,tests -Filter '*.cs' -Recurse -File)
    $manifest=@($files|ForEach-Object{[ordered]@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
    $manifest|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $runDirectory 'sources-before.json') -Encoding UTF8
    Copy-Item -LiteralPath $testExecutable -Destination (Join-Path $runDirectory 'NativeTests.exe')
    $expected=@([regex]::Matches((Get-Content -LiteralPath 'tests\NativeTests.cs' -Raw),'new TestSuite\("([A-Za-z0-9_]+)"')|ForEach-Object{$_.Groups[1].Value})
    if($expected.Count -ne 79){throw 'Expected exactly 79 registered suites.'}
    $expected|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $runDirectory 'expected-suite-order.json') -Encoding UTF8
    $shutdown=Start-Process -FilePath $appExecutable -ArgumentList '--shutdown' -WindowStyle Hidden -Wait -PassThru
    if($shutdown.ExitCode -ne 0){throw 'Graceful app shutdown failed.'}
    $appStopped=$true
    $shutdownClock=[Diagnostics.Stopwatch]::StartNew()
    do {
        $remaining=@(Get-Process -Name CodexIsland -ErrorAction SilentlyContinue|Where-Object{$_.Path -eq $appExecutable})
        if(-not $remaining.Count){break}
        Start-Sleep -Milliseconds 100
    }while($shutdownClock.Elapsed.TotalSeconds -lt 8)
    if($remaining.Count){throw 'Installed app did not finish graceful shutdown.'}
    [Environment]::SetEnvironmentVariable('CODEX_ISLAND_MINIMIZE_TRACE_DIR',$nativeDirectory)
    $etw=Start-Process -FilePath $etwExecutable -ArgumentList @('--watch',$watch,'--output',(Join-Path $runDirectory 'etw'),'--seconds','900') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runDirectory 'etw.stdout.log') -RedirectStandardError (Join-Path $runDirectory 'etw.stderr.log')
    $null=$etw.Handle;$readyClock=[Diagnostics.Stopwatch]::StartNew()
    while(-not (Test-Path (Join-Path $runDirectory 'etw\ready.json')) -and -not $etw.HasExited -and $readyClock.Elapsed.TotalSeconds -lt 12){Start-Sleep -Milliseconds 50}
    if(-not (Test-Path (Join-Path $runDirectory 'etw\ready.json'))){throw 'ETW collector did not become ready.'}
    $collector=Start-Process -FilePath $collectorExecutable -ArgumentList @($testExecutable,'--full-regression',$captureDirectory,'1800') -WorkingDirectory $projectDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runDirectory 'collector-console.log') -RedirectStandardError (Join-Path $runDirectory 'collector-stderr.log')
    $null=$collector.Handle;$runResult.CollectorProcessId=$collector.Id
    $nextSample=[DateTime]::UtcNow
    while(-not $collector.WaitForExit(1000)){
        if([DateTime]::UtcNow -ge $nextSample){$null=Save-DesktopSample;$nextSample=[DateTime]::UtcNow.AddSeconds(5)}
    }
    $collector.WaitForExit();$runResult.CollectorExitCode=$collector.ExitCode
    $null=Save-DesktopSample
    $runResult.TestExecutableUnchanged=((Get-FileHash -LiteralPath $testExecutable -Algorithm SHA256).Hash -eq $runResult.TestExecutableSHA256)
    $after=@($manifest|ForEach-Object{[ordered]@{Path=$_.Path;ExpectedSHA256=$_.SHA256;ActualSHA256=(Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash}})
    $after|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $runDirectory 'sources-after.json') -Encoding UTF8
    $runResult.SourcesUnchanged=(@($after|Where-Object{$_.ExpectedSHA256 -ne $_.ActualSHA256}).Count -eq 0)
    $runResult.CandidateAppUnchanged=((Get-FileHash -LiteralPath 'dist\CodexIsland.exe' -Algorithm SHA256).Hash -eq $runResult.CandidateAppSHA256)
    if($collector.ExitCode -ne 0){throw 'Continuous regression failed; no retry was performed.'}
    if(-not $runResult.TestExecutableUnchanged -or -not $runResult.SourcesUnchanged -or -not $runResult.CandidateAppUnchanged){throw 'Sources or executable identity changed during regression.'}
    $runResult.Completed=$true
}catch{
    $runResult.Error=$_.Exception.ToString()
    throw
}finally{
    try{
        if($collector -and -not $collector.HasExited){
            $runResult.Completed=$false
            $runResult.OwnedCollectorCleanupWait=$true
            # Do not restart the installed UI while our exact existing test
            # process is still running. Its collector enforces its own deadline.
            $collector.WaitForExit()
            $runResult.CleanupCollectorExitCode=$collector.ExitCode
        }
        if($etw){
            try{
                if(-not (Test-Path -LiteralPath $watch)){
                    New-Item -ItemType Directory -Path $nativeDirectory -Force|Out-Null
                    [IO.File]::WriteAllText($watch,'{"Version":1,"Targets":[],"Stop":true}')
                }else{
                    $map=Get-Content -LiteralPath $watch -Raw|ConvertFrom-Json
                    $map|Add-Member -NotePropertyName Stop -NotePropertyValue $true -Force
                    $temporary=$watch+'.stop.tmp';$map|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $temporary -Encoding UTF8
                    [IO.File]::Move($temporary,$watch,$true)
                }
            }catch{
                $runResult.ObserverStopError=$_.Exception.ToString()
                # The collector also has its own fixed 900-second deadline.
            }
            $etw.WaitForExit();$runResult.EtwCollectorExitCode=$etw.ExitCode
            $quality=Get-Content -LiteralPath (Join-Path $runDirectory 'etw\summary.json') -Raw|ConvertFrom-Json
            if($etw.ExitCode -ne 0 -or -not $quality.CleanupConfirmed -or $quality.DataLossDetected -or $quality.CallbackErrors -or $quality.WatchParseErrors -or $quality.LogLimitReached -or $quality.StopReason -ne 'watch-stop-request'){
                throw 'ETW capture failed quality/cleanup/coverage checks.'
            }
        }
    }catch{
        $runResult.ObserverError=$_.Exception.ToString();$runResult.Completed=$false
    }finally{
        [Environment]::SetEnvironmentVariable('CODEX_ISLAND_MINIMIZE_TRACE_DIR',$previousDiagnostic)
        try{
            if($appStopped){Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList 'shell:AppsFolder\CodexIsland.Notifications_vg32xay9edax2!Island' -WindowStyle Hidden}
            $runResult.AppRestartRequested=$appStopped
        }catch{
            $runResult.AppRestartError=$_.Exception.ToString();$runResult.Completed=$false
        }finally{
            $runResult.EndedUtc=[DateTime]::UtcNow.ToString('o')
            $runResult|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $runDirectory 'result.json') -Encoding UTF8
        }
    }
}
Get-Content -LiteralPath (Join-Path $runDirectory 'result.json')
if(-not $runResult.Completed){throw 'Regression or diagnostic validation did not complete successfully; evidence retained.'}
