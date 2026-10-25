using System;
using System.Globalization;

namespace CodexIsland
{
    internal static class CompactWidgetCatalog
    {
        public static readonly string[] Keys = { "codex-primary", "codex-secondary", "cpu", "memory", "battery", "clock", "focus", "awake", "notifications", "media", "ports", "activities" };
        private static readonly string[] Chinese = { "Codex 短期额度", "Codex 长期 / 7d 额度", "CPU 使用率", "内存使用率", "电池电量", "当前时间", "专注计时", "保持唤醒", "未读通知", "正在播放", "端口占用", "任务动态" };
        private static readonly string[] English = { "Codex short-term quota", "Codex long-term / 7d quota", "CPU usage", "Memory usage", "Battery", "Clock", "Focus timer", "Keep awake", "Unread notifications", "Now playing", "Port usage", "Task activities" };

        public static string Normalize(string key, string fallback)
        {
            for (int i = 0; i < Keys.Length; i++) if (String.Equals(Keys[i], key, StringComparison.Ordinal)) return Keys[i];
            return fallback == "codex-secondary" ? fallback : "codex-primary";
        }

        public static string LabelZhEn(string key, string language)
        {
            for (int i = 0; i < Keys.Length; i++) if (Keys[i] == key) return language == "en" ? English[i] : Chinese[i];
            return language == "en" ? English[0] : Chinese[0];
        }

        public static string Label(string key, string language) { return LabelZhEn(key, language); }
    }

    internal sealed class CompactWidgetData
    {
        public string Language = "zh";
        public DateTime LocalTime;
        public double? PrimaryRemaining, SecondaryRemaining, CpuPercent, MemoryPercent, BatteryPercent;
        public int? PrimaryMinutes, SecondaryMinutes;
        public bool QuotaStale, FocusRunning, AwakeRunning;
        public bool? OnAc;
        public int AlertThreshold = 15, UnreadCount;
        public string FocusStatus, FocusLabel, AwakeStatus;
        public MediaSnapshot Media;
        public string Lyric;
        public string PortsStatus;
        public string ActivityTitle, ActivityStatus;
        public double? ActivityProgress;
        public bool ActivityRunning;
    }

    internal sealed class CompactWidgetState
    {
        public string Key, Value, Caption, Tooltip, Accent = "#9BE9CA";
        public double? Fraction;
        public bool Warning, Stale, ShowRing;
        public System.Windows.Media.Imaging.BitmapSource Artwork;

        public static CompactWidgetState Create(string key, CompactWidgetData data)
        {
            if (data == null) throw new ArgumentNullException("data");
            key = CompactWidgetCatalog.Normalize(key, "codex-primary");
            bool english = data.Language == "en";
            var result = new CompactWidgetState { Key = key, Caption = CompactWidgetCatalog.Label(key, data.Language) };
            if (key == "codex-primary" || key == "codex-secondary")
            {
                bool primary = key == "codex-primary";
                double? remaining = FinitePercent(primary ? data.PrimaryRemaining : data.SecondaryRemaining);
                int? minutes = primary ? data.PrimaryMinutes : data.SecondaryMinutes;
                result.Caption = ShortWindow(minutes, primary ? (english ? "Short" : "短期") : (english ? "Long" : "长期"));
                result.Value = remaining.HasValue ? remaining.Value.ToString("0", CultureInfo.InvariantCulture) : "—";
                result.Fraction = remaining.HasValue ? remaining / 100 : null;
                result.ShowRing = true;
                result.Warning = remaining.HasValue && remaining.Value <= data.AlertThreshold;
                result.Stale = data.QuotaStale;
                result.Accent = primary ? "#9BE9CA" : "#C5B7F8";
                result.Tooltip = CompactWidgetCatalog.Label(key, data.Language) + " · " + (english ? "remaining " : "剩余 ") + Percent(remaining) + (data.QuotaStale ? (english ? " · last available data" : " · 上次同步数据") : "");
            }
            else if (key == "cpu" || key == "memory")
            {
                double? usage = FinitePercent(key == "cpu" ? data.CpuPercent : data.MemoryPercent);
                result.Value = Percent(usage); result.Fraction = usage.HasValue ? usage / 100 : null;
                result.ShowRing = true;
                result.Warning = usage.HasValue && usage.Value >= 85;
                result.Caption = key == "cpu" ? "CPU" : english ? "Memory" : "内存";
                result.Accent = key == "cpu" ? "#8FCFF5" : "#C5B7F8";
                result.Tooltip = CompactWidgetCatalog.Label(key, data.Language) + " · " + Percent(usage);
            }
            else if (key == "battery")
            {
                double? battery = FinitePercent(data.BatteryPercent);
                result.Value = battery.HasValue ? Percent(battery) : data.OnAc == true ? "AC" : "—";
                result.Caption = english ? "Battery" : "电量";
                result.Fraction = battery.HasValue ? battery / 100 : null;
                result.ShowRing = battery.HasValue;
                result.Warning = battery.HasValue && battery.Value <= 15 && data.OnAc != true;
                result.Tooltip = CompactWidgetCatalog.Label(key, data.Language) + " · " + (battery.HasValue ? Percent(battery) : english ? "unavailable" : "未知") + (data.OnAc == true ? (english ? " · plugged in" : " · 已接电") : "");
            }
            else if (key == "clock")
            {
                result.Value = data.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture);
                result.Caption = data.LocalTime.ToString("MM/dd", CultureInfo.InvariantCulture);
                result.Tooltip = (english ? "Local time · " : "本地时间 · ") + data.LocalTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                result.Accent = "#C5B7F8";
            }
            else if (key == "focus")
            {
                result.Value = TimerValue(data.FocusStatus);
                string label = data.FocusLabel ?? "专注";
                result.Caption = !english ? label : label == "休息" ? "Break" : label == "长休" ? "Long break" : label == "秒表" ? "Stopwatch" : label == "计时" ? "Timer" : "Focus";
                result.Tooltip = CompactWidgetCatalog.Label(key, data.Language) + " · " + result.Value + " · " + (data.FocusRunning ? (english ? "running" : "进行中") : (english ? "ready / paused" : "就绪 / 已暂停"));
                result.Accent = "#C5B7F8";
            }
            else if (key == "awake")
            {
                result.Value = data.AwakeRunning ? TimerValue(data.AwakeStatus) : english ? "Off" : "关闭";
                result.Caption = english ? "Awake" : "唤醒";
                result.Tooltip = CompactWidgetCatalog.Label(key, data.Language) + " · " + (data.AwakeRunning ? (english ? "remaining " : "剩余 ") + result.Value : english ? "inactive" : "未开启");
                result.Accent = "#F1CF8B";
            }
            else if (key == "ports")
            {
                result.Value = data.PortsStatus ?? "—"; result.Caption = english ? "Ports" : "端口";
                result.Tooltip = (english ? "Monitored port bindings: " : "监测端口的占用项：") + result.Value; result.Accent = "#8FCFF5";
            }
            else if (key == "activities")
            {
                result.Value = data.ActivityProgress.HasValue ? data.ActivityProgress.Value.ToString("0", CultureInfo.InvariantCulture) + "%" : data.ActivityRunning ? "…" : "—";
                result.Caption = String.IsNullOrWhiteSpace(data.ActivityTitle) ? (english ? "Tasks" : "任务") : data.ActivityTitle;
                result.Tooltip = result.Caption + (String.IsNullOrWhiteSpace(data.ActivityStatus) ? "" : "\n" + data.ActivityStatus);
                result.Fraction = data.ActivityProgress.HasValue ? data.ActivityProgress / 100 : null; result.ShowRing = data.ActivityProgress.HasValue; result.Accent = "#8FCFF5";
            }
            else if (key == "media")
            {
                var media = data.Media;
                result.Value = media != null && media.Available ? media.Title : "♪";
                result.Caption = media != null && media.Available ? media.Title : english ? "No music" : "暂无音乐";
                result.Artwork = media == null ? null : media.Artwork;
                result.Tooltip = media != null && media.Available ? media.Title + "\n" + media.Artist + (String.IsNullOrWhiteSpace(data.Lyric) ? "" : "\n" + data.Lyric) : english ? "Open a player that supports Windows media controls" : "打开支持 Windows 系统媒体控制的播放器";
                result.Accent = "#8FCFF5";
            }
            else
            {
                result.Value = Math.Max(0, data.UnreadCount).ToString(CultureInfo.InvariantCulture);
                result.Caption = english ? "Unread" : "未读";
                result.Tooltip = CompactWidgetCatalog.Label(key, data.Language) + " · " + result.Value;
                result.Accent = "#F1CF8B";
            }
            return result;
        }

        private static double? FinitePercent(double? value) { return value.HasValue && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value) ? (double?)Math.Max(0, Math.Min(100, value.Value)) : null; }
        private static string Percent(double? value) { return value.HasValue ? value.Value.ToString("0", CultureInfo.InvariantCulture) + "%" : "—"; }
        private static string ShortWindow(int? minutes, string fallback)
        {
            if (!minutes.HasValue || minutes.Value <= 0) return fallback;
            if (minutes.Value % 1440 == 0) return (minutes.Value / 1440).ToString(CultureInfo.InvariantCulture) + "d";
            if (minutes.Value % 60 == 0) return (minutes.Value / 60).ToString(CultureInfo.InvariantCulture) + "h";
            return minutes.Value.ToString(CultureInfo.InvariantCulture) + "m";
        }
        private static string TimerValue(string status)
        {
            if (String.IsNullOrWhiteSpace(status)) return "—";
            int space = status.LastIndexOf(' ');
            string value = space >= 0 ? status.Substring(space + 1) : status;
            for (int i = 0; i < value.Length; i++) if (!Char.IsDigit(value[i]) && value[i] != ':' && value[i] != '.') return "—";
            return value.Length == 0 ? "—" : value;
        }
    }
}
