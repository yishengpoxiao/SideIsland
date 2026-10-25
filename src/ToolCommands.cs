using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexIsland
{
    internal sealed class ToolCommand
    {
        internal string Kind, Argument, Error;
        internal int Minutes;
        internal string Description { get { return Error ?? UiLocalization.Localize("按 Enter 执行：") + Argument; } }
    }
    internal static class ToolCommandParser
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        internal static ToolCommand Parse(string input)
        {
            string text = (input ?? "").Trim(); if (text.Length > 4096) return new ToolCommand { Error = "命令内容过长。" };
            foreach (var tool in ToolCatalog.All) foreach (string label in new[] { tool.Label, UiLocalization.Localize(tool.Label) })
                if (String.Equals(text, label, StringComparison.OrdinalIgnoreCase) || (label.Length > 2 && String.Equals(text, label.Substring(2), StringComparison.OrdinalIgnoreCase))) return null;
            var focus = Regex.Match(text, @"\A(?:专注|focus)\s*(\d+)\s*(?:分钟|分|minutes?|mins?|m)?\z", Options);
            if (focus.Success)
            {
                int value; if (!Int32.TryParse(focus.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 1 || value > 1440) return new ToolCommand { Error = "请输入 1—1440 分钟" };
                return new ToolCommand { Kind = "focus", Argument = text, Minutes = value };
            }
            foreach (var pattern in new[] {
                new[] { "ports", @"\A(?:查端口\s*|端口\s+(?=\d)|ports?\s+|check\s+ports?\s+)(.+)\z" },
                new[] { "qr", @"\A(?:生成二维码|二维码|qr)\s+(.+)\z" },
                new[] { "scene", @"\A(?:启动场景|launch\s+scene)\s+(.+)\z" },
                new[] { "profile", @"\A(?:切换布局|应用布局|layout)\s+(.+)\z" },
                new[] { "template", @"\A(?:复制模板|template|copy\s+template)\s+(.+)\z" }
            })
            {
                var match = Regex.Match(text, pattern[1], Options | RegexOptions.Singleline); if (!match.Success) continue;
                var result = new ToolCommand { Kind = pattern[0], Argument = match.Groups[1].Value.Trim() };
                if (result.Argument.Length == 0) return null;
                if (result.Kind == "ports" && !Char.IsDigit(result.Argument[0]) && !Regex.IsMatch(text, @"\A(?:查端口|check\s+ports?)", Options)) return null;
                if (result.Kind == "ports") try { PortMonitor.ParsePorts(result.Argument); } catch (ArgumentException error) { result.Error = error.Message; }
                if (result.Kind == "qr") try { QrTransferTools.Validate(result.Argument, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Low); } catch (ArgumentException error) { result.Error = error.Message; }
                return result;
            }
            return null;
        }
    }
}
