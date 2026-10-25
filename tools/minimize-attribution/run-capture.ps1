param([string]$RunName='capture-001',[int]$ObserveSeconds=0,[switch]$DiagnosticRaw,
    [string]$CollectorPath='.runtime\minimize-attribution-consumer-v4\Win32kAttributionCollector.exe',
    [string]$ReceiverPath='.runtime\minimize-origin-calibration-v4\NativeTests.exe')
$ErrorActionPreference='Stop'
$projectPath=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
if($RunName -notmatch '^[a-z0-9-]+$'){throw 'Use a simple run name.'}
if($ObserveSeconds -ne 0 -and ($ObserveSeconds -lt 1 -or $ObserveSeconds -gt 300)){throw 'Observation must be 1..300 seconds (receiver limit).'}
$root=Join-Path $projectPath ('artifacts\minimize-attribution\'+$RunName)
if(Test-Path -LiteralPath $root){throw 'Fresh run required.'}
New-Item -ItemType Directory -Path $root|Out-Null
$receiverRoot=Join-Path $root 'receiver'
$watch=Join-Path $receiverRoot 'targets.json'
$traceRoot=Join-Path $root 'etw'
$collectorExe=[IO.Path]::GetFullPath((Join-Path $projectPath $CollectorPath))
$receiverExe=[IO.Path]::GetFullPath((Join-Path $projectPath $ReceiverPath))
foreach($executable in @($collectorExe,$receiverExe)){
    if(-not $executable.StartsWith($projectPath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Only project-owned diagnostic executables are allowed.'}
}
$traceArgs=@('--watch',$watch,'--output',$traceRoot,'--seconds',[string]([Math]::Max(30,$ObserveSeconds+15)))
if($DiagnosticRaw){$traceArgs+=@('--diagnostic-owned-raw','1')}
$collector=$null;$receiver=$null
$result=[ordered]@{StartedUtc=[DateTime]::UtcNow.ToString('o');Controlled=($ObserveSeconds -eq 0);ObserveSeconds=$ObserveSeconds;CollectorSHA256=(Get-FileHash $collectorExe -Algorithm SHA256).Hash;ReceiverSHA256=(Get-FileHash $receiverExe -Algorithm SHA256).Hash;Completed=$false}
try {
    $collector=Start-Process -FilePath $collectorExe -ArgumentList $traceArgs -WorkingDirectory $projectPath -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'collector.stdout.log') -RedirectStandardError (Join-Path $root 'collector.stderr.log')
    $null=$collector.Handle
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while(-not (Test-Path (Join-Path $traceRoot 'ready.json')) -and -not $collector.HasExited -and $clock.Elapsed.TotalSeconds -lt 12){Start-Sleep -Milliseconds 50}
    if(-not (Test-Path (Join-Path $traceRoot 'ready.json'))){throw 'Collector did not become ready.'}
    $args=@('--output',$receiverRoot)
    if($ObserveSeconds){$args+=@('--observe-seconds',[string]$ObserveSeconds)}else{$args+=@('--suite','MinimizeOriginCalibration')}
    $receiver=Start-Process -FilePath $receiverExe -ArgumentList $args -WorkingDirectory $projectPath -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'receiver.stdout.log') -RedirectStandardError (Join-Path $root 'receiver.stderr.log')
    $null=$receiver.Handle;$receiver.WaitForExit()
    $result.ReceiverId=$receiver.Id;$result.ReceiverExit=$receiver.ExitCode
} finally {
    # A receiver may fail before publishing targets. Always signal our collector;
    # otherwise an invalid receiver invocation needlessly runs until its timeout.
    if(-not (Test-Path -LiteralPath $watch)){
        New-Item -ItemType Directory -Path $receiverRoot -Force|Out-Null
        [IO.File]::WriteAllText($watch,'{"Version":1,"Targets":[],"Stop":true}')
    }
    if(Test-Path -LiteralPath $watch){
        $map=Get-Content -LiteralPath $watch -Raw|ConvertFrom-Json
        $map|Add-Member -NotePropertyName Stop -NotePropertyValue $true -Force
        $temporary=$watch+'.stop.tmp'
        $map|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $temporary -Encoding UTF8
        [IO.File]::Move($temporary,$watch,$true)
    }
    if($collector){$collector.WaitForExit();$result.CollectorId=$collector.Id;$result.CollectorExit=$collector.ExitCode}
    $result.Completed=($result.ReceiverExit -eq 0 -and $result.CollectorExit -eq 0)
    $result.EndedUtc=[DateTime]::UtcNow.ToString('o')
    $result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $root 'launch.json') -Encoding UTF8
}
Get-Content -LiteralPath (Join-Path $root 'launch.json')
Get-Content -LiteralPath (Join-Path $root 'receiver.stdout.log') -Tail 8
Get-Content -LiteralPath (Join-Path $traceRoot 'summary.json')
if(-not $result.Completed){throw 'Capture did not complete.'}
