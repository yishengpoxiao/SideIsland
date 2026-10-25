using System;
using System.Runtime.InteropServices;

namespace CodexIsland
{
    public class SystemStats
    {
        public double? CpuPercent;
        public double? MemoryPercent;
        public double? BatteryPercent;
        public bool? OnAc;
    }

    public static class SystemStatsReader
    {
        private static readonly object CpuLock = new object();
        private static bool hasPreviousCpu;
        private static ulong previousIdle, previousKernel, previousUser;

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint Low;
            public uint High;
            public ulong Ticks { get { return ((ulong)High << 32) | Low; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerStatus
        {
            public byte AcLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out PowerStatus power);

        public static SystemStats Read()
        {
            SystemStats result = new SystemStats();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return result;
            result.CpuPercent = ReadCpu();
            MemoryStatus memory = new MemoryStatus();
            memory.Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            if (GlobalMemoryStatusEx(ref memory))
                result.MemoryPercent = Math.Max(0.0, Math.Min(100.0, memory.MemoryLoad));
            PowerStatus power;
            if (GetSystemPowerStatus(out power))
            {
                if ((power.BatteryFlag & 128) == 0 && power.BatteryLifePercent <= 100)
                    result.BatteryPercent = power.BatteryLifePercent;
                if (power.AcLineStatus == 0 || power.AcLineStatus == 1)
                    result.OnAc = power.AcLineStatus == 1;
            }
            return result;
        }

        private static double? ReadCpu()
        {
            lock (CpuLock)
            {
                FileTime idle, kernel, user;
                if (!GetSystemTimes(out idle, out kernel, out user))
                {
                    hasPreviousCpu = false;
                    return null;
                }
                ulong idleNow = idle.Ticks, kernelNow = kernel.Ticks, userNow = user.Ticks;
                double? percent = null;
                if (hasPreviousCpu && idleNow >= previousIdle && kernelNow >= previousKernel && userNow >= previousUser)
                {
                    double total = (double)(kernelNow - previousKernel) + (double)(userNow - previousUser);
                    if (total > 0)
                        percent = Math.Max(0.0, Math.Min(100.0, (total - (idleNow - previousIdle)) * 100.0 / total));
                }
                previousIdle = idleNow;
                previousKernel = kernelNow;
                previousUser = userNow;
                hasPreviousCpu = true;
                return percent;
            }
        }
    }
}
