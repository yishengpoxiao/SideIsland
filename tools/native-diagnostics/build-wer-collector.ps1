param()
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outputPath = Join-Path $projectPath '.runtime\native-diagnostics'
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /langversion:5 /platform:x64 /target:exe /reference:System.Web.Extensions.dll /reference:System.Xml.dll ('/out:' + (Join-Path $outputPath 'WerOwnDumpCollector.exe')) (Join-Path $PSScriptRoot 'WerOwnDumpCollector.cs')
if ($LASTEXITCODE -ne 0) { throw 'WER own-process dump collector compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'WerOwnDumpCollector.cs') -Destination (Join-Path $outputPath 'WerOwnDumpCollector.compiled-source.cs') -Force
Write-Output (Join-Path $outputPath 'WerOwnDumpCollector.exe')
