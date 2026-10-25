param()
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$diagnosticOutput = Join-Path $projectPath '.runtime\native-diagnostics'
New-Item -ItemType Directory -Path $diagnosticOutput -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$library = Join-Path $diagnosticOutput 'Microsoft.Diagnostics.Runtime.dll'
if (-not (Test-Path -LiteralPath $library)) { throw 'Existing local ClrMD 1.1 library is required. Use the separate get-clrmd.ps1 tool first.' }
$source = Join-Path $PSScriptRoot 'AnalyzeNativeDump.cs'
$executable = Join-Path $diagnosticOutput 'AnalyzeNativeDump.exe'
& $compiler /nologo /langversion:5 /platform:x64 /target:exe /debug:pdbonly ('/reference:' + $library) ('/out:' + $executable) $source
if ($LASTEXITCODE -ne 0) { throw 'Offline native dump analyzer compilation failed.' }
Copy-Item -LiteralPath $source -Destination (Join-Path $diagnosticOutput 'AnalyzeNativeDump.compiled-source.cs') -Force
@{
    CompiledUtc=[DateTime]::UtcNow.ToString('o')
    Compiler=$compiler
    CompilerVersion=(Get-Item -LiteralPath $compiler).VersionInfo.FileVersion
    AnalyzerSHA256=(Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    SourceSHA256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    ClrMdSHA256=(Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash
    OfflineOnly=$true
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $diagnosticOutput 'native-analyzer-build.json') -Encoding UTF8
& $executable --self-test
if ($LASTEXITCODE -ne 0) { throw 'Offline analyzer ABI self-test failed.' }
Write-Output $executable
