param([string]$OutputDirectory='.runtime\mixer-lan-backend-probe-001')
$ErrorActionPreference='Stop'
$project=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
if(-not [IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory=Join-Path $project $OutputDirectory}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $output.StartsWith($project+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Project output required.'}
if(Test-Path -LiteralPath $output){throw 'Fresh isolated build directory required.'}
New-Item -ItemType Directory -Path $output|Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
$sources=@('src\AudioMixerService.cs','src\LanTransferService.cs','tests\LanTransferTests.cs','tools\feature-validation\MixerLanBackendProbe.cs')|ForEach-Object{Join-Path $project $_}
$hashes=@($sources|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
$references=@('System.dll','System.Core.dll','System.Web.Extensions.dll')|ForEach-Object{'/reference:'+(Join-Path $framework $_)}
$exe=Join-Path $output 'MixerLanBackendProbe.exe'
& $compiler /nologo /langversion:5 /platform:x64 /target:exe /main:CodexIsland.MixerLanBackendProbe ('/out:'+$exe) $references $sources 2>&1|Tee-Object -FilePath (Join-Path $output 'compile.log')
if($LASTEXITCODE -ne 0){throw 'Backend probe build failed.'}
foreach($source in $hashes){if((Get-FileHash -LiteralPath $source.Path -Algorithm SHA256).Hash -ne $source.SHA256){throw 'Build source changed.'}}
[ordered]@{CompiledUtc=[DateTime]::UtcNow.ToString('o');Executable=$exe;SHA256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash;Sources=$hashes;UiExecuted=$false;AudioChanged=$false}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'build-manifest.json')
Write-Output $exe
