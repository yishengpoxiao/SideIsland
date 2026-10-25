[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$CheckOnly,
    [string]$AppDirectory,
    [string]$SignedPackagePath,
    [switch]$Unregister
)
$ErrorActionPreference = 'Stop'
$packageName = 'CodexIsland.Notifications'
$packagingRoot = Join-Path $PSScriptRoot 'packaging'
$releaseVersion = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'VERSION')).Trim()
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain a three-part release version.' }
$targetVersion = [version]($releaseVersion + '.0')
if (-not $AppDirectory) {
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'CodexIsland.exe')) { $AppDirectory = $PSScriptRoot }
    else { $AppDirectory = Join-Path $PSScriptRoot 'dist' }
}
$AppDirectory = [IO.Path]::GetFullPath($AppDirectory)
$executable = Join-Path $AppDirectory 'CodexIsland.exe'
$osBuild = [Environment]::OSVersion.Version.Build
$devKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
$policyKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Appx'
$developerMode = (Get-ItemProperty -LiteralPath $devKey -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense -eq 1
$developerPolicy = (Get-ItemProperty -LiteralPath $policyKey -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
if ($null -ne $developerPolicy) { $developerMode = $developerPolicy -eq 1 }
$appxCommand = Get-Command Add-AppxPackage -ErrorAction SilentlyContinue
$registered = @()
$registrationStatus = 'not registered'
$registrationError = $null
try {
    $registered = @(Get-AppxPackage -Name $packageName -ErrorAction Stop)
    if ($registered.Count) { $registrationStatus = ($registered | ForEach-Object PackageFullName) -join ', ' }
}
catch {
    $registrationError = $_.Exception.Message
    $registrationStatus = 'cannot read in this process: ' + $registrationError
}
$newerRegistrations = @($registered | Where-Object { [version]$_.Version -gt $targetVersion })
if ($CheckOnly) {
    [PSCustomObject]@{
        WindowsBuild = $osBuild
        SupportedWindows = $osBuild -ge 19041
        AppDirectory = $AppDirectory
        ExecutableExists = Test-Path -LiteralPath $executable
        DeveloperModeEnabled = $developerMode
        PackageRegistration = $registrationStatus
        TargetIdentityVersion = $targetVersion.ToString()
        HigherDevelopmentVersionInstalled = $newerRegistrations.Count -gt 0
        CanRegisterLooseManifest = ($osBuild -ge 19041 -and $developerMode -and $null -ne $appxCommand -and -not $registrationError -and $newerRegistrations.Count -eq 0)
        SignedPackageAlternative = 'Use -SignedPackagePath with a signed MSIX already trusted by this Windows account.'
    }
    return
}
if ($Unregister) {
    if ($registrationError) { throw ('Cannot read package registration. Run this script as your normal Windows account outside restricted processes. ' + $registrationError) }
    foreach ($package in $registered) {
        if ($PSCmdlet.ShouldProcess($package.PackageFullName, 'Remove the Codex Island notification identity for this user')) {
            Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
        }
    }
    if ($registered.Count) { Write-Output 'Notification identity removed for this user. Restart Codex Island to apply.' }
    else { Write-Output 'No Codex Island notification identity is registered for this user.' }
    return
}
if ($registrationError) { throw 'Cannot verify the existing notification registration. Run this script as your normal Windows account in Windows PowerShell 5.1 before installing; no registered app or data was changed.' }
if ($newerRegistrations.Count -gt 0) {
    throw ('A higher development notification identity is already registered (for example 1.13.0.0), while this release targets ' + $targetVersion + '. Windows will not accept this as a normal downgrade. First export your settings, exit the old app from its tray menu, then explicitly run .\install-notifications.ps1 -Unregister from the old installation (or use Windows Settings to remove that development identity). After confirming the old registration is removed, run this installer again. This script has not removed any app, data, permissions or Windows settings.')
}
if ($osBuild -lt 19041) { throw 'Notification identity requires Windows 10 version 2004 (build 19041) or newer.' }
if (-not $appxCommand) { throw 'Add-AppxPackage is unavailable. Run this script in Windows PowerShell 5.1.' }
if (-not (Test-Path -LiteralPath $executable)) { throw ('CodexIsland.exe was not found in ' + $AppDirectory + '. Build the app first, or pass -AppDirectory.') }
$executableVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion
if ($executableVersion -ne $targetVersion.ToString()) { throw ('The executable version ' + $executableVersion + ' does not match VERSION (' + $targetVersion + '). Rebuild or extract the complete matching release before installing.') }
if (-not $SignedPackagePath -and -not $developerMode) {
    throw 'Windows requires a trusted signed identity package, or Developer Mode for loose-file development registration. Developer Mode is not enabled. No Windows setting or certificate store was changed. Provide -SignedPackagePath, or enable Developer Mode yourself in Settings > System > Advanced > For developers, then run this script again. Developer Mode is intended for development; use a signed package for distribution.'
}
if ($SignedPackagePath) {
    $SignedPackagePath = (Resolve-Path -LiteralPath $SignedPackagePath).Path
    if ([IO.Path]::GetExtension($SignedPackagePath) -notin @('.msix', '.appx')) { throw 'SignedPackagePath must identify an .msix or .appx file.' }
}
$stagingDirectory = Join-Path $packagingRoot 'app'
$stagedExecutable = Join-Path $stagingDirectory 'CodexIsland.exe'
$running = @(Get-Process -Name CodexIsland -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable -or $_.Path -eq $stagedExecutable })
if ($running.Count) { throw 'Close Codex Island using its Exit menu before registering notification access; then run this script again.' }
if (-not $PSCmdlet.ShouldProcess($AppDirectory, 'Register the optional Codex Island notification identity for this user')) { return }
if ($SignedPackagePath) {
    Add-AppxPackage -Path $SignedPackagePath -ErrorAction Stop
}
else {
    $packageDirectory = & (Join-Path $packagingRoot 'stage-package.ps1') -AppDirectory $AppDirectory -OutputDirectory $stagingDirectory
    $identityManifest = Join-Path $packageDirectory 'AppxManifest.xml'
    Add-AppxPackage -Path $identityManifest -Register -ErrorAction Stop
}
$installed = Get-AppxPackage -Name $packageName -ErrorAction Stop
if (-not $installed) { throw 'Windows did not return the registered identity. Check AppxDeployment-Server in Event Viewer.' }
Write-Output ('Registered notification identity: ' + $installed.PackageFamilyName)
Write-Output 'Launching the registered app. Select Enable notifications and grant access in the Windows consent dialog yourself.'
Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList ('shell:AppsFolder\' + $installed.PackageFamilyName + '!Island') -WindowStyle Hidden
