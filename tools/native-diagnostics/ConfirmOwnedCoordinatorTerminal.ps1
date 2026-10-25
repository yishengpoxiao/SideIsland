param([Parameter(Mandatory=$true)][string]$RunDirectory,[int]$TimeoutSeconds=1800)
$ErrorActionPreference='Stop'
$projectPath=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runPath=[IO.Path]::GetFullPath($RunDirectory)
if(-not $runPath.StartsWith($projectPath.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or $TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 1800){throw 'Evidence/timeout outside authorized project scope.'}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class OwnedCoordinatorTerminalNative {
 [StructLayout(LayoutKind.Sequential)] public struct FileTime {public uint Low,High;public ulong Value{get{return ((ulong)High<<32)|Low;}}}
 [DllImport("kernel32.dll",SetLastError=true)] public static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder path,ref uint size);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool GetProcessTimes(IntPtr process,out FileTime created,out FileTime ended,out FileTime kernel,out FileTime user);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool GetExitCodeProcess(IntPtr process,out uint code);
 [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
}
'@
$ownedPath=Join-Path $runPath 'coordinator/owned-processes.json'
$ownership=Get-Content -LiteralPath $ownedPath -Raw|ConvertFrom-Json
$coordinators=@($ownership.Processes|Where-Object{$_.Role -eq 'coordinator'})
if($coordinators.Count -ne 1){throw 'Need exactly one previously verified coordinator identity.'}
$owner=$coordinators[0]
$coordinatorPid=[uint32]$owner.ProcessId
$expectedImage=[IO.Path]::GetFullPath($owner.Image)
if(-not $expectedImage.StartsWith((Join-Path $projectPath '.runtime')+'\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($expectedImage) -ne 'NativeTests.exe'){throw 'Owned identity image is outside exact project test scope.'}
$expectedHash=([IO.File]::ReadAllText((Join-Path $runPath 'coordinator/target-sha256.txt'))).Trim()
$observedHash=(Get-FileHash -LiteralPath $expectedImage -Algorithm SHA256).Hash
if($observedHash -ne $expectedHash){throw 'Test image hash differs from coordinator launch record.'}
$expectedCreation=[UInt64]::Parse($owner.CreationFileTime.Substring(2),[Globalization.NumberStyles]::HexNumber)
$handle=[OwnedCoordinatorTerminalNative]::OpenProcess(0x101000,$false,$coordinatorPid)
if($handle -eq [IntPtr]::Zero){throw ('Cannot retain the one authorized coordinator handle: '+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
$record=[ordered]@{Kind='independent-retained-owned-coordinator-terminal-handle';ProcessId=$coordinatorPid;ExpectedCreationFileTime=$owner.CreationFileTime;ExpectedImage=$expectedImage;ExpectedImageSHA256=$expectedHash;OpenedUtc=[DateTime]::UtcNow.ToString('o');IdentityVerified=$false;DebuggerAttached=$false;UIAccessed=$false;EnumeratedOtherProcesses=$false;SourceSHA256=(Get-FileHash -LiteralPath $MyInvocation.MyCommand.Path -Algorithm SHA256).Hash;WaitSignaled=$false;ProbeSucceeded=$false}
try {
    $created=New-Object OwnedCoordinatorTerminalNative+FileTime;$ended=New-Object OwnedCoordinatorTerminalNative+FileTime;$kernel=New-Object OwnedCoordinatorTerminalNative+FileTime;$user=New-Object OwnedCoordinatorTerminalNative+FileTime
    if(-not [OwnedCoordinatorTerminalNative]::GetProcessTimes($handle,[ref]$created,[ref]$ended,[ref]$kernel,[ref]$user)){throw ('Initial creation-time query failed: '+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
    $path=New-Object Text.StringBuilder 32768;$pathLength=[uint32]$path.Capacity
    if(-not [OwnedCoordinatorTerminalNative]::QueryFullProcessImageName($handle,0,$path,[ref]$pathLength)){throw ('Exact image query failed: '+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
    if($created.Value -ne $expectedCreation -or -not [String]::Equals([IO.Path]::GetFullPath($path.ToString()),$expectedImage,[StringComparison]::OrdinalIgnoreCase)){throw 'PID creation/image identity mismatch; no terminal observation accepted.'}
    $record.IdentityVerified=$true;$record.ObservedCreationFileTime='0x'+$created.Value.ToString('X');$record.ObservedImage=$path.ToString()
    $record|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $runPath 'coordinator-terminal-handle-start.json') -Encoding UTF8
    Write-Output ('Retained verified coordinator PID='+$coordinatorPid+' creation='+$owner.CreationFileTime+'; waiting for actual process-object signal.')
    $clock=[Diagnostics.Stopwatch]::StartNew();$waitStatus=[uint32]258
    while($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds){
        $remaining=[Math]::Max(1,[int](($TimeoutSeconds-$clock.Elapsed.TotalSeconds)*1000));$waitStatus=[OwnedCoordinatorTerminalNative]::WaitForSingleObject($handle,[uint32][Math]::Min(5000,$remaining))
        if($waitStatus -eq 0){$record.WaitSignaled=$true;break}
        if($waitStatus -ne 258){throw ('WaitForSingleObject failed/status='+$waitStatus+' error='+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
    }
    $record.WaitStatus=$waitStatus;$record.WaitElapsedMilliseconds=$clock.ElapsedMilliseconds
    if($record.WaitSignaled){
        $actualExit=[uint32]0;$exitSuccess=[OwnedCoordinatorTerminalNative]::GetExitCodeProcess($handle,[ref]$actualExit);$exitError=if($exitSuccess){0}else{[Runtime.InteropServices.Marshal]::GetLastWin32Error()}
        $timesSuccess=[OwnedCoordinatorTerminalNative]::GetProcessTimes($handle,[ref]$created,[ref]$ended,[ref]$kernel,[ref]$user);$timesError=if($timesSuccess){0}else{[Runtime.InteropServices.Marshal]::GetLastWin32Error()}
        $record.GetExitCodeProcessSucceeded=$exitSuccess;$record.GetExitCodeProcessError=$exitError;$record.ActualExitCode=if($exitSuccess){$actualExit}else{$null};$record.ActualExitHex=if($exitSuccess){'0x'+$actualExit.ToString('X8')}else{$null}
        $record.GetProcessTimesSucceeded=$timesSuccess;$record.GetProcessTimesError=$timesError;$record.TerminalCreationFileTime='0x'+$created.Value.ToString('X');$record.TerminalExitFileTime='0x'+$ended.Value.ToString('X')
        $record.TerminalExitUtc=if($ended.Value -ne 0){[DateTime]::FromFileTimeUtc([int64]$ended.Value).ToString('o')}else{$null}
        $record.ProbeSucceeded=$exitSuccess -and $timesSuccess -and ($created.Value -eq $expectedCreation) -and ($ended.Value -ne 0)
    }
    $record.Meaning='Read-only single-PID evidence after retained process handle was signaled; actual coordinator result remains separate from probe success. No process was terminated or retried.'
} catch {$record.Error=$_.Exception.ToString();throw}
finally {
    $record.EndedUtc=[DateTime]::UtcNow.ToString('o');$record.TestImageSHA256After=(Get-FileHash -LiteralPath $expectedImage -Algorithm SHA256).Hash
    $record|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $runPath 'coordinator-terminal-handle.json') -Encoding UTF8
    [OwnedCoordinatorTerminalNative]::CloseHandle($handle)|Out-Null
}
$record|ConvertTo-Json -Depth 5
if(-not $record.ProbeSucceeded){exit 2}
