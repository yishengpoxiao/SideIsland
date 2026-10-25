param()
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$diagnosticOutput = Join-Path $projectPath '.runtime\native-diagnostics'
New-Item -ItemType Directory -Path $diagnosticOutput -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /langversion:5 /platform:x64 /target:exe /debug:pdbonly ('/out:' + (Join-Path $diagnosticOutput 'NativeCrashProbe.exe')) (Join-Path $PSScriptRoot 'NativeCrashProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native diagnostic launcher compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NativeCrashProbe.cs') -Destination (Join-Path $diagnosticOutput 'NativeCrashProbe.compiled-source.cs') -Force
@{
    CompiledUtc=[DateTime]::UtcNow.ToString('o')
    Compiler=$compiler
    CompilerVersion=(Get-Item -LiteralPath $compiler).VersionInfo.FileVersion
    LauncherSHA256=(Get-FileHash -LiteralPath (Join-Path $diagnosticOutput 'NativeCrashProbe.exe') -Algorithm SHA256).Hash
    SourceSHA256=(Get-FileHash -LiteralPath (Join-Path $diagnosticOutput 'NativeCrashProbe.compiled-source.cs') -Algorithm SHA256).Hash
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $diagnosticOutput 'launcher-build.json') -Encoding UTF8
Write-Output (Join-Path $diagnosticOutput 'NativeCrashProbe.exe')
$clrmdLibrary = Join-Path $diagnosticOutput 'clrmd\lib\net45\Microsoft.Diagnostics.Runtime.dll'
if (Test-Path -LiteralPath $clrmdLibrary) {
    Copy-Item -LiteralPath $clrmdLibrary -Destination $diagnosticOutput -Force
    & $compiler /nologo /langversion:5 /platform:x64 /target:exe /debug:pdbonly ('/reference:' + $clrmdLibrary) ('/out:' + (Join-Path $diagnosticOutput 'AnalyzeManagedDump.exe')) (Join-Path $PSScriptRoot 'AnalyzeManagedDump.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Offline managed dump analyzer compilation failed.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AnalyzeManagedDump.cs') -Destination (Join-Path $diagnosticOutput 'AnalyzeManagedDump.compiled-source.cs') -Force
    Write-Output (Join-Path $diagnosticOutput 'AnalyzeManagedDump.exe')
}
