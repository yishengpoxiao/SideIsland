param([string]$OutputDirectory='.runtime\glyph-opacity-probe')
$ErrorActionPreference='Stop'
$projectPath=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if(-not [IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory=Join-Path $projectPath $OutputDirectory}
$outputPath=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputPath)|Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
$source=Join-Path $PSScriptRoot 'GlyphOpacityProbe.cs'
$sourceHash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$target=Join-Path $outputPath 'NativeTests.exe'
$references=@('System.dll','System.Core.dll','System.Drawing.dll','System.Web.Extensions.dll','System.Xaml.dll')|ForEach-Object{'/reference:'+(Join-Path $framework $_)}
$references+=@('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')|ForEach-Object{'/reference:'+(Join-Path (Join-Path $framework 'WPF') $_)}
& $compiler /nologo /langversion:5 /target:exe /main:GlyphOpacityProbe ('/out:'+$target) $references $source 2>&1|Tee-Object -FilePath (Join-Path $outputPath 'compile.log')
if($LASTEXITCODE-ne 0){throw 'Glyph opacity probe compilation failed.'}
if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash-ne $sourceHash){throw 'Probe source changed during compilation.'}
[ordered]@{Status='compile_only_passed';UiExecuted=$false;CompiledUtc=[DateTime]::UtcNow.ToString('o');Source=$source;SourceSHA256=$sourceHash;Target=$target;TargetSHA256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash;Compiler=$compiler;Standalone=$true;ProductSourceChanged=$false;NativePixels=$true;RenderTargetBitmapUsed=$false;OpacityLevels=@(.35,.7,.99,1);FontFamilies=@('Segoe UI','Microsoft YaHei UI')}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $outputPath 'build-manifest.json') -Encoding UTF8
Write-Output ('Compiled only; no UI executed: '+$target)
