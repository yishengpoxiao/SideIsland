using System.Globalization;
using System.Text;

namespace SideIsland.Desktop.Platforms;

public sealed record PortOwner(int Port, string Protocol, int? Pid, string Name, string Path,
    string Address = "", string PeerAddress = "", string State = "", string Family = "");
public sealed record PortReadIssue(string Code, string Detail);
public sealed record PortQuerySnapshot(int[] RequestedPorts, DateTimeOffset ObservedUtc, string Source,
    IReadOnlyList<PortOwner> Owners, IReadOnlyList<PortReadIssue> Issues)
{
    public bool Complete => Issues.Count == 0;
}

internal static class PortQueryReport
{
    public const int MaximumCharacters = 1024 * 1024;
    public static int[] ParsePorts(string input)
    {
        var parts = (input ?? "").Split([',', '，', '、', ';', '；', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 64) throw new ArgumentException("ports");
        var result = new SortedSet<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535) throw new ArgumentException("ports");
            result.Add(port);
        }
        return result.ToArray();
    }
    public static string IssueText(PortReadIssue issue, bool english) => issue.Code switch
    {
        "command" => english ? "The query command did not complete successfully." : "查询命令未成功完成。",
        "diagnostic" => english ? "The query command reported a warning or error." : "查询命令报告了警告或错误。",
        "truncated" => english ? "Command output was incomplete or exceeded its limit." : "命令输出不完整或超出容量限制。",
        "parse" => english ? "Some socket records could not be decoded." : "部分套接字记录无法解析。",
        "owner" => english ? "Some process IDs or names are unavailable." : "部分进程 ID 或名称无法读取。",
        "path" => english ? "Some executable paths are unavailable." : "部分程序路径无法读取。",
        "demo" => english ? "Preview mode did not query system sockets." : "演示模式未查询系统套接字。",
        "unsupported" => english ? "This adapter supports macOS and Linux only." : "此适配器仅支持 macOS 和 Linux。",
        _ => english ? "The query is incomplete." : "查询不完整。"
    };
    public static bool TryFormat(PortQuerySnapshot snapshot, bool english, out string? report)
    {
        report = null; var output = new StringBuilder();
        bool Line(string value)
        { if (value.Length > MaximumCharacters - output.Length - Environment.NewLine.Length) return false; output.AppendLine(value); return true; }
        if (!Line(english ? "Side Island - port report" : "Side Island - 端口报告") ||
            !Line((english ? "Observed UTC: " : "采集时间 UTC：") + snapshot.ObservedUtc.ToString("o", CultureInfo.InvariantCulture)) ||
            !Line((english ? "Queried ports: " : "查询端口：") + string.Join(", ", snapshot.RequestedPorts)) ||
            !Line((english ? "Source: " : "来源：") + Field(snapshot.Source)) ||
            !Line(english ? "All parsed socket records; the display card limit does not truncate this report." : "包含全部已解析套接字记录，不受界面卡片数量限制。") ||
            !Line(snapshot.Complete ? english ? "No collection errors reported. No matching record does not guarantee a free port." : "未报告采集错误；没有匹配记录也不能保证端口空闲。" : english ? "Partial read: missing records do not prove that a port is free." : "读取不完整：没有记录不代表端口空闲。") ||
            !Line(english ? "Limited to sockets visible to the tool and current permissions; inactive bindings may be omitted, and sockets/PIDs can change after capture." : "结果受工具与当前权限可见范围限制，未活动绑定可能未列出；套接字与 PID 可能在采集后变化。")) return false;
        foreach (var issue in snapshot.Issues)
            if (!Line(string.Join("\t", [english ? "Read issue" : "读取问题", Field(issue.Code), Field(IssueText(issue, english)), Field(issue.Detail)]))) return false;
        if (!Line(english ? "Port\tProtocol\tFamily\tLocal endpoint\tPeer endpoint\tPID\tProgram\tPath\tState" : "端口\t协议\t地址族\t本地端点\t远端端点\tPID\t程序\t路径\t状态")) return false;
        foreach (var owner in snapshot.Owners)
            if (!Line(string.Join("\t", [owner.Port.ToString(CultureInfo.InvariantCulture), Field(owner.Protocol), Field(owner.Family), Field(owner.Address), Field(owner.PeerAddress), owner.Pid?.ToString(CultureInfo.InvariantCulture) ?? "", Field(owner.Name), Field(owner.Path), Field(owner.State)]))) return false;
        foreach (int port in snapshot.RequestedPorts.Where(port => !snapshot.Owners.Any(owner => owner.Port == port)))
            if (!Line(port.ToString(CultureInfo.InvariantCulture) + "\t\t\t\t\t\t\t\t" + (snapshot.Complete ? english ? "No matching local socket observed" : "未观察到匹配的本地套接字" : english ? "Unknown: collection incomplete" : "未知：采集不完整"))) return false;
        report = output.ToString(); return true;
    }
    private static string Field(string value) => value.IndexOfAny(['\t', '\r', '\n', '"']) < 0 ? value : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
