using System;
using System.Collections.Generic;

namespace CodexIsland
{
    internal static class SystemToolsTests
    {
        private static Dictionary<string, NetworkCounter> Sample(string adapter, ulong received, ulong sent)
        {
            return new Dictionary<string, NetworkCounter> { { adapter, new NetworkCounter { Received = received, Sent = sent } } };
        }

        public static void Run()
        {
            var calculator = new NetworkRateCalculator();
            Check(!calculator.Update(Sample("wifi", 100, 200), 1).DownloadBytesPerSecond.HasValue, "First sample does not report lifetime bytes as throughput");
            var rates = calculator.Update(Sample("wifi", 1124, 712), 3);
            Check(rates.DownloadBytesPerSecond == 512 && rates.UploadBytesPerSecond == 256, "Two-second counter deltas yield bytes per second");
            rates = calculator.Update(Sample("wifi", 10, 20), 5);
            Check(!rates.DownloadBytesPerSecond.HasValue, "Reset counters produce no negative spike");
            rates = calculator.Update(Sample("wifi", 210, 420), 7);
            Check(rates.DownloadBytesPerSecond == 100 && rates.UploadBytesPerSecond == 200, "Sampling resumes after counter reset");
            rates = calculator.Update(Sample("ethernet", 900000000, 800000000), 9);
            Check(!rates.DownloadBytesPerSecond.HasValue, "New adapters get a baseline rather than a lifetime-byte spike");
            var both = Sample("ethernet", 900000100, 800000200);
            both.Add("wifi", new NetworkCounter { Received = 100000000, Sent = 100000000 });
            rates = calculator.Update(both, 11);
            Check(rates.DownloadBytesPerSecond == 50 && rates.UploadBytesPerSecond == 100, "Only established adapters contribute when a new NIC appears");
            both = Sample("ethernet", 900000300, 800000600);
            both.Add("wifi", new NetworkCounter { Received = 100000400, Sent = 100000800 });
            rates = calculator.Update(both, 13);
            Check(rates.DownloadBytesPerSecond == 300 && rates.UploadBytesPerSecond == 600, "Physical NIC rates aggregate independently");
            Check(!calculator.Update(both, 13).DownloadBytesPerSecond.HasValue, "Zero elapsed time remains unknown");
            Check(!calculator.Update(both, 12).DownloadBytesPerSecond.HasValue, "Backwards time remains unknown");
            Check(!calculator.Update(both, 1000).DownloadBytesPerSecond.HasValue, "Resume after long sleep establishes a fresh baseline");
            Check(!calculator.Update(null, 1002).DownloadBytesPerSecond.HasValue, "Missing sample clears baseline");
            Check(!calculator.Update(both, 1004).DownloadBytesPerSecond.HasValue, "A read failure cannot bridge stale counters");
            rates = calculator.Update(new Dictionary<string, NetworkCounter>(), 1006);
            Check(rates.DownloadBytesPerSecond == 0 && rates.UploadBytesPerSecond == 0, "No connected NICs has zero throughput after baseline");
            calculator.Reset();
            Check(!calculator.Update(both, 1008).DownloadBytesPerSecond.HasValue, "Explicit restart clears baseline");

            Check(SystemFormats.Bytes(1024).EndsWith(" KB") && SystemFormats.Bytes(1024 * 1024).EndsWith(" MB"), "Binary size units scale");
            Check(SystemFormats.Bytes(-1) == "—" && SystemFormats.Bytes(Double.NaN) == "—", "Invalid byte sizes stay unknown");
            Check(SystemFormats.Rate(0) == "0 B/s" && SystemFormats.Rate(null) == "采样中", "Idle and pending rates differ");
            Check(SystemFormats.Uptime(TimeSpan.FromHours(49)).Contains("2 天 1 时"), "Uptime preserves days without wrapping");
            Check(SystemFormats.Uptime(TimeSpan.FromSeconds(-1)) == "—", "Invalid uptime stays unknown");

            // Read-only OS telemetry. Deliberately print no adapter IDs or IPs.
            var device = DeviceStatsReader.Read();
            Check(device.Uptime.HasValue && device.Uptime >= TimeSpan.Zero, "Local monotonic uptime is available");
            Check(device.ProcessCount.HasValue && device.ProcessCount > 0, "Local process count is available");
            Check(device.DriveTotalBytes.HasValue && device.DriveTotalBytes > 0 && device.DriveAvailableBytes >= 0 && device.DriveAvailableBytes <= device.DriveTotalBytes, "System disk sizes are sane");
            var network = new NetworkStatsReader().Read(1);
            Check(network.Available, "Native interface inspection is available");
            Check(network.ConnectedAdapters >= 0 && !network.Rates.DownloadBytesPerSecond.HasValue, "Native first sample remains pending");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("System tools: " + message);
        }
    }
}
