using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexIsland
{
    internal sealed class PortOwner
    {
        public string Protocol, Address, State, Program, Path;
        public int Port, Pid;
    }

    internal sealed class PortMonitorResult
    {
        public readonly List<PortOwner> Owners = new List<PortOwner>();
        public readonly List<string> Errors = new List<string>();
    }

    internal static class PortMonitor
    {
        private const int BufferTooSmall = 122;
        public static int[] ParsePorts(string text)
        {
            var parts = (text ?? "").Split(new[] { ',', '，', ';', '；', ' ', '\t', '\r', '\n', '、' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) throw new ArgumentException("请输入端口，例如 3000、8080。");
            if (parts.Length > 64) throw new ArgumentException("一次最多监测 64 个端口。");
            var ports = new SortedSet<int>();
            foreach (string part in parts)
            {
                int port;
                if (!Int32.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                    throw new ArgumentException("端口必须是 1—65535 的整数：" + part);
                ports.Add(port);
            }
            return ports.ToArray();
        }

        public static PortMonitorResult Read(IEnumerable<int> requested)
        {
            var wanted = new HashSet<int>(requested);
            var result = new PortMonitorResult();
            foreach (bool tcp in new[] { true, false })
                foreach (bool ipv6 in new[] { false, true }) ReadTable(result, wanted, tcp, ipv6);
            var identities = new Dictionary<int, Tuple<string, string>>();
            foreach (var owner in result.Owners)
            {
                Tuple<string, string> identity;
                if (!identities.TryGetValue(owner.Pid, out identity)) { identity = ResolveProcess(owner.Pid); identities[owner.Pid] = identity; }
                owner.Program = identity.Item1; owner.Path = identity.Item2;
            }
            result.Owners.Sort((a, b) => { int port = a.Port.CompareTo(b.Port); return port != 0 ? port : String.CompareOrdinal(a.Protocol + a.Address, b.Protocol + b.Address); });
            return result;
        }

        private static void ReadTable(PortMonitorResult result, HashSet<int> ports, bool tcp, bool ipv6)
        {
            string label = (tcp ? "TCP" : "UDP") + (ipv6 ? " IPv6" : " IPv4");
            int size = 0, family = ipv6 ? 23 : 2;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int code = tcp ? GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0) : GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, 1, 0);
                if (code != 0 && code != BufferTooSmall) throw new Win32Exception(code);
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (size < 4 || size > 16 * 1024 * 1024) throw new InvalidOperationException("端口表大小异常");
                    int allocated = size; buffer = Marshal.AllocHGlobal(allocated);
                    code = tcp ? GetExtendedTcpTable(buffer, ref size, false, family, 5, 0) : GetExtendedUdpTable(buffer, ref size, false, family, 1, 0);
                    if (code == BufferTooSmall) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; continue; }
                    if (code != 0) throw new Win32Exception(code);
                    int count = Marshal.ReadInt32(buffer), rowSize = tcp ? (ipv6 ? 56 : 24) : (ipv6 ? 28 : 12);
                    if (count < 0 || count > (allocated - 4) / rowSize) throw new InvalidOperationException("端口表结构异常");
                    for (int index = 0; index < count; index++)
                    {
                        IntPtr row = IntPtr.Add(buffer, 4 + index * rowSize);
                        int portOffset = ipv6 ? 20 : tcp ? 8 : 4;
                        int port = (Marshal.ReadByte(row, portOffset) << 8) | Marshal.ReadByte(row, portOffset + 1);
                        if (!ports.Contains(port)) continue;
                        string endpoint = ReadLocalAddress(row, tcp, ipv6).ToString();
                        int pidOffset = tcp ? (ipv6 ? 52 : 20) : (ipv6 ? 24 : 8);
                        int stateOffset = ipv6 ? 48 : 0;
                        result.Owners.Add(new PortOwner { Protocol = label, Address = endpoint, Port = port, Pid = Marshal.ReadInt32(row, pidOffset), State = tcp ? TcpStateName(Marshal.ReadInt32(row, stateOffset)) : "已绑定" });
                    }
                    return;
                }
                throw new InvalidOperationException("端口表正在变化，请重试");
            }
            catch (Exception error)
            {
                if (!(error is Win32Exception) && !(error is InvalidOperationException)) throw;
                result.Errors.Add(label + "：" + error.Message);
            }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
        }

        internal static string TcpStateName(int value)
        {
            string[] names = { "未知", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED", "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK", "TIME_WAIT", "DELETE_TCB" };
            return value >= 0 && value < names.Length ? names[value] : "状态 " + value;
        }

        internal static IPAddress ReadLocalAddress(IntPtr row, bool tcp, bool ipv6)
        {
            byte[] address = new byte[ipv6 ? 16 : 4];
            Marshal.Copy(IntPtr.Add(row, ipv6 ? 0 : tcp ? 4 : 0), address, 0, address.Length);
            // Scope is a native DWORD, unlike the network-order port bytes.
            // Match .NET Framework SystemTcpConnectionInformation's IPv6 handling.
            return ipv6 ? new IPAddress(address, unchecked((uint)Marshal.ReadInt32(row, 16))) : new IPAddress(address);
        }

        private static Tuple<string, string> ResolveProcess(int pid)
        {
            if (pid == 0) return Tuple.Create("系统 / 已结束连接", "无可查询的用户进程路径");
            string name = "PID " + pid;
            try { using (var process = Process.GetProcessById(pid)) name = process.ProcessName; }
            catch (ArgumentException) { return Tuple.Create(name, "进程已退出，路径不可用"); }
            catch (Win32Exception) { }
            catch (InvalidOperationException) { }
            IntPtr handle = OpenProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                return Tuple.Create(name, error == 5 ? "无权限读取受保护进程路径" : "进程已退出或路径不可用（" + error + "）");
            }
            try
            {
                int length = 32768; var path = new StringBuilder(length);
                if (QueryFullProcessImageName(handle, 0, path, ref length)) return Tuple.Create(name, path.ToString());
                int error = Marshal.GetLastWin32Error();
                return Tuple.Create(name, error == 5 ? "无权限读取受保护进程路径" : "无法读取路径（" + error + "）");
            }
            finally { CloseHandle(handle); }
        }

        [DllImport("iphlpapi.dll")] private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);
        [DllImport("iphlpapi.dll")] private static extern int GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
