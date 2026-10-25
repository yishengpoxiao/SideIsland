param(
    [string]$OutputDirectory=('.runtime\settings-startup-prelude-probe-build-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')),
    [string]$BaselineExecutable='.runtime\tests\NativeTests.exe',
    [string]$NativeProbeSource='tools\minimize-attribution\NativeMinimizeStackProbeV4.cs',
    [string]$NativeProbeDll='.runtime\native-minimize-stack-probe-v4\NativeMinimizeStackProbeV4.dll',
    [switch]$InstallOriginalPath,
    [switch]$SelfCheck
)
$ErrorActionPreference='Stop'
$projectPath=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
function Project-Path([string]$value){
    if(-not [IO.Path]::IsPathRooted($value)){$value=Join-Path $projectPath $value}
    $full=[IO.Path]::GetFullPath($value)
    if(-not $full.StartsWith($projectPath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Paths must stay in this project.'}
    return $full
}
$out=Project-Path $OutputDirectory
if(Test-Path -LiteralPath $out){throw 'Fresh diagnostic build directory required.'}
New-Item -ItemType Directory -Path $out|Out-Null
$baseline=Project-Path $BaselineExecutable
$expectedBaseline='8B673323C2678B39FA49B3DAB7B3D9D14CEA656B2B87A8BE7E234E70F444B87A'
if((Get-FileHash -LiteralPath $baseline -Algorithm SHA256).Hash -ne $expectedBaseline){throw 'Supply the preserved 8B6733 baseline executable; do not overwrite it with a probe.'}
Copy-Item -LiteralPath $baseline -Destination (Join-Path $out 'baseline-NativeTests.exe')
$copies=Join-Path $out 'diagnostic-sources'
New-Item -ItemType Directory -Path $copies|Out-Null
$settingsPath=Join-Path $projectPath 'tests\SettingsAndFilterMotionTests.cs'
$settingsRaw=[IO.File]::ReadAllText($settingsPath)
$guardPattern='(?m)^.*(?:private static NativeFixtureInputIsolation settingsInputIsolation;|settingsInputIsolation = NativeFixtureInputIsolation.Attach\(settingsWindow\);|if \(settingsInputIsolation != null\) \{ settingsInputIsolation.Dispose\(\); settingsInputIsolation = null; \}).*\r?\n'
if([regex]::Matches($settingsRaw,$guardPattern).Count -ne 3){throw 'Expected exactly three guard integration lines.'}
$withoutGuard=[regex]::Replace($settingsRaw,$guardPattern,'')
$utf8=[Text.UTF8Encoding]::new($false)
$sha=[Security.Cryptography.SHA256]::Create()
try{$baselineSourceHash=([BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes($withoutGuard)))).Replace('-','')}finally{$sha.Dispose()}
if($baselineSourceHash -ne '93CEE5FBA477463113F6C57CB20342F161AF47EF85D1AAEF20F72DB9A47C659B'){throw ('Removing only the three guard lines must restore original suite hash; actual='+$baselineSourceHash)}
[IO.File]::WriteAllText((Join-Path $copies 'SettingsAndFilterMotionTests.original-unguarded.cs.txt'),$withoutGuard,$utf8)
$originalCall='SettingsMatrix(settingsWindow); SettingsRapidAndInterruptions(settingsWindow);'
if(([regex]::Matches($withoutGuard,[regex]::Escape($originalCall))).Count -ne 1){throw 'Expected one original settings startup call sequence.'}
$prefix=$withoutGuard.Replace($originalCall,'SettingsMatrix(settingsWindow); return; // Diagnostic startup prefix only; not a complete suite.')
$prefixPath=Join-Path $copies 'SettingsAndFilterMotionTests.prefix.cs'
[IO.File]::WriteAllText($prefixPath,$prefix,$utf8)
$tracePath=Join-Path $projectPath 'tests\NativeWindowStateTrace.cs'
$traceRaw=[IO.File]::ReadAllText($tracePath)
$originalRegister='if (attribution != null) attribution.Register(handle);'
if(([regex]::Matches($traceRaw,[regex]::Escape($originalRegister))).Count -ne 1){throw 'Expected one native trace registration point.'}
$traceCopy=Join-Path $copies 'NativeWindowStateTrace.startup.cs'
$traceDiagnostic=$traceRaw.Replace($originalRegister,$originalRegister+"`r`n            SettingsStartupProbe.RegisterTarget(handle, name);")
$qpcEntry='long entryQpc = Stopwatch.GetTimestamp();'
if(([regex]::Matches($traceDiagnostic,[regex]::Escape($qpcEntry))).Count -ne 1){throw 'Expected one native trace entry QPC point.'}
$traceDiagnostic=$traceDiagnostic.Replace($qpcEntry,$qpcEntry+"`r`n            SettingsStartupProbe.FlushNativeCritical(message, wParam);")
$caseMark='context = value ?? "";'
if(([regex]::Matches($traceDiagnostic,[regex]::Escape($caseMark))).Count -ne 1){throw 'Expected one case Mark context point.'}
$traceDiagnostic=$traceDiagnostic.Replace($caseMark,$caseMark+"`r`n            SettingsStartupProbe.FlushNativeAtCaseMark(name);")
[IO.File]::WriteAllText($traceCopy,$traceDiagnostic,$utf8)
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
$references=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xaml.dll','System.Runtime.dll')|ForEach-Object{Join-Path $framework $_}
$references+=@('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll')|ForEach-Object{Join-Path (Join-Path $framework 'WPF') $_}
$references+=@('Windows.UI.winmd','Windows.Foundation.winmd','Windows.ApplicationModel.winmd','Windows.Media.winmd','Windows.Storage.winmd','Windows.Graphics.winmd','Windows.Globalization.winmd')|ForEach-Object{Join-Path (Join-Path $env:WINDIR 'System32\WinMetadata') $_}
$sources=@(Get-ChildItem -LiteralPath (Join-Path $projectPath 'src') -File -Filter '*.cs' -Recurse|Sort-Object FullName|ForEach-Object FullName)
$sources+=@(Get-ChildItem -LiteralPath (Join-Path $projectPath 'tests') -File -Filter '*.cs'|Where-Object{$_.Name -notin @('SettingsAndFilterMotionTests.cs','NativeWindowStateTrace.cs')}|Sort-Object FullName|ForEach-Object FullName)
$sources+=@($prefixPath,$traceCopy,(Join-Path $PSScriptRoot 'SettingsStartupPreludeProbe.cs'),(Join-Path $PSScriptRoot 'OwnThreadMessageOriginObserver.cs'),(Join-Path $PSScriptRoot 'StartupWatchdogCapture.cs'))
if($NativeProbeSource){$sources+=Project-Path $NativeProbeSource}
$sourceHashes=@($sources|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
$target=Join-Path $out 'NativeTests.exe'
$options=@('/nologo','/langversion:5','/target:exe','/main:CodexIsland.SettingsStartupProbe',('/out:'+$target))
& $compiler ($options+@($references|ForEach-Object{'/reference:'+$_})+$sources) 2>&1|Tee-Object -FilePath (Join-Path $out 'compile.log')
if($LASTEXITCODE -ne 0){throw 'Startup prefix compilation failed.'}
foreach($item in $sourceHashes){if((Get-FileHash -LiteralPath $item.Path -Algorithm SHA256).Hash -ne $item.SHA256){throw ('Compile source changed: '+$item.Path)}}
$originalExe=Join-Path $projectPath '.runtime\tests\NativeTests.exe'
if($InstallOriginalPath){
    if($NativeProbeDll){$dll=Project-Path $NativeProbeDll;Copy-Item -LiteralPath $dll -Destination (Join-Path (Split-Path $originalExe -Parent) ([IO.Path]::GetFileName($dll))) -Force}
    Copy-Item -LiteralPath $target -Destination $originalExe -Force
}
foreach($probeSourceName in @('SettingsStartupPreludeProbe.cs','OwnThreadMessageOriginObserver.cs','StartupWatchdogCapture.cs')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $probeSourceName) -Destination (Join-Path $copies ($probeSourceName+'.txt'))}
[ordered]@{CompiledUtc=[DateTime]::UtcNow.ToString('o');Main='CodexIsland.SettingsStartupProbe';OriginalPath=$originalExe;Target=$target;TargetSHA256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash;InstalledAtOriginalPath=[bool]$InstallOriginalPath;BaselineExecutableSHA256=$expectedBaseline;BaselineBackup=(Join-Path $out 'baseline-NativeTests.exe');UnguardedOriginalSuiteSourceSHA256=$baselineSourceHash;GuardLinesRemoved=3;OriginalCallReplacedOnce=$true;OriginalMatrixAndAssertionsUnchanged=$true;PickerSourceUnmodified=$true;PreludeAvailable=$true;Full79Executed=$false;SourceHashesStable=$true;Sources=$sourceHashes;NativeProbeSource=$NativeProbeSource;NativeProbeDll=$NativeProbeDll;UiExecuted=$false;HookInstalled=$false;SessionCreated=$false}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $out 'build-manifest.json') -Encoding UTF8
if($SelfCheck){& $target --self-check 2>&1|Tee-Object -FilePath (Join-Path $out 'self-check.log');if($LASTEXITCODE -ne 0){throw 'Non-UI selfcheck failed.'}}
Write-Output ('Compiled prelude/prefix diagnostic: '+$target+'; installed at original path='+[bool]$InstallOriginalPath)
Write-Output ('Preserved full-regression baseline: '+(Join-Path $out 'baseline-NativeTests.exe'))
