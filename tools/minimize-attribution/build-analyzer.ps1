param([string]$OutputDirectory = '.runtime\minimize-attribution-analysis')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$taskOutput = [IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory))
if (-not $taskOutput.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be within project.' }
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskSource = Join-Path $PSScriptRoot 'AnalyzeAttributionCalibration.cs'
$taskExe = Join-Path $taskOutput 'AnalyzeAttributionCalibration.exe'
New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
if (Test-Path -LiteralPath $taskExe) { throw 'Executable exists; choose a fresh output directory to preserve diagnostics.' }
$taskCompile = & $taskCompiler /nologo /target:exe /platform:x64 /optimize+ /r:System.Core.dll /r:System.Web.Extensions.dll ('/out:' + $taskExe) $taskSource 2>&1
$taskCompileExit = $LASTEXITCODE
Set-Content -LiteralPath (Join-Path $taskOutput 'compile.log') -Value (($taskCompile | Out-String) + 'CompilerExitCode=' + $taskCompileExit) -Encoding UTF8
if ($taskCompileExit -ne 0) { throw "Compiler exit $taskCompileExit" }
$taskSelfCheck = & $taskExe --self-check 2>&1
$taskCheckExit = $LASTEXITCODE
Set-Content -LiteralPath (Join-Path $taskOutput 'self-check.log') -Value (($taskSelfCheck | Out-String) + 'SelfCheckExitCode=' + $taskCheckExit) -Encoding UTF8
$taskManifest = [ordered]@{
    GeneratedUtc = [DateTime]::UtcNow.ToString('o')
    NoUiOrSessionExecuted = $true
    Source = $taskSource
    SourceSha256 = (Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash
    Compiler = $taskCompiler
    CompilerVersion = (Get-Item -LiteralPath $taskCompiler).VersionInfo.FileVersion
    CompilerSha256 = (Get-FileHash -LiteralPath $taskCompiler -Algorithm SHA256).Hash
    BuildScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    Executable = $taskExe
    ExecutableSha256 = (Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash
    CompilerExitCode = $taskCompileExit
    SelfCheckExitCode = $taskCheckExit
    References = @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll')
}
$taskManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskOutput 'build-manifest.json') -Encoding UTF8
Copy-Item -LiteralPath $taskSource -Destination (Join-Path $taskOutput 'AnalyzeAttributionCalibration.compiled-source.cs')
if ($taskCheckExit -ne 0) { throw "Self-check exit $taskCheckExit" }
$taskSelfCheck
