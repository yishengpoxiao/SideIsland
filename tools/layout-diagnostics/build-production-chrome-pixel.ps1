param([string]$OutputDirectory='.runtime\production-chrome-pixel',[switch]$SelfCheck)
$ErrorActionPreference='Stop'
$projectPath=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if(-not [IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory=Join-Path $projectPath $OutputDirectory}
$outputPath=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputPath)|Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
$references=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xaml.dll','System.Runtime.dll')|ForEach-Object{Join-Path $framework $_}
$references+=@('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')|ForEach-Object{Join-Path (Join-Path $framework 'WPF') $_}
$references+=@('Windows.UI.winmd','Windows.Foundation.winmd','Windows.ApplicationModel.winmd','Windows.Media.winmd','Windows.Storage.winmd','Windows.Graphics.winmd','Windows.Globalization.winmd')|ForEach-Object{Join-Path (Join-Path $env:WINDIR 'System32\WinMetadata') $_}
$probe=Join-Path $PSScriptRoot 'ProductionChromePixelProbe.cs'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $projectPath 'src') -File -Filter '*.cs' -Recurse|Sort-Object FullName|ForEach-Object FullName)+@($probe)
$hashes=@(foreach($sourcePath in $sources){[ordered]@{Path=$sourcePath;SHA256=(Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash}})
$target=Join-Path $outputPath 'NativeTests.exe'
$options=@('/nologo','/langversion:5','/target:exe','/main:CodexIsland.ProductionChromePixelProbe',('/out:'+$target))
& $compiler ($options+@($references|ForEach-Object{'/reference:'+$_})+$sources) 2>&1|Tee-Object -FilePath (Join-Path $outputPath 'compile.log')
if($LASTEXITCODE-ne 0){throw 'Production chrome pixel probe compilation failed.'}
foreach($item in $hashes){if((Get-FileHash -LiteralPath $item.Path -Algorithm SHA256).Hash-ne $item.SHA256){throw ('Compile input changed: '+$item.Path)}}
[ordered]@{Status='compile_only_passed';UiExecuted=$false;CompiledUtc=[DateTime]::UtcNow.ToString('o');Entry='CodexIsland.ProductionChromePixelProbe';Target=$target;TargetSHA256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash;Compiler=$compiler;CompilerVersion=(Get-Item -LiteralPath $compiler).VersionInfo.FileVersion;CompilerOptions=$options;Sources=$hashes;BuildScriptSHA256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash;Matrix='zh/dark/391,en/light/560';Scenarios=2;RoutesPerScenario=5;NativeScreenCrop=$true;RTBUsed=$false;ProductSourceChanged=$false}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $outputPath 'production-chrome-pixel-build.json') -Encoding UTF8
if($SelfCheck){& $target --self-check 2>&1|Tee-Object -FilePath (Join-Path $outputPath 'self-check.log');if($LASTEXITCODE-ne 0){throw 'Non-UI chrome probe self-check failed.'}}
Write-Output ('Compiled only; no UI launched: '+$target)
