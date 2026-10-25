param([Parameter(Mandatory=$true)][string]$RunDirectory)
$ErrorActionPreference='Stop'
$projectDirectory=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$runDirectory=(Resolve-Path -LiteralPath $RunDirectory).Path
$evidenceRoot=Join-Path $projectDirectory 'artifacts\minimize-attribution'
if(-not $runDirectory.StartsWith($evidenceRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Use an existing run within project artifacts/minimize-attribution.'}
for($directory=[IO.DirectoryInfo]$runDirectory;$directory -and $directory.FullName.Length -ge $evidenceRoot.Length;$directory=$directory.Parent){if(($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Evidence path may not traverse a reparse point.'}}
function Require([bool]$condition,[string]$message){if(-not $condition){throw $message}}
function Read-Json([string]$relative){Get-Content -LiteralPath (Join-Path $runDirectory $relative) -Raw -Encoding UTF8|ConvertFrom-Json}
function Has-Property($object,[string]$name){return $null -ne $object.PSObject.Properties[$name]}
function Zero-Field($object,[string]$name){Require ((Has-Property $object $name) -and $object.$name -eq 0) ('Missing/nonzero '+$name)}
function False-Field($object,[string]$name){Require ((Has-Property $object $name) -and $object.$name -eq $false) ('Missing/non-false '+$name)}
function True-Field($object,[string]$name){Require ((Has-Property $object $name) -and $object.$name -eq $true) ('Missing/non-true '+$name)}
$captureRoot=Join-Path $runDirectory 'coordinator'
$run=Read-Json 'result.json'
$capture=Read-Json 'coordinator\collector-result.json'
$owned=Read-Json 'coordinator\owned-processes.json'
$expected=@(Read-Json 'expected-suite-order.json')
$lines=@(Get-Content -LiteralPath (Join-Path $captureRoot 'stdout.log') -Encoding UTF8)
$starts=@($lines|ForEach-Object{if($_ -match '^SUITE START:(.+)$'){$Matches[1]}})
$passes=@($lines|ForEach-Object{if($_ -match '^SUITE PASS:(.+)$'){$Matches[1]}})
True-Field $run 'Completed';True-Field $run 'SingleCoordinatorInvocation'
Zero-Field $run 'CollectorExitCode';Zero-Field $run 'EtwCollectorExitCode';Zero-Field $run 'AutomaticRetries'
False-Field $run 'HistoricalSenderProven'
foreach($field in @('TestExecutableUnchanged','SourcesUnchanged','CandidateAppUnchanged')){True-Field $run $field}
Require ((Get-FileHash -LiteralPath (Join-Path $runDirectory 'NativeTests.exe') -Algorithm SHA256).Hash -eq $run.TestExecutableSHA256) 'Preserved test executable hash differs.'
$before=@(Read-Json 'sources-before.json');$after=@(Read-Json 'sources-after.json')
Require ($before.Count -gt 0 -and $before.Count -eq $after.Count) 'Source manifests are missing or differ in size.'
foreach($source in $before){
    $matches=@($after|Where-Object{$_.Path -eq $source.Path})
    Require ($matches.Count -eq 1 -and $matches[0].ExpectedSHA256 -eq $source.SHA256 -and $matches[0].ActualSHA256 -eq $source.SHA256) ('Source manifest changed: '+$source.Path)
}
True-Field $capture 'FullRegression';True-Field $capture 'ChildExited';False-Field $capture 'DeadlineTerminated';False-Field $capture 'AutomaticRetry'
Require ($capture.ChildExitHex -eq '0x00000000' -and @($capture.Failures).Count -eq 0 -and @($capture.CapturedFiles).Count -eq 0) 'Collector recorded failure, timeout or WER evidence requiring investigation.'
Require ($expected.Count -eq 79 -and $starts.Count -eq 79 -and $passes.Count -eq 79) 'Expected exactly 79 suite start/pass markers.'
Require (@($expected|Sort-Object -Unique).Count -eq 79 -and @($starts|Sort-Object -Unique).Count -eq 79 -and @($passes|Sort-Object -Unique).Count -eq 79) 'Repeated suite name.'
for($index=0;$index -lt 79;$index++){Require ($starts[$index] -eq $expected[$index] -and $passes[$index] -eq $expected[$index]) ('Unexpected suite order at '+($index+1))}
Require (@($lines|Where-Object{$_ -eq 'PASS: 79 isolated suite workers completed; no automatic retries.'}).Count -eq 1) 'Default coordinator final completion missing/repeated.'
Require (@($lines|Where-Object{$_ -match '^PASS: 40 quota/display checks'}).Count -eq 1) 'Original forty primitive checks missing/repeated.'
Require ((Get-Item -LiteralPath (Join-Path $captureRoot 'stderr.log')).Length -eq 0) 'Coordinator stderr requires investigation.'
foreach($relative in @('collector-stderr.log','etw.stderr.log')){Require ((Get-Item -LiteralPath (Join-Path $runDirectory $relative)).Length -eq 0) ('Nonempty '+$relative+' requires investigation.')}
$coordinators=@($owned.Processes|Where-Object{$_.Role -eq 'coordinator'})
Require ($coordinators.Count -eq 1 -and @($owned.Processes).Count -eq 80) 'Expected one coordinator and 79 owned workers.'
Require (@($owned.Processes|Where-Object{-not $_.Exited -or $_.ExitCode -ne 0 -or $_.ExitHex -ne '0x00000000'}).Count -eq 0) 'An owned process did not exit cleanly.'
$workers=@($owned.Processes|Where-Object{$_.ParentProcessId -eq $coordinators[0].ProcessId}|Sort-Object{[Convert]::ToUInt64($_.CreationFileTime.Substring(2),16)})
Require ($workers.Count -eq 79) 'Observer did not record all direct suite workers.'
Require ($expected[71] -eq 'SettingsAndFilterMotionTests') 'Expected the original settings suite at position 72.'

$etw=Read-Json 'etw\summary.json'
foreach($field in @('Started','ProcessingReturned','CleanupConfirmed','StopRequestedByWatch')){True-Field $etw $field}
foreach($field in @('EtlWritten','GlobalKeyboardHook','OtherSessionStopped','LogLimitReached','DataLossDetected')){False-Field $etw $field}
foreach($field in @('EventsLost','SessionEventsLostBeforeStop','SourceReportedEventsLost','CallbackErrors','WatchParseErrors','ExpiredSpans','AmbiguousSpanStops')){Zero-Field $etw $field}
Require ($null -eq $etw.CleanupError -and $null -eq $etw.ErrorType -and $null -eq $etw.LostCounterReadError) 'ETW collector recorded an error.'
Require ($etw.StopReason -eq 'watch-stop-request' -and $etw.TargetUpdates -gt 0 -and $etw.MatchedRecords -gt 0) 'ETW did not observe targets or stopped before wrapper cleanup.'
$etwRows=@(Get-Content -LiteralPath (Join-Path $runDirectory 'etw\events.jsonl') -Encoding UTF8|ForEach-Object{$_|ConvertFrom-Json})
$etwOwned=@($etwRows|Where-Object{Has-Property $_ 'TargetProcessId'})
Require ($etwOwned.Count -gt 0) 'ETW has no owned HWND records.'

$native=Read-Json 'native-trace\summary.json'
$owner=Read-Json 'native-trace\owner.json'
True-Field $native 'Completed';False-Field $native 'LimitReached';False-Field $native 'DataLossDetected';Zero-Field $native 'Errors'
foreach($field in @('NoInputOrWindowStateChanges','NoExternalHooksOrSessions','CriticalEventsImmediatelyFlushed')){True-Field $native $field}
False-Field $native 'OriginalHistoricalSenderProven';False-Field $owner 'OriginalHistoricalSenderProven';True-Field $owner 'OnlyOwnWindows'
Require ($null -eq $native.FirstError -and $native.Lines -gt 0 -and $native.Bytes -gt 0 -and $native.FlushCount -gt 0) 'Native observer incomplete or erroneous.'
Require ($native.ProcessId -eq $workers[71].ProcessId -and $owner.ProcessId -eq $workers[71].ProcessId -and $owner.ProcessStartUtc -eq $native.ProcessStartUtc) 'Native trace does not belong to the exact original suite 72 process.'
# Recent PowerShell JSON readers materialize ISO timestamps as DateTime. Do not
# pass that object through Parse(string): culture coercion drops fractional ticks.
$ownerStart=if($owner.ProcessStartUtc -is [DateTime]){$owner.ProcessStartUtc.ToUniversalTime()}else{[DateTime]::Parse([string]$owner.ProcessStartUtc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)}
Require ([UInt64]$ownerStart.ToFileTimeUtc() -eq [Convert]::ToUInt64($workers[71].CreationFileTime.Substring(2),16)) 'Native observer process start time differs from WER observer identity.'
Require ($native.QpcFrequency -gt 0 -and $native.QpcFrequency -eq $owner.QpcFrequency) 'Native QPC clock missing/mismatched.'
$nativePath=Join-Path $runDirectory 'native-trace\events.jsonl'
$nativeRows=@(Get-Content -LiteralPath $nativePath -Encoding UTF8|ForEach-Object{$_|ConvertFrom-Json})
Require ($nativeRows.Count -eq $native.Lines -and (Get-Item -LiteralPath $nativePath).Length -eq $native.Bytes) 'Native observer line/byte counts do not match saved file.'
$roles=@('settings-motion-dialog','settings-motion-owner')
$actualRoles=@($nativeRows|ForEach-Object Label|Sort-Object -Unique)
Require ($actualRoles.Count -eq 2 -and @($actualRoles|Where-Object{$roles -notcontains $_}).Count -eq 0) 'Native trace does not contain exactly the original dialog and owner roles.'
Require (@($nativeRows|Where-Object{$_.ReceiverProcessId -ne $native.ProcessId -or $_.QpcFrequency -ne $native.QpcFrequency -or $_.Qpc -le 0 -or $_.CurrentThreadId -le 0}).Count -eq 0) 'Native event process/thread/clock identity differs.'
foreach($role in $roles){
    $roleRows=@($nativeRows|Where-Object{$_.Label -eq $role})
    $published=@($roleRows|Where-Object{$_.Event -eq 'target-published'})
    $messages=@($roleRows|Where-Object{$_.Event -eq 'WM'})
    Require ($published.Count -eq 1 -and $messages.Count -gt 0) ('Missing published own target or native messages: '+$role)
    Require ($published[0].Qpc -le ($messages|Measure-Object Qpc -Minimum).Minimum) ('Native hook entry precedes target publication: '+$role)
    $hwnd=$published[0].Detail.Hwnd
    Require (@($messages|Where-Object{$_.Detail.Hwnd -ne $hwnd}).Count -eq 0) ('Native role changed HWND: '+$role)
    Require (@($etwOwned|Where-Object{$_.TargetProcessId -eq $native.ProcessId -and $_.TargetRole -eq $role -and $_.TargetHwnd -eq $hwnd}).Count -gt 0) ('ETW did not record exact native target: '+$role)
}
Require (@($etwOwned|Where-Object{$_.TargetProcessId -ne $native.ProcessId -or $roles -notcontains $_.TargetRole}).Count -eq 0) 'ETW contains a target outside the two original fixture roles.'
$minimized=@($nativeRows|Where-Object{
    if($_.Event -ne 'WM'){return $false}
    if($_.Detail.Message -eq 5){return $_.Detail.W -eq '0x1'}
    if($_.Detail.Message -eq 0x112){return ([Convert]::ToUInt64($_.Detail.W.Substring(2),16) -band [UInt64]0xFFF0) -eq [UInt64]0xF020}
    return $false
})
Require ($minimized.Count -eq 0) 'Actual native minimize request/transition detected; analyze original records instead of declaring a clean regression.'
foreach($role in $roles){Require (@($lines|Where-Object{$_ -match ('^TRACE SUMMARY: '+[regex]::Escape($role)+' .*minimized-messages=0 state-events=0 .*failure-dumped=False')}).Count -eq 1) ('Missing clean original bounded trace summary: '+$role)}

# Only after all assertions pass, create derived logs without rewriting originals.
$suiteRoot=Join-Path $runDirectory 'suites'
foreach($relative in @('suites','verification.json','suite-results.json','coordinator-summary.log')){Require (-not (Test-Path -LiteralPath (Join-Path $runDirectory $relative))) ('Derived output already exists: '+$relative)}
New-Item -ItemType Directory -Path $suiteRoot|Out-Null
$buffer=New-Object 'System.Collections.Generic.List[string]';$summary=New-Object 'System.Collections.Generic.List[string]'
$activeName=$null;$completed=0;$suiteRecords=@()
foreach($line in $lines){
    if($line -match '^SUITE START:(.+)$'){Require (-not $activeName) 'Overlapping suite output.';$activeName=$Matches[1];$buffer.Clear()}
    if($activeName){$buffer.Add($line)}else{$summary.Add($line)}
    if($line -match '^SUITE PASS:(.+)$'){
        Require ($activeName -eq $Matches[1]) 'Mismatched suite output boundary.'
        $name='{0:D2}-{1}.stdout.log' -f ($completed+1),$activeName
        [IO.File]::WriteAllLines((Join-Path $suiteRoot $name),$buffer.ToArray(),[Text.UTF8Encoding]::new($false))
        $suiteRecords += [ordered]@{Index=$completed+1;Suite=$activeName;ProcessId=$workers[$completed].ProcessId;CreationFileTime=$workers[$completed].CreationFileTime;ExitCode=$workers[$completed].ExitCode;ExitHex=$workers[$completed].ExitHex;Log='suites/'+$name}
        $completed++;$activeName=$null
    }
}
Require (-not $activeName -and $completed -eq 79) 'Incomplete suite split.'
[IO.File]::WriteAllLines((Join-Path $runDirectory 'coordinator-summary.log'),$summary.ToArray(),[Text.UTF8Encoding]::new($false))
$suiteRecords|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $runDirectory 'suite-results.json') -Encoding UTF8
$desktopSamples=@(Get-Content -LiteralPath (Join-Path $runDirectory 'input-desktop.jsonl') -Encoding UTF8|ForEach-Object{$_|ConvertFrom-Json})
[ordered]@{
    Passed=$true;SingleContinuousRun=$true;RegisteredSuites=79;StartedSuites=79;PassedSuites=79;OriginalPrimitiveChecks=40
    CoordinatorProcessId=$coordinators[0].ProcessId;ObservedDirectSuiteProcesses=79;ObservedTotalProcesses=80;AllObservedExitCodesZero=$true
    OrderedUniqueCompletionMarkers=$true;AutomaticRetries=0;WERDumpCount=0;StderrBytes=0;SourcesUnchanged=$true
    TestExecutableSHA256=$run.TestExecutableSHA256;AppExecutableSHA256=$run.AppExecutableSHA256;CandidateAppSHA256=$run.CandidateAppSHA256
    EtwCleanupConfirmed=$true;EtwLostEvents=0;EtwCallbackErrors=0;EtwLimitReached=$false;EtwMatchedRecords=$etw.MatchedRecords
    NativeObserverProcessId=$native.ProcessId;NativeObserverLines=$native.Lines;NativeObserverRoles=$actualRoles;NativeMinimizeEvents=0
    InputDesktopSamples=$desktopSamples.Count;InputDesktopStates=@($desktopSamples|ForEach-Object InputDesktop|Sort-Object -Unique)
    LockScreenForegroundSamples=@($desktopSamples|Where-Object{$_.Foreground.ProcessName -eq 'LockApp' -or $_.Foreground.Class -like 'LockScreen*'}).Count
    HistoricalSenderProven=$false;HistoricalAttributionMeaning='Clean regression and diagnostic calibration do not identify the historical first-run sender.'
    StartedUtc=$run.StartedUtc;EndedUtc=$run.EndedUtc;VerifiedUtc=[DateTime]::UtcNow.ToString('o')
}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $runDirectory 'verification.json') -Encoding UTF8
Write-Output 'PASS: one continuous default-coordinator run, 79 ordered suites, 40 primitive checks, 80 observed processes exit zero, complete owned-window diagnostics; historical sender remains unproven.'
