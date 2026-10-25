param()
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runtimePath = Join-Path $projectPath '.runtime\native-diagnostics'
$packagePath = Join-Path $runtimePath 'microsoft.diagnostics.runtime.1.1.35504.nupkg'
$extractPath = Join-Path $runtimePath 'clrmd'
$packageUrl = 'https://api.nuget.org/v3-flatcontainer/microsoft.diagnostics.runtime/1.1.35504/microsoft.diagnostics.runtime.1.1.35504.nupkg'
$expectedHash = '5AE3A8079D5D9E6E49558064DFB00CBF07D89133E18F143AA2426C351BB3A16A'
New-Item -ItemType Directory -Path $runtimePath -Force | Out-Null
if (-not (Test-Path -LiteralPath $packagePath)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri $packageUrl -OutFile $packagePath
}
if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ne $expectedHash) { throw 'ClrMD package SHA256 mismatch; no assembly was extracted or executed.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not (Test-Path -LiteralPath $extractPath)) { [IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $extractPath) }
$library = Join-Path $extractPath 'lib\net45\Microsoft.Diagnostics.Runtime.dll'
if (-not (Test-Path -LiteralPath $library)) { throw 'ClrMD net45 library is missing.' }
[PSCustomObject]@{ Package='Microsoft.Diagnostics.Runtime'; Version='1.1.35504'; Source=$packageUrl; SHA256=$expectedHash; Author='Microsoft'; License=(Join-Path $extractPath 'License-Stable.rtf'); Library=$library } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runtimePath 'clrmd-dependency.json') -Encoding UTF8
Write-Output $library
