param([string]$AppDirectory, [string]$OutputDirectory = (Join-Path $PSScriptRoot 'app'))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$versionPath = Join-Path $projectRoot 'VERSION'
$version = [IO.File]::ReadAllText($versionPath).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain a three-part release version.' }
$identityVersion = $version + '.0'
if (-not $AppDirectory) {
    if (Test-Path -LiteralPath (Join-Path $projectRoot 'CodexIsland.exe')) { $AppDirectory = $projectRoot }
    else { $AppDirectory = Join-Path $projectRoot 'dist' }
}
$AppDirectory = [IO.Path]::GetFullPath($AppDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$executable = Join-Path $AppDirectory 'CodexIsland.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw ('Build CodexIsland.exe first: ' + $executable) }
$actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion
if ($actualVersion -ne $identityVersion) { throw ('The source executable version is ' + $actualVersion + '; VERSION requires ' + $identityVersion + '. Rebuild before notification staging.') }
if ([String]::Equals($AppDirectory.TrimEnd('\'), $OutputDirectory.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Package staging must use a separate directory from the portable executable.' }
$existingManifest = Join-Path $OutputDirectory 'AppxManifest.xml'
if (Test-Path -LiteralPath $existingManifest) {
    [xml]$existing = Get-Content -LiteralPath $existingManifest -Raw
    $existingNamespaces = New-Object Xml.XmlNamespaceManager($existing.NameTable)
    $existingNamespaces.AddNamespace('p', $existing.DocumentElement.NamespaceURI)
    $existingIdentity = $existing.SelectSingleNode('/p:Package/p:Identity', $existingNamespaces)
    if ($null -eq $existingIdentity) { throw 'Existing staging manifest has no identity. Choose a separate empty OutputDirectory.' }
    if ([version]$existingIdentity.Version -gt [version]$identityVersion) { throw 'The existing staging directory contains a newer development identity. Export settings, exit the old app and explicitly unregister it before choosing a separate clean staging directory. No files were changed.' }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Copy-Item -LiteralPath $executable -Destination (Join-Path $OutputDirectory 'CodexIsland.exe') -Force
Copy-Item -LiteralPath (Join-Path $AppDirectory 'CodexIsland.exe.config') -Destination (Join-Path $OutputDirectory 'CodexIsland.exe.config') -Force
$notice = Join-Path $AppDirectory 'THIRD-PARTY-NOTICES.txt'
if (-not (Test-Path -LiteralPath $notice)) { $notice = Join-Path (Split-Path -Parent $PSScriptRoot) 'THIRD-PARTY-NOTICES.txt' }
Copy-Item -LiteralPath $notice -Destination (Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.txt') -Force
[xml]$stagedAppxManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
$manifestNamespaces = New-Object Xml.XmlNamespaceManager($stagedAppxManifest.NameTable)
$manifestNamespaces.AddNamespace('p', $stagedAppxManifest.DocumentElement.NamespaceURI)
$stagedAppxIdentity = $stagedAppxManifest.SelectSingleNode('/p:Package/p:Identity', $manifestNamespaces)
if ($null -eq $stagedAppxIdentity -or $stagedAppxIdentity.Name -ne 'CodexIsland.Notifications') { throw 'Notification manifest identity is missing or unexpected.' }
$stagedAppxIdentity.SetAttribute('Version', $identityVersion)
$stagedAppxManifest.Save((Join-Path $OutputDirectory 'AppxManifest.xml'))
Copy-Item -LiteralPath $versionPath -Destination (Join-Path $OutputDirectory 'VERSION') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NotificationAssets') -Destination $OutputDirectory -Recurse -Force
Write-Output $OutputDirectory
