param(
    [string]$MakeAppxPath,
    [string]$SignToolPath,
    [string]$SigningCertificateThumbprint,
    [string]$AppDirectory,
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\release\CodexIsland.Notifications.msix')
)
$ErrorActionPreference = 'Stop'
function Find-SdkTool([string]$toolName, [string]$explicitPath) {
    if ($explicitPath) { return (Resolve-Path -LiteralPath $explicitPath).Path }
    $command = Get-Command $toolName -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kitRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitRoot) {
        $match = Get-ChildItem -LiteralPath $kitRoot -Filter $toolName -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($match) { return $match.FullName }
    }
    throw ($toolName + ' was not found. Install the Windows SDK packaging tools, or provide its explicit path. This script does not install an SDK or change trust settings.')
}
$MakeAppxPath = Find-SdkTool 'MakeAppx.exe' $MakeAppxPath
if ($SigningCertificateThumbprint) {
    $SignToolPath = Find-SdkTool 'signtool.exe' $SignToolPath
    $certificatePath = 'Cert:\CurrentUser\My\' + ($SigningCertificateThumbprint -replace '\s', '')
    $certificate = Get-Item -LiteralPath $certificatePath
    if ($certificate.Subject -ne 'CN=CodexIsland' -or -not $certificate.HasPrivateKey) {
        throw 'The signing certificate must have subject CN=CodexIsland and an accessible private key. Keep the publisher in the package and executable manifests identical.'
    }
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($OutputPath)) -Force | Out-Null
$staging = & (Join-Path $PSScriptRoot 'stage-package.ps1') -AppDirectory $AppDirectory
& $MakeAppxPath pack /o /d $staging /p $OutputPath
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed.' }
if ($SigningCertificateThumbprint) {
    & $SignToolPath sign /fd SHA256 /sha1 $SigningCertificateThumbprint $OutputPath
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
    Write-Output ('Signed identity package: ' + $OutputPath)
    Write-Output 'Install only on accounts that already trust this certificate. This script does not add certificates to any trust store.'
}
else {
    Write-Output ('Unsigned build artifact: ' + $OutputPath)
    Write-Output 'This artifact must be signed with a trusted certificate before normal installation. It is not an installable release yet.'
}
