using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace CodexIsland
{
    internal static class AutoSceneContextReader
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
        internal static AutoSceneContext Read()
        {
            var result = new AutoSceneContext { LocalTime = DateTime.Now };
            IntPtr hwnd = GetForegroundWindow(); uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            using (var own = Process.GetCurrentProcess()) result.OwnProcess = pid == own.Id;
            if (pid != 0 && !result.OwnProcess)
            {
                try { using (var process = Process.GetProcessById((int)pid)) result.Executable = process.ProcessName; }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            try { if (hwnd != IntPtr.Zero) result.Monitor = Forms.Screen.FromHandle(hwnd).DeviceName; }
            catch (InvalidOperationException) { }
            try
            {
                var power = Forms.SystemInformation.PowerStatus.PowerLineStatus;
                result.OnAc = power == Forms.PowerLineStatus.Unknown ? (bool?)null : power == Forms.PowerLineStatus.Online;
            }
            catch (InvalidOperationException) { }
            return result;
        }
    }
}
