param([string]$OutputDirectory='.runtime\posted-attribution-analysis',[switch]$SelfCheck)
$ErrorActionPreference='Stop'
$projectPath=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if(-not [IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory=Join-Path $projectPath $OutputDirectory}
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $out.StartsWith($projectPath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Project output required.'}
if(Test-Path -LiteralPath $out){throw 'Fresh output required.'}
[IO.Directory]::CreateDirectory($out)|Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
$source=Join-Path $PSScriptRoot 'AnalyzePostedCalibration.cs'
$hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$target=Join-Path $out 'AnalyzePostedCalibration.exe'
& $compiler /nologo /langversion:5 /target:exe /main:AnalyzePostedCalibration ('/out:'+$target) ('/reference:'+(Join-Path $framework 'System.dll')) ('/reference:'+(Join-Path $framework 'System.Core.dll')) ('/reference:'+(Join-Path $framework 'System.Web.Extensions.dll')) $source 2>&1|Tee-Object -FilePath (Join-Path $out 'compile.log')
if($LASTEXITCODE -ne 0){throw 'Posted analyzer compile failed.'}
if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $hash){throw 'Compile input changed.'}
Copy-Item -LiteralPath $source -Destination (Join-Path $out 'AnalyzePostedCalibration.compiled-source.cs')
[ordered]@{CompiledUtc=[DateTime]::UtcNow.ToString('o');OfflineOnly=$true;UiExecuted=$false;Source=$source;SourceSHA256=$hash;Target=$target;TargetSHA256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $out 'build.json') -Encoding UTF8
if($SelfCheck){& $target --self-check 2>&1|Tee-Object -FilePath (Join-Path $out 'self-check.log');if($LASTEXITCODE -ne 0){throw 'Posted analyzer self-check failed.'}}
