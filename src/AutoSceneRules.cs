using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodexIsland
{
    internal sealed class AutoSceneRule
    {
        public string Id = Guid.NewGuid().ToString("N"), Name = "", Executable = "", Start = "", End = "", Monitor = "", Power = "any", ProfileId = "code";
        public bool Enabled = true;
        public int Priority = 50, CooldownSeconds = 30;
        internal AutoSceneRule Copy() { return (AutoSceneRule)MemberwiseClone(); }
    }
    internal sealed class AutoSceneRuleData
    {
        public bool Enabled;
        public List<AutoSceneRule> Rules = new List<AutoSceneRule>();
    }
    internal sealed class AutoSceneContext
    {
        internal string Executable = "", Monitor = "";
        internal bool? OnAc;
        internal bool OwnProcess;
        internal DateTime LocalTime;
    }
    internal static class AutoScenePolicy
    {
        internal static string NormalizeExecutable(string value)
        {
            value = (value ?? "").Trim();
            if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - 4);
            return value.ToLowerInvariant();
        }
        internal static bool TryMinute(string text, out int minute)
        {
            minute = 0; TimeSpan time;
            if (!TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out time) || time.TotalHours >= 24) return false;
            minute = (int)time.TotalMinutes; return true;
        }
        internal static bool Validate(AutoSceneRule value, out string error)
        {
            error = null; Guid id; int start, end;
            if (value == null || !Guid.TryParseExact(value.Id, "N", out id)) error = "规则标识无效。";
            else if (value.Name == null || value.Executable == null || value.Start == null || value.End == null || value.Monitor == null || value.Power == null || value.ProfileId == null) error = "规则字段无效。";
            else if (String.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 60 || value.Name.Any(Char.IsControl)) error = "请输入不超过 60 字符的规则名称。";
            else if (value.Executable.Length > 128 || value.Executable.Any(c => Char.IsControl(c) || "\\/:*?\"<>|".IndexOf(c) >= 0)) error = "应用条件只填写程序名称，例如 Code.exe。";
            else if (value.Monitor.Length > 128 || value.Monitor.Any(Char.IsControl)) error = "显示器名称无效。";
            else if ((value.Start.Length == 0) != (value.End.Length == 0) || (value.Start.Length != 0 && (!TryMinute(value.Start, out start) || !TryMinute(value.End, out end)))) error = "时间条件需同时填写有效的 HH:mm 开始与结束时间。";
            else if (value.Power != "any" && value.Power != "ac" && value.Power != "battery") error = "电源条件无效。";
            else if (String.IsNullOrWhiteSpace(value.ProfileId) || value.ProfileId.Length > 64 || value.ProfileId.Any(Char.IsControl)) error = "请选择目标场景布局。";
            else if (value.Priority < 0 || value.Priority > 100 || value.CooldownSeconds < 5 || value.CooldownSeconds > 3600) error = "优先级范围为 0—100，冷却时间为 5—3600 秒。";
            else if (String.IsNullOrWhiteSpace(value.Executable) && value.Start.Length == 0 && value.Monitor.Length == 0 && value.Power == "any") error = "至少设置一个应用、时间、显示器或电源条件。";
            return error == null;
        }
        internal static bool Matches(AutoSceneRule rule, AutoSceneContext context)
        {
            if (rule == null || context == null || context.OwnProcess || !rule.Enabled) return false;
            if (rule.Executable.Length != 0 && NormalizeExecutable(rule.Executable) != NormalizeExecutable(context.Executable)) return false;
            if (rule.Monitor.Length != 0 && !String.Equals(rule.Monitor, context.Monitor, StringComparison.OrdinalIgnoreCase)) return false;
            if (rule.Power == "ac" && context.OnAc != true) return false;
            if (rule.Power == "battery" && context.OnAc != false) return false;
            if (rule.Start.Length != 0)
            {
                int start, end; if (!TryMinute(rule.Start, out start) || !TryMinute(rule.End, out end)) return false;
                int minute = context.LocalTime.Hour * 60 + context.LocalTime.Minute;
                // Equal endpoints mean a full day; overnight ranges cross midnight.
                if (start != end && (start < end ? minute < start || minute >= end : minute < start && minute >= end)) return false;
            }
            return true;
        }
    }
    internal sealed class AutoSceneRuleStore
    {
        private readonly LocalDataFile<AutoSceneRuleData> file;
        internal AutoSceneRuleData Data { get; private set; }
        internal AutoSceneRuleStore(bool demo)
        {
            file = new LocalDataFile<AutoSceneRuleData>("auto-scene-rules.json", demo);
            Data = file.Load(new AutoSceneRuleData());
            var clean = new List<AutoSceneRule>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in Data.Rules ?? new List<AutoSceneRule>())
            {
                string error;
                if (clean.Count < 40 && AutoScenePolicy.Validate(rule, out error) && ids.Add(rule.Id)) clean.Add(rule);
                else file.MarkInvalid();
            }
            Data.Rules = clean;
        }
        internal void Save(AutoSceneRuleData value)
        {
            if (value == null || value.Rules == null || value.Rules.Count > 40) throw new ArgumentException("最多保存 40 条自动规则。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in value.Rules) { string error; if (!AutoScenePolicy.Validate(rule, out error) || !ids.Add(rule.Id)) throw new ArgumentException(error ?? "规则标识重复。"); }
            file.Save(value); Data = value;
        }
    }
    internal sealed class AutoSceneRuleEngine
    {
        private string candidate = "", appliedRule = "", appliedProfile = "";
        private DateTime candidateSince, lastApplied, pausedUntil;
        internal DateTime PausedUntil { get { return pausedUntil; } }
        internal void Pause(DateTime now, TimeSpan duration) { pausedUntil = now.Add(duration); candidate = ""; }
        internal void Resume() { pausedUntil = DateTime.MinValue; candidate = ""; }
        internal void Reset() { candidate = appliedRule = appliedProfile = ""; candidateSince = lastApplied = DateTime.MinValue; }
        internal AutoSceneRule Evaluate(AutoSceneRuleData data, AutoSceneContext context, DateTime utc, Func<string, bool> profileExists)
        {
            if (data == null || !data.Enabled || context == null || utc < pausedUntil || context.OwnProcess) { candidate = ""; return null; }
            AutoSceneRule winner = (data.Rules ?? new List<AutoSceneRule>()).Where(r => AutoScenePolicy.Matches(r, context) && profileExists(r.ProfileId))
                .OrderByDescending(r => r.Priority).ThenBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();
            if (winner == null) { candidate = ""; appliedRule = ""; return null; }
            if (candidate != winner.Id) { candidate = winner.Id; candidateSince = utc; return null; }
            if ((utc - candidateSince).TotalMilliseconds < 1200) return null;
            if (appliedRule == winner.Id && appliedProfile == winner.ProfileId) return null;
            if (lastApplied != DateTime.MinValue && (utc - lastApplied).TotalSeconds < winner.CooldownSeconds) return null;
            return winner;
        }
        internal void MarkApplied(AutoSceneRule rule, DateTime utc)
        { appliedRule = rule.Id; appliedProfile = rule.ProfileId; lastApplied = utc; }
    }
}
