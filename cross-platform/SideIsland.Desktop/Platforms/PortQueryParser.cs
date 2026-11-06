using System.Globalization;
using System.Text.RegularExpressions;

namespace SideIsland.Desktop.Platforms;

public sealed partial class CommandPlatformService
{
    internal static PortQuerySnapshot ParsePortCommand(string source, int exitCode, string output, string diagnostic,
        bool incomplete, int[] ports, DateTimeOffset observed, Func<int, string> resolvePath, CancellationToken cancellationToken = default)
    {
        int[] requested = ports.Distinct().Order().ToArray(); var issues = new List<PortReadIssue>();
        // lsof documents exit 1 for an empty selection as well as failures.
        bool emptyLsof = source == "lsof" && exitCode == 1 && string.IsNullOrWhiteSpace(output) && string.IsNullOrWhiteSpace(diagnostic) && !incomplete;
        if (exitCode != 0 && !emptyLsof) issues.Add(new("command", source + ": exit " + exitCode.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(diagnostic)) issues.Add(new("diagnostic", diagnostic));
        if (incomplete) issues.Add(new("truncated", source));
        var paths = new Dictionary<int, string>();
        string PathFor(int pid)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (paths.TryGetValue(pid, out string? path)) return path;
            try { path = resolvePath(pid) ?? ""; } catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { path = ""; }
            paths[pid] = path;
            if (path.Length == 0) issues.Add(new("path", "PID " + pid.ToString(CultureInfo.InvariantCulture)));
            return path;
        }
        List<PortOwner> rows;
        if (source == "ss") rows = ParseSsRecords(output, requested, issues, PathFor, cancellationToken);
        else if (source == "lsof") rows = ParseLsofRecords(output, requested, issues, PathFor, cancellationToken);
        else { rows = new(); issues.Add(new("unsupported", source)); }
        return new(requested, observed, source, rows.OrderBy(row => row.Port).ThenBy(row => row.Protocol, StringComparer.Ordinal).ThenBy(row => row.Pid).ToArray(), issues.Distinct().ToArray());
    }
    private static List<PortOwner> ParseSsRecords(string text, int[] requested, List<PortReadIssue> issues, Func<int, string> path, CancellationToken cancellationToken = default)
    {
        var wanted = requested.ToHashSet(); var rows = new List<PortOwner>(); int lineNumber = 0;
        if (text.Length > 0 && !text.EndsWith('\n')) issues.Add(new("parse", "ss: unterminated final line"));
        foreach (string line in text.Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++; if (string.IsNullOrWhiteSpace(line)) continue;
            var columns = Regex.Match(line, @"^\s*(?<protocol>tcp6?|udp6?)\s+(?<state>\S+)\s+\S+\s+\S+\s+(?<local>\S+)\s+(?<peer>\S+)(?:\s+(?<owners>.*))?$", RegexOptions.CultureInvariant);
            if (!columns.Success || EndpointPort(columns.Groups["local"].Value) is not int port)
            { issues.Add(new("parse", "ss: line " + lineNumber.ToString(CultureInfo.InvariantCulture))); continue; }
            if (!wanted.Contains(port)) continue;
            string protocol = columns.Groups["protocol"].Value.StartsWith("tcp", StringComparison.Ordinal) ? "TCP" : "UDP";
            string address = columns.Groups["local"].Value, peer = columns.Groups["peer"].Value, state = columns.Groups["state"].Value;
            string family = address.Contains('[') || address.Count(character => character == ':') > 1 ? "IPv6" : address.Contains('.') ? "IPv4" : "";
            string rawOwners = columns.Groups["owners"].Value;
            var owners = Regex.Matches(rawOwners, """\("(?<name>(?:[^"\\]|\\.)*)",pid=(?<pid>[0-9]+)(?:,[^)]*)?\)""", RegexOptions.CultureInvariant);
            if (rawOwners.Contains("users:", StringComparison.Ordinal))
            {
                string remainder = rawOwners;
                foreach (Match owner in owners.Cast<Match>().Reverse()) remainder = remainder.Remove(owner.Index, owner.Length);
                if (!Regex.IsMatch(remainder.Trim(), @"^users:\([,\s]*\)$", RegexOptions.CultureInvariant)) issues.Add(new("parse", "ss: incomplete owner data on line " + lineNumber.ToString(CultureInfo.InvariantCulture)));
            }
            int valid = 0;
            foreach (Match owner in owners)
            {
                if (!int.TryParse(owner.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
                { issues.Add(new("parse", "ss: invalid PID on line " + lineNumber.ToString(CultureInfo.InvariantCulture))); continue; }
                valid++; rows.Add(new(port, protocol, pid, owner.Groups["name"].Value, path(pid), address, peer, state, family));
            }
            if (valid == 0)
            {
                rows.Add(new(port, protocol, null, "", "", address, peer, state, family)); issues.Add(new("owner", "ss: port " + port.ToString(CultureInfo.InvariantCulture)));
                if (rawOwners.Contains("users:", StringComparison.Ordinal)) issues.Add(new("parse", "ss: incomplete owner data on line " + lineNumber.ToString(CultureInfo.InvariantCulture)));
            }
        }
        return rows;
    }
    private static List<PortOwner> ParseLsofRecords(string text, int[] requested, List<PortReadIssue> issues, Func<int, string> path, CancellationToken cancellationToken = default)
    {
        var wanted = requested.ToHashSet(); var rows = new List<PortOwner>();
        int? pid = null; string name = "", endpoint = "", protocol = "", state = "", family = ""; bool file = false, observedFile = false;
        void Flush()
        {
            if (file)
            {
                int? port = EndpointPort(endpoint);
                if (port is null || protocol is not ("TCP" or "UDP")) issues.Add(new("parse", "lsof: incomplete socket fields"));
                else if (wanted.Contains(port.Value))
                {
                    int peer = endpoint.IndexOf("->", StringComparison.Ordinal); string local = peer < 0 ? endpoint : endpoint[..peer]; string remote = peer < 0 ? "" : endpoint[(peer + 2)..];
                    if (pid is null || name.Length == 0) issues.Add(new("owner", "lsof: port " + port.Value.ToString(CultureInfo.InvariantCulture)));
                    rows.Add(new(port.Value, protocol, pid, name, pid is int id ? path(id) : "", local, remote, state, family));
                }
            }
            file = false; endpoint = protocol = state = family = "";
        }
        string[] fields = text.Split('\0');
        for (int index = 0; index < fields.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string field = fields[index].TrimStart('\r', '\n'); if (field.Length == 0) continue;
            if (index == fields.Length - 1) { issues.Add(new("parse", "lsof: unterminated final field")); break; }
            if (field.Length < 2) { issues.Add(new("parse", "lsof: empty field")); continue; }
            string value = field[1..];
            switch (field[0])
            {
                case 'p': Flush(); pid = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : null; name = ""; if (pid is null) issues.Add(new("parse", "lsof: invalid PID")); break;
                case 'c': name = value; break;
                case 'f': Flush(); file = true; observedFile = true; break;
                case 'P': protocol = value.ToUpperInvariant(); break;
                case 'n': endpoint = value; break;
                case 't': family = value; break;
                case 'T': if (value.StartsWith("ST=", StringComparison.Ordinal)) state = value[3..]; break;
                default: issues.Add(new("parse", "lsof: unrecognized field " + field[0])); break;
            }
        }
        Flush(); if (!observedFile && !string.IsNullOrWhiteSpace(text)) issues.Add(new("parse", "lsof: no complete file set")); return rows;
    }
}
