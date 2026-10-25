param([string]$OutputDirectory='.runtime/function-switch-pose',[switch]$SelfCheck)
$ErrorActionPreference='Stop'
$projectPath=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if(-not [IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory=Join-Path $projectPath $OutputDirectory}
$outputPath=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputPath)|Out-Null
$compilerRoot=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler=Join-Path $compilerRoot 'csc.exe'
$references=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xaml.dll','System.Runtime.dll')|ForEach-Object{Join-Path $compilerRoot $_}
$references+=@('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll')|ForEach-Object{Join-Path (Join-Path $compilerRoot 'WPF') $_}
$references+=@('Windows.UI.winmd','Windows.Foundation.winmd','Windows.ApplicationModel.winmd','Windows.Media.winmd','Windows.Storage.winmd','Windows.Graphics.winmd','Windows.Globalization.winmd')|ForEach-Object{Join-Path (Join-Path $env:WINDIR 'System32/WinMetadata') $_}
$source=Join-Path $PSScriptRoot 'FunctionSwitchPoseProbe.cs'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $projectPath 'src') -File -Filter '*.cs' -Recurse|Sort-Object FullName|ForEach-Object FullName)+@($source)
$hashes=@(foreach($inputPath in $sources){[ordered]@{Path=$inputPath;SHA256=(Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash}})
$target=Join-Path $outputPath 'NativeTests.exe'
$options=@('/nologo','/langversion:5','/target:exe','/main:CodexIsland.FunctionSwitchPoseProbe',('/out:'+$target))
& $compiler ($options+@($references|ForEach-Object{'/reference:'+$_})+$sources) 2>&1|Tee-Object -FilePath (Join-Path $outputPath 'compile.log')
if($LASTEXITCODE -ne 0){throw 'Function-switch pose probe compilation failed.'}
foreach($item in $hashes){if((Get-FileHash -LiteralPath $item.Path -Algorithm SHA256).Hash -ne $item.SHA256){throw ('Compile input changed; rebuild after production edits finish: '+$item.Path)}}
[ordered]@{Status='compile_only_passed';CompiledUtc=[DateTime]::UtcNow.ToString('o');Entry='CodexIsland.FunctionSwitchPoseProbe';Target=$target;TargetSHA256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash;Compiler=$compiler;CompilerVersion=(Get-Item -LiteralPath $compiler).VersionInfo.FileVersion;CompilerOptions=$options;Sources=$hashes;BuildScriptSHA256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash;SourceHashesStableDuringCompile=$true;RegressionSuiteInvoked=$false;UiExecuted=$false;DefaultDeadlineSeconds=60;DefaultMatrix='zh,en x light,dark x391,560';SmallMatrix='zh/dark/391,en/light/560';Routes='all|tools|picker|pages|overlap';RTBUsed=$false;GlyphRunBaselineRecorded=$true}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $outputPath 'function-switch-pose-build.json') -Encoding UTF8
if($SelfCheck){& $target --self-check 2>&1|Tee-Object -FilePath (Join-Path $outputPath 'self-check.log');if($LASTEXITCODE -ne 0){throw 'Non-UI self-check failed.'}}
Write-Output ('Compiled only, no UI launched: '+$target)
