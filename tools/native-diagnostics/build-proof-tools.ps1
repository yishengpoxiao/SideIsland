param()
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$diagnosticOutput = Join-Path $projectPath '.runtime/native-diagnostics'
[IO.Directory]::CreateDirectory($diagnosticOutput) | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$clrmd = Join-Path $diagnosticOutput 'Microsoft.Diagnostics.Runtime.dll'
$icedRoot = Join-Path $diagnosticOutput 'iced'
$icedPackage = Join-Path $icedRoot 'iced.1.21.0.nupkg'
$icedHash = 'D31613617E3DDF91239BBC94A8C587849B4236E8F8A2A2BA59EA2397A1487C7A'
if (-not (Test-Path -LiteralPath $clrmd)) { throw 'Run get-clrmd.ps1 and build.ps1 first for the offline dump reader dependency.' }
if (-not (Test-Path -LiteralPath $icedPackage) -or (Get-FileHash -LiteralPath $icedPackage -Algorithm SHA256).Hash -ne $icedHash) {
    throw 'Missing or mismatched Iced package: official NuGet Iced 1.21.0; SHA256 must match D31613617E3DDF91239BBC94A8C587849B4236E8F8A2A2BA59EA2397A1487C7A. No network request was made.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($icedPackage)
try {
    $entry=$zip.GetEntry('lib/net45/Iced.dll')
    if ($null -eq $entry) { throw 'Pinned package has no net45 Iced.dll.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path $diagnosticOutput 'Iced.dll'),$true)
} finally { $zip.Dispose() }
$specifications=@(
    @{Name='ReadDumpMemory';Reference=$clrmd},
    @{Name='DisassemblePe';Reference=(Join-Path $diagnosticOutput 'Iced.dll')},
    @{Name='NativePtrMultisetProof';Reference='System.Web.Extensions.dll'}
)
$builds=foreach ($specification in $specifications) {
    $source=Join-Path $PSScriptRoot ($specification.Name+'.cs')
    $exe=Join-Path $diagnosticOutput ($specification.Name+'.exe')
    & $compiler /nologo /langversion:5 /platform:x64 /target:exe /optimize+ ('/reference:'+$specification.Reference) ('/out:'+$exe) $source
    if ($LASTEXITCODE -ne 0) { throw ($specification.Name+' compilation failed.') }
    Copy-Item -LiteralPath $source -Destination (Join-Path $diagnosticOutput ($specification.Name+'.compiled-source.cs')) -Force
    [ordered]@{Tool=$specification.Name;SourceSHA256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash;ExecutableSHA256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash}
}
[ordered]@{CompiledUtc=[DateTime]::UtcNow.ToString('o');Compiler=$compiler;IcedPackageSHA256=$icedHash;Builds=$builds} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $diagnosticOutput 'proof-tools-build.json') -Encoding UTF8
$builds | ForEach-Object { Write-Output (Join-Path $diagnosticOutput ($_.Tool+'.exe')) }
