using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexIsland
{
    internal sealed class MediaPlayerOption
    {
        public string SourceId;
        public bool Playing;
    }
    internal sealed class MediaSessionEndpoint
    {
        internal string SourceId;
        internal bool Playing, Current;
        internal Func<CancellationToken, Task<MediaSnapshot>> Read;
        internal Func<string, CancellationToken, Task<string>> Send;
    }
    internal static class MediaSessionSelection
    {
        internal static string NormalizeSource(string value)
        { return String.IsNullOrWhiteSpace(value) || value.Length > 512 || value.IndexOf('\0') >= 0 ? "" : value; }
        // WinRT can wrap GetCurrentSession and GetSessions results in different
        // managed objects. The official app identity remains stable across wrappers.
        internal static bool IsCurrentSource(string sourceId, string currentSourceId)
        { return !String.IsNullOrWhiteSpace(sourceId) && !String.IsNullOrWhiteSpace(currentSourceId) && String.Equals(sourceId, currentSourceId, StringComparison.Ordinal); }
        internal static MediaSessionEndpoint Select(IEnumerable<MediaSessionEndpoint> source, string preferredSource)
        {
            var available = (source ?? Enumerable.Empty<MediaSessionEndpoint>()).Where(item => item != null).ToArray();
            if (!String.IsNullOrEmpty(preferredSource)) available = available.Where(item => String.Equals(item.SourceId, preferredSource, StringComparison.Ordinal)).ToArray();
            return available.FirstOrDefault(item => item.Current && item.Playing) ?? available.FirstOrDefault(item => item.Playing) ?? available.FirstOrDefault(item => item.Current) ?? available.FirstOrDefault();
        }
        internal static MediaPlayerOption[] Options(IEnumerable<MediaSessionEndpoint> source)
        {
            return (source ?? Enumerable.Empty<MediaSessionEndpoint>()).Where(item => item != null && !String.IsNullOrEmpty(NormalizeSource(item.SourceId)))
                .GroupBy(item => item.SourceId, StringComparer.Ordinal).Take(64)
                .Select(group => new MediaPlayerOption { SourceId = group.Key, Playing = group.Any(item => item.Playing) })
                .OrderBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
