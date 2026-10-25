param([string]$OutputDirectory='.runtime\minimize-origin-calibration-v5',[switch]$SelfCheck)
$ErrorActionPreference='Stop'
$projectPath=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if(-not [IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory=Join-Path $projectPath $OutputDirectory}
$out=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($out)|Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
$references=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xaml.dll','System.Runtime.dll')|ForEach-Object{Join-Path $framework $_}
$references+=@('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll')|ForEach-Object{Join-Path (Join-Path $framework 'WPF') $_}
$references+=@('Windows.UI.winmd','Windows.Foundation.winmd','Windows.ApplicationModel.winmd','Windows.Media.winmd','Windows.Storage.winmd','Windows.Graphics.winmd','Windows.Globalization.winmd')|ForEach-Object{Join-Path (Join-Path $env:WINDIR 'System32\WinMetadata') $_}
$sources=@(Get-ChildItem -LiteralPath (Join-Path $projectPath 'src') -File -Filter '*.cs' -Recurse|Sort-Object FullName|ForEach-Object FullName)+@(Join-Path $projectPath 'tests\NativeWindowStateTrace.cs')+@(Join-Path $projectPath 'tests\NativeMinimizeAttribution.cs')+@(Join-Path $PSScriptRoot 'OwnThreadMessageOriginObserver.cs')+@(Join-Path $PSScriptRoot 'MinimizeOriginCalibrationV5.cs')
$hashes=@(foreach($path in $sources){[ordered]@{Path=$path;SHA256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}})
$target=Join-Path $out 'NativeTests.exe'
$options=@('/nologo','/langversion:5','/target:exe','/main:CodexIsland.MinimizeOriginCalibrationV5',('/out:'+$target))
& $compiler ($options+@($references|ForEach-Object{'/reference:'+$_})+$sources) 2>&1|Tee-Object -FilePath (Join-Path $out 'compile.log')
if($LASTEXITCODE-ne 0){throw 'Minimize origin calibration compilation failed'}
foreach($item in $hashes){if((Get-FileHash -LiteralPath $item.Path -Algorithm SHA256).Hash-ne $item.SHA256){throw ('Input changed during compile: '+$item.Path)}}
[ordered]@{CompiledUtc=[DateTime]::UtcNow.ToString('o');Entry='CodexIsland.MinimizeOriginCalibrationV5';Target=$target;TargetSHA256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash;Sources=$hashes;Compiler=$compiler;CompilerOptions=$options;UiExecuted=$false;SystemSettingsChanged=$false;SourceHashesStable=$true}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $out 'build-manifest.json') -Encoding UTF8
if($SelfCheck){& $target --self-check 2>&1|Tee-Object -FilePath (Join-Path $out 'self-check.log');if($LASTEXITCODE-ne 0){throw 'Non-UI selfcheck failed'}}
Write-Output ('Compiled only; no UI/hooks launched: '+$target)
