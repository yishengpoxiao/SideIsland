param([string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) '.runtime\notification-permission.txt'), [switch]$ReadCheck)
$ErrorActionPreference = 'Stop'
$package = Get-AppxPackage -Name 'CodexIsland.Notifications' -ErrorAction Stop
if (-not $package) { throw 'Notification edition is not installed for this Windows user.' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
if (Test-Path -LiteralPath $OutputPath) { Remove-Item -LiteralPath $OutputPath }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IIslandActivationManager {
    int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint pid);
}
[ComImport, Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c")]
public class IslandActivationManager { }
public static class IslandActivation {
    public static uint Start(string id, string arguments) {
        var manager = (IIslandActivationManager)new IslandActivationManager();
        uint pid;
        manager.ActivateApplication(id, arguments, 0, out pid);
        return pid;
    }
}
'@
$probeFlag = if ($ReadCheck) { '--notification-read-check' } else { '--notification-status' }
$arguments = $probeFlag + ' "' + $OutputPath + '"'
[IslandActivation]::Start(($package.PackageFamilyName + '!Island'), $arguments) | Out-Null
$deadline = [DateTime]::UtcNow.AddSeconds(15)
while (-not (Test-Path -LiteralPath $OutputPath) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
if (-not (Test-Path -LiteralPath $OutputPath)) { throw 'Permission check did not return a result.' }
Get-Content -LiteralPath $OutputPath
