param([string]$OutputDirectory='.runtime\minimize-attribution',[switch]$SelfCheck)
$ErrorActionPreference='Stop'
$projectPath=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if(-not [IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory=Join-Path $projectPath $OutputDirectory}
$outputPath=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $outputPath.StartsWith($projectPath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Build output must remain in project.'}
[IO.Directory]::CreateDirectory($outputPath)|Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
$source=Join-Path $PSScriptRoot 'Win32kAttributionCollector.cs'
$sourceHash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$dependencyRoot=Join-Path $projectPath '.runtime\minimize-attribution-deps'
$traceRoot=Join-Path $dependencyRoot 'traceevent2\lib\net45'
$dependencyPaths=@(Get-ChildItem -LiteralPath $traceRoot -File -Filter '*.dll'|ForEach-Object FullName)
$dependencyPaths+=Join-Path $dependencyRoot 'unsafe\lib\netstandard1.0\System.Runtime.CompilerServices.Unsafe.dll'
foreach($path in $dependencyPaths){Copy-Item -LiteralPath $path -Destination $outputPath -Force}
$referencePaths=@('System.dll','System.Core.dll','System.Web.Extensions.dll')|ForEach-Object{Join-Path $framework $_}
$referencePaths+=Join-Path $traceRoot 'Microsoft.Diagnostics.Tracing.TraceEvent.dll'
$referencePaths+=Join-Path $traceRoot 'Microsoft.Diagnostics.FastSerialization.dll'
$references=@($referencePaths|ForEach-Object{'/reference:'+$_})
$target=Join-Path $outputPath 'Win32kAttributionCollector.exe'
& $compiler /nologo /langversion:5 /target:exe /main:Win32kAttributionCollector ('/out:'+$target) $references $source 2>&1|Tee-Object -FilePath (Join-Path $outputPath 'compile.log')
if($LASTEXITCODE-ne 0){throw 'Collector compilation failed.'}
if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash-ne $sourceHash){throw 'Source changed while compiling.'}
Copy-Item -LiteralPath $source -Destination (Join-Path $outputPath 'Win32kAttributionCollector.compiled-source.cs') -Force
[ordered]@{Status='compile_only_passed';CompiledUtc=[DateTime]::UtcNow.ToString('o');UiExecuted=$false;SessionStarted=$false;Source=$source;SourceSHA256=$sourceHash;Target=$target;TargetSHA256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash;Compiler=$compiler;CompilerVersion=(Get-Item -LiteralPath $compiler).VersionInfo.FileVersion;BuildScriptSHA256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash;Dependencies=@(foreach($path in $dependencyPaths){[ordered]@{Path=$path;SHA256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}})}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $outputPath 'collector-build.json') -Encoding UTF8
if($SelfCheck){& $target --self-check 2>&1|Tee-Object -FilePath (Join-Path $outputPath 'self-check.log');if($LASTEXITCODE-ne 0){throw 'Non-session self-check failed.'}}
Write-Output ('Compiled only; no session/UI launched: '+$target)
