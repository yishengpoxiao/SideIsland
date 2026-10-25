using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CodexIsland
{
    internal sealed class NetworkCounter
    {
        public ulong Received;
        public ulong Sent;
    }

    internal sealed class NetworkRates
    {
        public double? DownloadBytesPerSecond;
        public double? UploadBytesPerSecond;
    }

    // A baseline is kept per NIC. Plugging in or resetting an adapter must never
    // turn its lifetime byte count into a two-second traffic spike.
    internal sealed class NetworkRateCalculator
    {
        private Dictionary<string, NetworkCounter> previous;
        private double previousTime;

        public NetworkRates Update(IDictionary<string, NetworkCounter> counters, double seconds)
        {
            var rates = new NetworkRates();
            if (counters == null || Double.IsNaN(seconds) || Double.IsInfinity(seconds))
            {
                previous = null;
                return rates;
            }
            double elapsed = seconds - previousTime;
            if (previous != null && elapsed > 0 && elapsed <= 30)
            {
                double received = 0, sent = 0;
                int comparable = 0;
                foreach (var entry in counters)
                {
                    NetworkCounter before;
                    if (entry.Value == null || !previous.TryGetValue(entry.Key, out before)) continue;
                    if (entry.Value.Received < before.Received || entry.Value.Sent < before.Sent) continue;
                    received += entry.Value.Received - before.Received;
                    sent += entry.Value.Sent - before.Sent;
                    comparable++;
                }
                if (comparable > 0 || counters.Count == 0)
                {
                    rates.DownloadBytesPerSecond = received / elapsed;
                    rates.UploadBytesPerSecond = sent / elapsed;
                }
            }
            previous = new Dictionary<string, NetworkCounter>(StringComparer.Ordinal);
            foreach (var entry in counters)
                if (entry.Value != null)
                    previous[entry.Key] = new NetworkCounter { Received = entry.Value.Received, Sent = entry.Value.Sent };
            previousTime = seconds;
            return rates;
        }

        public void Reset() { previous = null; }
    }

    internal sealed class NetworkSnapshot
    {
        public bool Available;
        public bool Partial;
        public int ConnectedAdapters;
        public string AdapterNames = "";
        public string LocalAddresses = "";
        public NetworkRates Rates = new NetworkRates();
    }

    internal sealed class NetworkStatsReader
    {
        private readonly NetworkRateCalculator calculator = new NetworkRateCalculator();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct InterfaceRow
        {
            public ulong Luid;
            public uint Index;
            public Guid Guid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
            public uint PhysicalAddressLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentAddress;
            public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
            public byte Flags;
            public uint OperStatus, AdminStatus, MediaConnectState;
            public Guid NetworkGuid;
            public uint ConnectionType;
            public ulong TransmitLinkSpeed, ReceiveLinkSpeed;
            public ulong InOctets, InUnicastPackets, InOtherPackets, InDiscards, InErrors, InUnknownProtocols;
            public ulong InUnicastOctets, InMulticastOctets, InBroadcastOctets;
            public ulong OutOctets, OutUnicastPackets, OutOtherPackets, OutDiscards, OutErrors;
            public ulong OutUnicastOctets, OutMulticastOctets, OutBroadcastOctets, OutQueueLength;
        }

        [DllImport("iphlpapi.dll")]
        private static extern uint GetIfEntry2(ref InterfaceRow row);

        public void Reset() { calculator.Reset(); }

        public NetworkSnapshot Read(double monotonicSeconds)
        {
            var result = new NetworkSnapshot();
            var counters = new Dictionary<string, NetworkCounter>(StringComparer.Ordinal);
            var names = new List<string>();
            var addresses = new List<string>();
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback || adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    try
                    {
                        var properties = adapter.GetIPProperties();
                        int index = 0;
                        if (adapter.Supports(NetworkInterfaceComponent.IPv4)) index = properties.GetIPv4Properties().Index;
                        else if (adapter.Supports(NetworkInterfaceComponent.IPv6)) index = properties.GetIPv6Properties().Index;
                        if (index == 0) continue;
                        var row = new InterfaceRow { Index = (uint)index };
                        if (GetIfEntry2(ref row) != 0) { result.Partial = true; continue; }
                        // HardwareInterface, excluding FilterInterface. VPN/tunnel and
                        // virtual adapters otherwise count the same traffic twice.
                        if ((row.Flags & 1) == 0 || (row.Flags & 2) != 0 || row.OperStatus != 1) continue;
                        counters[adapter.Id] = new NetworkCounter { Received = row.InOctets, Sent = row.OutOctets };
                        names.Add(adapter.Name);
                        foreach (var address in properties.UnicastAddresses)
                            if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                string text = address.Address.ToString();
                                if (!addresses.Contains(text)) addresses.Add(text);
                            }
                    }
                    catch (NetworkInformationException) { result.Partial = true; }
                    catch (NotSupportedException) { result.Partial = true; }
                }
                result.Available = true;
                result.ConnectedAdapters = counters.Count;
                result.AdapterNames = String.Join(" / ", names);
                result.LocalAddresses = String.Join(" · ", addresses);
                result.Rates = calculator.Update(counters, monotonicSeconds);
            }
            catch (NetworkInformationException) { calculator.Reset(); }
            catch (DllNotFoundException) { calculator.Reset(); }
            catch (EntryPointNotFoundException) { calculator.Reset(); }
            catch (System.Security.SecurityException) { calculator.Reset(); }
            return result;
        }
    }

    internal static class SystemFormats
    {
        public static string Bytes(double bytes)
        {
            if (Double.IsNaN(bytes) || Double.IsInfinity(bytes) || bytes < 0) return "—";
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            int unit = 0;
            while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
            return bytes.ToString(unit == 0 ? "0" : bytes >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + units[unit];
        }

        public static string Rate(double? bytes) { return bytes.HasValue && bytes.Value >= 0 && !Double.IsNaN(bytes.Value) && !Double.IsInfinity(bytes.Value) ? Bytes(bytes.Value) + "/s" : "采样中"; }

        public static string Uptime(TimeSpan uptime)
        {
            if (uptime < TimeSpan.Zero) return "—";
            if (uptime.TotalDays >= 1) return ((long)uptime.TotalDays).ToString(CultureInfo.CurrentCulture) + " 天 " + uptime.Hours + " 时 " + uptime.Minutes + " 分";
            if (uptime.TotalHours >= 1) return uptime.Hours + " 时 " + uptime.Minutes + " 分";
            return uptime.Minutes + " 分 " + uptime.Seconds + " 秒";
        }
    }
}
