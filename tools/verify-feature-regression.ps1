param([Parameter(Mandatory=$true)][string]$RunDirectory)
$ErrorActionPreference='Stop'
$project=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$run=(Resolve-Path -LiteralPath $RunDirectory).Path
$evidence=Join-Path $project 'artifacts\features-1.13'
if(-not $run.StartsWith($evidence+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Use a run under artifacts/features-1.13.'}
function Require([bool]$condition,[string]$message){if(-not $condition){throw $message}}
function ReadJson([string]$name){Get-Content -LiteralPath (Join-Path $run $name) -Raw -Encoding UTF8|ConvertFrom-Json}
$result=ReadJson 'result.json'
$collector=ReadJson 'coordinator\collector-result.json'
$owned=ReadJson 'coordinator\owned-processes.json'
$expected=@(ReadJson 'expected-suite-order.json')
$lines=@(Get-Content -LiteralPath (Join-Path $run 'coordinator\stdout.log') -Encoding UTF8)
$starts=@($lines|ForEach-Object{if($_ -match '^SUITE START:(.+)$'){$Matches[1]}})
$passes=@($lines|ForEach-Object{if($_ -match '^SUITE PASS:(.+)$'){$Matches[1]}})
$count=$expected.Count
Require ($count -eq 87 -and $expected[78] -eq 'LongRunningTests') 'Expected original 79 suites followed by 8 new suites.'
Require ($result.Completed -eq $true -and $result.FullRegression -eq $true -and $result.AutomaticRetries -eq 0) 'Continuous run did not finish without retries.'
Require ($result.TestUnchanged -eq $true -and $result.SourcesUnchanged -eq $true -and $result.SourceSetUnchanged -eq $true) 'Sources or executable changed during validation.'
Require ($result.HistoricalMinimizeRootCauseClaimed -eq $false) 'Regression cannot prove the historical minimize source.'
Require ((Get-FileHash -LiteralPath (Join-Path $run 'NativeTests.exe') -Algorithm SHA256).Hash -eq $result.TestSHA256) 'Preserved binary hash differs.'
Require ((Get-Content -LiteralPath (Join-Path $run 'coordinator\target-sha256.txt') -Raw).Trim() -eq $result.TestSHA256) 'Collector launched a different test binary.'
$before=@(ReadJson 'sources-before.json');$after=@(ReadJson 'sources-after.json')
Require ($before.Count -gt 0 -and $before.Count -eq $after.Count) 'Source manifests differ.'
foreach($file in $before){$match=@($after|Where-Object Path -eq $file.Path);Require ($match.Count -eq 1 -and $match[0].ExpectedSHA256 -eq $file.SHA256 -and $match[0].ActualSHA256 -eq $file.SHA256) ('Changed source: '+$file.Path)}
Require ($collector.FullRegression -eq $true -and $collector.ChildExited -eq $true -and $collector.ChildExitHex -eq '0x00000000') 'Default coordinator failed.'
Require ($collector.DeadlineTerminated -eq $false -and $collector.AutomaticRetry -eq $false -and @($collector.Failures).Count -eq 0 -and @($collector.CapturedFiles).Count -eq 0) 'Timeout, retry or native failure evidence needs review.'
Require ($starts.Count -eq $count -and $passes.Count -eq $count) 'Missing suite start/pass markers.'
Require (@($starts|Sort-Object -Unique).Count -eq $count -and @($passes|Sort-Object -Unique).Count -eq $count) 'Repeated suite.'
for($i=0;$i -lt $count;$i++){Require ($starts[$i] -eq $expected[$i] -and $passes[$i] -eq $expected[$i]) ('Suite order mismatch: '+$i)}
Require (@($lines|Where-Object{$_ -eq ('PASS: '+$count+' isolated suite workers completed; no automatic retries.')}).Count -eq 1) 'Coordinator final marker missing.'
Require (@($lines|Where-Object{$_ -match '^PASS: 40 quota/display checks'}).Count -eq 1) 'Original 40 primitive checks missing.'
foreach($name in @('coordinator\stderr.log','full-regression.collector.stderr.log')){Require ((Get-Item -LiteralPath (Join-Path $run $name)).Length -eq 0) ('Nonempty stderr: '+$name)}
$coordinators=@($owned.Processes|Where-Object Role -eq 'coordinator')
Require ($coordinators.Count -eq 1 -and @($owned.Processes).Count -gt 1) 'Owned process observer recorded no suite children.'
Require (@($owned.Processes|Where-Object{-not $_.Exited -or $_.ExitCode -ne 0 -or $_.ExitHex -ne '0x00000000'}).Count -eq 0) 'An observed process did not exit cleanly.'
$desktop=@(Get-Content -LiteralPath (Join-Path $run 'desktop.jsonl') -Encoding UTF8|ForEach-Object{$_|ConvertFrom-Json})
$lockCount=@($desktop|Where-Object{$_.Desktop -ne 'Default' -or @(@($_.Foreground)+@($_.ScreenPoints)|Where-Object{$_.ProcessName -eq 'LockApp' -or $_.Class -like 'LockScreen*'}).Count}).Count
Require ($lockCount -eq 0) 'Desktop became locked during native validation.'
$split=Join-Path $run 'suites';Require (-not(Test-Path -LiteralPath $split)) 'Derived suite logs already exist.'
New-Item -ItemType Directory -Path $split|Out-Null
$buffer=New-Object 'System.Collections.Generic.List[string]';$active=$null;$index=0
foreach($line in $lines){
    if($line -match '^SUITE START:(.+)$'){Require (-not $active -and $index -lt $count -and $Matches[1] -eq $expected[$index]) 'Overlapping, extra or unordered suite start.';$active=$Matches[1];$buffer.Clear()}
    if($active){$buffer.Add($line)}
    if($line -match '^SUITE PASS:(.+)$'){Require ($active -and $Matches[1] -eq $active) 'Mismatched suite completion.';$index++;[IO.File]::WriteAllLines((Join-Path $split ('{0:D2}-{1}.log' -f $index,$active)),$buffer.ToArray(),[Text.UTF8Encoding]::new($false));$active=$null}
}
Require (-not $active -and $index -eq $count) 'Incomplete serial suite sequence.'
[ordered]@{Passed=$true;SingleContinuousRun=$true;OriginalSuites=79;NewSuites=8;TotalSuites=$count;PrimitiveChecks=40;ObservedOwnedProcesses=@($owned.Processes).Count;ObserverCoverage='20ms polling may miss short-lived normal children; ordered coordinator markers establish every suite result.';AllObservedProcessesExitedZero=$true;AutomaticRetries=0;NativeDumps=0;StderrBytes=0;SourcesUnchanged=$true;TestSHA256=$result.TestSHA256;DesktopSamples=$desktop.Count;LockSamples=$lockCount;HistoricalMinimizeRootCauseClaimed=$false;VerifiedUtc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'verification.json') -Encoding UTF8
Write-Output ('PASS: one continuous run; '+$count+' ordered unique suites, 40 primitive checks, '+@($owned.Processes).Count+' observed clean processes, no retries/native dumps, unchanged sources and complete logs.')
