using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Forms = System.Windows.Forms;

namespace CodexIsland
{
    internal sealed class FullscreenSample
    {
        internal bool Visible, Minimized, Cloaked, OwnProcess, Desktop, Maximized, Captioned;
        internal Rect Window, Monitor;
        internal string Device, ProcessName;
    }
    internal static class FullscreenPolicy
    {
        internal static string ModeFor(FullscreenSample sample, DesktopExperiencePreferences preferences)
        {
            if (preferences == null) return "off";
            string name = sample == null ? "" : FullscreenApplicationRule.NormalizeProcess(sample.ProcessName);
            if (name.Length > 0 && preferences.FullscreenRules != null)
                foreach (var rule in preferences.FullscreenRules)
                    if (rule != null && String.Equals(FullscreenApplicationRule.NormalizeProcess(rule.ProcessName), name, StringComparison.OrdinalIgnoreCase))
                        return rule.Mode == "hide" || rule.Mode == "slim" || rule.Mode == "off" ? rule.Mode : preferences.FullscreenMode;
            return preferences.FullscreenMode;
        }
        internal static bool IsFullscreen(FullscreenSample sample, string islandDevice)
        {
            if (sample == null || !sample.Visible || sample.Minimized || sample.Cloaked || sample.OwnProcess || sample.Desktop || (sample.Maximized && sample.Captioned) || !String.Equals(sample.Device, islandDevice, StringComparison.OrdinalIgnoreCase)) return false;
            Rect window = sample.Window, monitor = sample.Monitor;
            return monitor.Width > 0 && monitor.Height > 0 && window.Width > 0 && window.Height > 0 && window.Left <= monitor.Left + 2 && window.Top <= monitor.Top + 2 && window.Right >= monitor.Right - 2 && window.Bottom >= monitor.Bottom - 2;
        }
    }
    internal static class DesktopExperienceNative
    {
        private static readonly uint OwnProcessId = (uint)Process.GetCurrentProcess().Id;
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(IntPtr handle);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int length);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] private static extern int GetWindowLong32(IntPtr handle, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] private static extern int SetWindowLong32(IntPtr handle, int index, int value);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLong64(IntPtr handle, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] private static extern IntPtr SetWindowLong64(IntPtr handle, int index, IntPtr value);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        internal static FullscreenSample ReadForeground()
        {
            try
            {
                IntPtr handle = GetForegroundWindow(); if (handle == IntPtr.Zero) return null;
                NativeRect rectangle; if (!GetWindowRect(handle, out rectangle)) return null;
                uint process; GetWindowThreadProcessId(handle, out process); var name = new StringBuilder(128); GetClassName(handle, name, name.Capacity);
                int cloaked = 0; try { DwmGetWindowAttribute(handle, 14, out cloaked, 4); } catch { }
                var screen = Forms.Screen.FromHandle(handle); var bounds = screen.Bounds; string className = name.ToString();
                return new FullscreenSample { Visible = IsWindowVisible(handle), Minimized = IsIconic(handle), Maximized = IsZoomed(handle), Captioned = (GetWindowLong32(handle, -16) & 0x00C00000) != 0, Cloaked = cloaked != 0, OwnProcess = process == OwnProcessId,
                    Desktop = className == "Progman" || className == "WorkerW" || className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd" || className == "LockScreenInputOcclusionFrame",
                    Window = new Rect(rectangle.Left, rectangle.Top, Math.Max(0, rectangle.Right - rectangle.Left), Math.Max(0, rectangle.Bottom - rectangle.Top)), Monitor = new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height), Device = screen.DeviceName, ProcessName = ReadProcessName(process) };
            }
            catch { return null; }
        }
        private static string ReadProcessName(uint processId)
        {
            IntPtr process = IntPtr.Zero;
            try
            {
                process = OpenProcess(0x1000, false, processId); if (process == IntPtr.Zero) return "";
                var path = new StringBuilder(32768); uint length = (uint)path.Capacity;
                return QueryFullProcessImageName(process, 0, path, ref length) ? FullscreenApplicationRule.NormalizeProcess(Path.GetFileName(path.ToString())) : "";
            }
            catch { return ""; }
            finally { if (process != IntPtr.Zero) CloseHandle(process); }
        }
        internal static long GetExtendedStyle(IntPtr handle) { return IntPtr.Size == 8 ? GetWindowLong64(handle, -20).ToInt64() : GetWindowLong32(handle, -20); }
        internal static void SetExtendedStyle(IntPtr handle, long style) { if (IntPtr.Size == 8) SetWindowLong64(handle, -20, new IntPtr(style)); else SetWindowLong32(handle, -20, (int)style); }
    }
}
