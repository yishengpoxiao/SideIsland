param([string]$RunName='quick-001',[switch]$Full,[string[]]$Suites)
$ErrorActionPreference='Stop'
$projectRoot=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Set-Location -LiteralPath $projectRoot
if($RunName -notmatch '^[a-z0-9-]+$'){throw 'Use a simple fresh run name.'}
$root=Join-Path $projectRoot ('artifacts\features-1.13\'+$RunName)
if(Test-Path -LiteralPath $root){throw 'Fresh run directory required.'}
New-Item -ItemType Directory -Path $root|Out-Null
$result=[ordered]@{StartedUtc=[DateTime]::UtcNow.ToString('o');FullRegression=[bool]$Full;AutomaticRetries=0;Completed=$false;HistoricalMinimizeRootCauseClaimed=$false}
$stopped=$false;$collector=$null;$records=@()
try{
Add-Type -TypeDefinition (Get-Content tools\common\InputDesktopProbe.cs -Raw)
Add-Type -TypeDefinition (Get-Content tools\common\AlphaObscurationProbe.cs -Raw)
Add-Type -AssemblyName System.Windows.Forms
$test=Join-Path $projectRoot '.runtime\tests\NativeTests.exe'
$wer=Join-Path $projectRoot '.runtime\native-diagnostics\WerOwnDumpCollector.exe'
$app=Join-Path $projectRoot 'packaging\app\CodexIsland.exe'
$before=@(Get-ChildItem -LiteralPath src,tests -File -Filter '*.cs' -Recurse|ForEach-Object{[ordered]@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
$before|ConvertTo-Json|Set-Content (Join-Path $root 'sources-before.json') -Encoding UTF8
Copy-Item -LiteralPath $test -Destination (Join-Path $root 'NativeTests.exe')
$expected=@([regex]::Matches((Get-Content tests\NativeTests.cs -Raw),'new TestSuite\("([A-Za-z0-9_]+)"')|ForEach-Object{$_.Groups[1].Value})
$expected|ConvertTo-Json|Set-Content (Join-Path $root 'expected-suite-order.json') -Encoding UTF8
$result.TestSHA256=(Get-FileHash $test -Algorithm SHA256).Hash
@($PSCommandPath,(Join-Path $PSScriptRoot 'verify-feature-regression.ps1'),$wer,'tools\common\InputDesktopProbe.cs','tools\common\AlphaObscurationProbe.cs')|ForEach-Object{[ordered]@{Path=(Resolve-Path -LiteralPath $_).Path;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}}|ConvertTo-Json|Set-Content (Join-Path $root 'observation-tools.json') -Encoding UTF8
function Sample-Desktop{
    $points=@([System.Windows.Forms.Screen]::AllScreens|ForEach-Object{$area=$_.WorkingArea; [AlphaObscurationProbe]::At($area.Left+[int]($area.Width/2),$area.Top+[int]($area.Height/2));[AlphaObscurationProbe]::At($area.Right-30,$area.Top+30)})
    $sample=[ordered]@{Utc=[DateTime]::UtcNow.ToString('o');Desktop=[OwnedInputDesktopProbe]::Read();Foreground=[AlphaObscurationProbe]::Foreground();ScreenPoints=$points}
    $sample|ConvertTo-Json -Depth 4 -Compress|Add-Content (Join-Path $root 'desktop.jsonl') -Encoding UTF8
    return $sample
}
    $initial=Sample-Desktop
    if($initial.Desktop -ne 'Default' -or @(@($initial.Foreground)+@($initial.ScreenPoints)|Where-Object{$_.ProcessName -eq 'LockApp' -or $_.Class -like 'LockScreen*'}).Count){throw 'Unlock the desktop before native UI validation.'}
    if(@(Get-ChildItem Env:CODEX_ISLAND* -ErrorAction SilentlyContinue).Count){throw 'Unexpected inherited diagnostic/data environment.'}
    $shutdown=Start-Process -FilePath $app -ArgumentList '--shutdown' -WindowStyle Hidden -Wait -PassThru
    if($shutdown.ExitCode -ne 0){throw 'App shutdown failed.'};$stopped=$true
    $shutdownClock=[Diagnostics.Stopwatch]::StartNew()
    while(@(Get-Process -Name CodexIsland -ErrorAction SilentlyContinue|Where-Object{$_.Path -eq $app}).Count -gt 0){if($shutdownClock.Elapsed.TotalSeconds -gt 8){throw 'App still running after shutdown.'};Start-Sleep -Milliseconds 100}
    $suites=if($Full){@('--full-regression')}elseif($Suites){@($Suites)}else{@('TaskActivityTests','NotificationTaskTests','AutoSceneWidgetTests','ScreenshotAnnotationTests','BatchFilesTests','AudioMixerTests','LanTransferTests','NextFeatureLayoutTests')}
    foreach($entry in $suites){if($entry -ne '--full-regression' -and $entry -notin $expected){throw ('Unknown suite: '+$entry)}}
    foreach($suite in $suites){
        $output=Join-Path $root $(if($Full){'coordinator'}else{$suite})
        $collector=Start-Process -FilePath $wer -ArgumentList @($test,$suite,$output,$(if($Full){'2400'}else{'600'})) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root ($suite.TrimStart('-')+'.collector.stdout.log')) -RedirectStandardError (Join-Path $root ($suite.TrimStart('-')+'.collector.stderr.log'))
        $null=$collector.Handle;$next=[DateTime]::UtcNow
        while(-not $collector.WaitForExit(1000)){if([DateTime]::UtcNow -ge $next){$null=Sample-Desktop;$next=[DateTime]::UtcNow.AddSeconds(5)}}
        $collector.WaitForExit();$exit=$collector.ExitCode
        $record=[ordered]@{Suite=$suite;CollectorExitCode=$exit;Output=$output;EndedUtc=[DateTime]::UtcNow.ToString('o')};$records+=$record
        $records|ConvertTo-Json -Depth 4|Set-Content (Join-Path $root 'suite-results.json') -Encoding UTF8
        if($exit -ne 0){throw ('First failing suite retained without retry: '+$suite)}
        Write-Output ('PASS '+$suite)
    }
    $result.TestUnchanged=((Get-FileHash $test -Algorithm SHA256).Hash -eq $result.TestSHA256)
    $after=@($before|ForEach-Object{[ordered]@{Path=$_.Path;ExpectedSHA256=$_.SHA256;ActualSHA256=(Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash}})
    $after|ConvertTo-Json|Set-Content (Join-Path $root 'sources-after.json') -Encoding UTF8
    $result.SourcesUnchanged=(@($after|Where-Object{$_.ExpectedSHA256 -ne $_.ActualSHA256}).Count -eq 0)
    $current=@(Get-ChildItem -LiteralPath src,tests -File -Filter '*.cs' -Recurse|ForEach-Object FullName)
    $result.SourceSetUnchanged=(@(Compare-Object @($before|ForEach-Object Path) $current).Count -eq 0)
    if(-not $result.TestUnchanged -or -not $result.SourcesUnchanged -or -not $result.SourceSetUnchanged){throw 'Source or test binary changed during execution.'}
    $result.Completed=$true
}catch{$result.Error=$_.Exception.ToString();throw}
finally{
    if($collector -and -not $collector.HasExited){$result.Completed=$false;$collector.WaitForExit()}
    try { if($stopped){Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList 'shell:AppsFolder\CodexIsland.Notifications_vg32xay9edax2!Island' -WindowStyle Hidden} } catch { $result.AppRestartError=$_.Exception.ToString() }
    $result.AppRestartRequested=$stopped;$result.EndedUtc=[DateTime]::UtcNow.ToString('o')
    $result|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'result.json') -Encoding UTF8
}
