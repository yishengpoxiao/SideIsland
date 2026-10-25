param([ValidateRange(1,3)][int]$Count = 1, [switch]$VerifyReveal, [switch]$ViaShortcut)
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$portableExecutable = Join-Path $projectDirectory 'dist\CodexIsland.exe'
$installedExecutable = Join-Path $projectDirectory 'packaging\app\CodexIsland.exe'
$package = Get-AppxPackage -Name 'CodexIsland.Notifications' -ErrorAction Stop
if (-not $package) { throw 'The notification edition must be installed first.' }
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class IslandStartupProbe {
 public delegate bool Visitor(IntPtr window, IntPtr state);
 [DllImport("user32.dll")] static extern bool EnumWindows(Visitor visitor, IntPtr state);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
 [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
 [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window,int command);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder title, int size);
 [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
 public static bool VisibleAndResponding(uint process) {
  bool found=false;
  EnumWindows((window,state)=>{ uint owner; GetWindowThreadProcessId(window,out owner); if(owner!=process || !IsWindowVisible(window) || IsIconic(window)) return true;
   var title=new StringBuilder(160); GetWindowText(window,title,title.Capacity); if(!title.ToString().StartsWith("Codex Island") && !title.ToString().StartsWith("Side Island")) return true;
   IntPtr result; found=SendMessageTimeout(window,0,IntPtr.Zero,IntPtr.Zero,2,75,out result)!=IntPtr.Zero; return !found;
  },IntPtr.Zero); return found;
 }
 public static bool Minimize(uint process) {
  bool found=false;
  EnumWindows((window,state)=>{ uint owner; GetWindowThreadProcessId(window,out owner); if(owner!=process || !IsWindowVisible(window)) return true;
   var title=new StringBuilder(160); GetWindowText(window,title,title.Capacity); if(!title.ToString().StartsWith("Codex Island") && !title.ToString().StartsWith("Side Island")) return true;
   ShowWindow(window,6); found=IsIconic(window); return false;
  },IntPtr.Zero); return found;
 }
}
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IIslandStartupActivation {
 int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint pid);
}
[ComImport, Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c")]
public class IslandStartupActivationManager { }
public static class IslandStartupActivation {
 public static uint Start(string id) { uint pid; var manager=(IIslandStartupActivation)new IslandStartupActivationManager(); int result=manager.ActivateApplication(id,"",0,out pid); if(result<0) Marshal.ThrowExceptionForHR(result); return pid; }
}
'@
for ($iteration = 1; $iteration -le $Count; $iteration++) {
    $existing = @(Get-Process -Name CodexIsland -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $portableExecutable -or $_.Path -eq $installedExecutable })
    Start-Process -FilePath $portableExecutable -ArgumentList '--shutdown' -WindowStyle Hidden -Wait
    foreach ($process in $existing) { if (-not $process.WaitForExit(10000)) { throw 'App kept its window open; stopped to preserve unsaved work.' } }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    if ($ViaShortcut) {
        Start-Process -FilePath (Join-Path $projectDirectory '启动通知版.cmd') -WindowStyle Hidden
        do {
            $launched = Get-Process -Name CodexIsland -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExecutable } | Select-Object -First 1
            if (-not $launched) { Start-Sleep -Milliseconds 20 }
            if ($watch.ElapsedMilliseconds -ge 15000) { throw 'Shortcut did not start the installed app within 15 seconds.' }
        } while (-not $launched)
        $launchedId = $launched.Id
    }
    else { $launchedId = [IslandStartupActivation]::Start($package.PackageFamilyName + '!Island') }
    $activationMilliseconds = $watch.ElapsedMilliseconds
    while (-not [IslandStartupProbe]::VisibleAndResponding($launchedId)) {
        if ($watch.ElapsedMilliseconds -ge 15000) { throw 'No visible, responding island window within 15 seconds.' }
        Start-Sleep -Milliseconds 20
    }
    $visibleMilliseconds = $watch.ElapsedMilliseconds
    $revealMilliseconds = $null
    if ($VerifyReveal) {
        if (-not [IslandStartupProbe]::Minimize($launchedId)) { throw 'Could not minimize the test-owned island window.' }
        $watch.Restart()
        $secondary = Start-Process -FilePath $portableExecutable -WindowStyle Hidden -PassThru
        while (-not [IslandStartupProbe]::VisibleAndResponding($launchedId)) {
            if ($watch.ElapsedMilliseconds -ge 5000) { throw 'Second launch did not restore the minimized island within five seconds.' }
            Start-Sleep -Milliseconds 20
        }
        $revealMilliseconds = $watch.ElapsedMilliseconds
        if (-not $secondary.WaitForExit(5000) -or $secondary.ExitCode -ne 0) { throw 'Second launch did not hand off to the existing island.' }
    }
    [PSCustomObject]@{ Run=$iteration; ProcessId=$launchedId; ViaShortcut=$ViaShortcut.IsPresent; ActivationMilliseconds=$activationMilliseconds; VisibleResponsiveMilliseconds=$visibleMilliseconds; RestoreOnSecondLaunchMilliseconds=$revealMilliseconds }
}
