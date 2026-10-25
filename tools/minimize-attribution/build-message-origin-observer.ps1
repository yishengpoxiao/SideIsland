param([string]$OutputDirectory='.runtime\own-thread-message-origin-observer',[switch]$SelfCheck)
$ErrorActionPreference='Stop'
$projectPath=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if(-not [IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory=Join-Path $projectPath $OutputDirectory}
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $out.StartsWith($projectPath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Build output must remain in project.'}
if(Test-Path -LiteralPath $out){throw 'Fresh build output required.'}
[IO.Directory]::CreateDirectory($out)|Out-Null
$source=Join-Path $PSScriptRoot 'OwnThreadMessageOriginObserver.cs'
$hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
$target=Join-Path $out 'MessageOriginObserverSelfCheck.exe'
& $compiler /nologo /langversion:5 /target:exe /main:CodexIsland.MessageOriginObserverSelfCheck ('/out:'+$target) ('/reference:'+(Join-Path $framework 'System.dll')) ('/reference:'+(Join-Path $framework 'System.Core.dll')) $source 2>&1|Tee-Object -FilePath (Join-Path $out 'compile.log')
if($LASTEXITCODE -ne 0){throw 'Observer compilation failed.'}
if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $hash){throw 'Compile input changed.'}
Copy-Item -LiteralPath $source -Destination (Join-Path $out 'OwnThreadMessageOriginObserver.compiled-source.cs')
[ordered]@{CompiledUtc=[DateTime]::UtcNow.ToString('o');UiExecuted=$false;HooksInstalled=$false;Source=$source;SourceSHA256=$hash;Target=$target;TargetSHA256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $out 'build.json') -Encoding UTF8
if($SelfCheck){& $target --self-check 2>&1|Tee-Object -FilePath (Join-Path $out 'self-check.log');if($LASTEXITCODE -ne 0){throw 'Observer non-UI self-check failed.'}}
Write-Output ('Compiled only; no UI or hooks installed: '+$target)
