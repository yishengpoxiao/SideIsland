using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("SideIsland.Desktop")]
[assembly: InternalsVisibleTo("SideIsland.Tests")]

namespace System.Web.Script.Serialization
{
    // Compatibility boundary for linked, already-tested JSON consumers. No
    // System.Web or platform-specific runtime is part of the portable build.
    internal sealed class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; } = 2 * 1024 * 1024;
        public int RecursionLimit { get; set; } = 64;
        private JsonSerializerOptions Options => new() { IncludeFields = true, MaxDepth = Math.Clamp(RecursionLimit, 1, 128), PropertyNameCaseInsensitive = true };
        public string Serialize(object value) { var result = JsonSerializer.Serialize(value, Options); if (result.Length > MaxJsonLength) throw new ArgumentException("JSON too long"); return result; }
        public T Deserialize<T>(string value) { Check(value); return JsonSerializer.Deserialize<T>(value, Options)!; }
        public object DeserializeObject(string value)
        {
            Check(value); using var doc = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = Math.Clamp(RecursionLimit, 1, 128) }); return Convert(doc.RootElement);
        }
        private void Check(string value) { if (value == null || value.Length > MaxJsonLength) throw new ArgumentException("Invalid JSON length"); }
        private static object Convert(JsonElement node) => node.ValueKind switch
        {
            JsonValueKind.Object => node.EnumerateObject().ToDictionary(p => p.Name, p => Convert(p.Value), StringComparer.Ordinal),
            JsonValueKind.Array => node.EnumerateArray().Select(Convert).ToArray(),
            JsonValueKind.String => node.GetString(), JsonValueKind.True => true, JsonValueKind.False => false,
            JsonValueKind.Number => Number(node),
            _ => null
        };
        private static object Number(JsonElement node) { if (node.TryGetInt32(out var i)) return i; if (node.TryGetInt64(out var l)) return l; if (node.TryGetDecimal(out var d)) return d; return node.GetDouble(); }
    }
}
namespace CodexIsland
{
    internal static class UiLocalization
    {
        public static string Language { get; set; } = "zh";
        private static readonly Dictionary<string, string> Labels = new() { ["后台任务"]="Background task",["本地接口"]="Local input",["批量文件处理"]="Batch files",["批量文件"]="Batch files",["任务守望"]="Task watch",["服务就绪"]="Service ready",["等待中"]="Queued",["进行中"]="Running",["已完成"]="Completed",["已取消"]="Cancelled",["失败"]="Failed" };
        public static bool HasTranslation(string value) => Labels.ContainsKey(value ?? "");
        public static string Localize(string value) => Language == "en" && Labels.TryGetValue(value ?? "", out var translated) ? translated : value;
    }
}
