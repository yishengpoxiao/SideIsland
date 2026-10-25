using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    public sealed class WorkspaceSceneResource
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Target { get; set; }
        public string Arguments { get; set; }
        public WorkspaceSceneResource Copy() { return new WorkspaceSceneResource { Name = Name, Kind = Kind, Target = Target, Arguments = Arguments }; }
    }
    public sealed class WorkspaceScene
    {
        public string LayoutProfileId { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public List<WorkspaceSceneResource> Resources { get; set; }
        public WorkspaceScene Copy() { return new WorkspaceScene { Id = Id, Name = Name, LayoutProfileId = LayoutProfileId, Resources = (Resources ?? new List<WorkspaceSceneResource>()).Select(x => x.Copy()).ToList() }; }
    }
    internal sealed class WorkspaceSceneStore
    {
        public const int MaximumScenes = 20, MaximumResources = 30;
        private const int MaximumFileBytes = 2 * 1024 * 1024;
        private readonly string path;
        private bool unreadablePreserved;
        public bool LoadFailed { get; private set; }
        public WorkspaceSceneStore(string location = null) { path = Path.GetFullPath(location ?? Path.Combine(Path.GetDirectoryName(new SettingsStore().FilePath), "workspace-scenes.json")); }
        public List<WorkspaceScene> Load()
        {
            LoadFailed = false; unreadablePreserved = false;
            try
            {
                if (!File.Exists(path)) return new List<WorkspaceScene>();
                if (new FileInfo(path).Length > MaximumFileBytes) throw new IOException();
                string json = File.ReadAllText(path, new UTF8Encoding(false, true));
                if (!json.TrimStart().StartsWith("[", StringComparison.Ordinal)) throw new ArgumentException("场景文件必须为数组。");
                var scenes = new JavaScriptSerializer { MaxJsonLength = MaximumFileBytes, RecursionLimit = 12 }.Deserialize<List<WorkspaceScene>>(json);
                Validate(scenes); return scenes.Select(x => x.Copy()).ToList();
            }
            catch (IOException) { LoadFailed = true; } catch (UnauthorizedAccessException) { LoadFailed = true; }
            catch (ArgumentException) { LoadFailed = true; } catch (InvalidOperationException) { LoadFailed = true; }
            return new List<WorkspaceScene>();
        }
        public void Save(IEnumerable<WorkspaceScene> entries)
        {
            if (entries == null) throw new ArgumentNullException("entries");
            var list = entries.ToList(); Validate(list); string json = new JavaScriptSerializer { MaxJsonLength = MaximumFileBytes, RecursionLimit = 12 }.Serialize(list);
            byte[] bytes = new UTF8Encoding(false).GetBytes(json); if (bytes.Length > MaximumFileBytes) throw new ArgumentException("场景内容过多，请减少资源。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (LoadFailed && !unreadablePreserved && File.Exists(path)) { File.Copy(path, path + ".unreadable-" + Guid.NewGuid().ToString("N") + ".bak", false); unreadablePreserved = true; }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); LoadFailed = false;
            }
            finally { if (File.Exists(temporary)) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        private static void Validate(IList<WorkspaceScene> scenes)
        {
            if (scenes == null || scenes.Count > MaximumScenes) throw new ArgumentException("最多保存 20 个场景。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (WorkspaceScene scene in scenes)
            {
                Guid ignored;
                if (scene == null || !Guid.TryParseExact(scene.Id, "N", out ignored) || !ids.Add(scene.Id) || scene.Resources == null || scene.Resources.Count > MaximumResources) throw new ArgumentException("场景数据格式无效。");
                if (String.IsNullOrWhiteSpace(scene.Name) || scene.Name.Length > 60) throw new ArgumentException("每个场景都需要 1 至 60 个字符的名称。");
                if ((scene.LayoutProfileId ?? "").Length > 64 || (scene.LayoutProfileId ?? "").Any(Char.IsControl)) throw new ArgumentException("场景布局标识无效。");
                foreach (var item in scene.Resources) WorkspaceSceneLaunch.ValidateShape(item);
            }
        }
    }
    internal interface IWorkspaceSceneLaunchAdapter { void Start(ProcessStartInfo info); }
    internal sealed class SystemWorkspaceSceneLaunchAdapter : IWorkspaceSceneLaunchAdapter
    {
        public void Start(ProcessStartInfo info) { using (var process = Process.Start(info)) { } }
    }
    internal sealed class WorkspaceSceneLaunchResult
    {
        public int Opened { get; set; }
        public int Skipped { get; set; }
        public readonly List<string> Errors = new List<string>();
    }
    internal static class WorkspaceSceneLaunch
    {
        public static void ValidateShape(WorkspaceSceneResource resource)
        {
            if (resource == null || String.IsNullOrWhiteSpace(resource.Name) || resource.Name.Length > 60 || String.IsNullOrWhiteSpace(resource.Target) || resource.Target.Length > 2048 || resource.Target.Any(Char.IsControl) || (resource.Arguments ?? "").Length > 4096 || (resource.Arguments ?? "").IndexOf('\0') >= 0 || !new[] { "url", "folder", "program", "shortcut" }.Contains(resource.Kind)) throw new ArgumentException("请输入有效名称、路径或网址。");
            if (resource.Kind != "program" && !String.IsNullOrEmpty(resource.Arguments)) throw new ArgumentException("只有程序入口支持单独的启动参数。");
        }
        public static ProcessStartInfo CreateStartInfo(WorkspaceSceneResource item)
        {
            ValidateShape(item); string target = item.Target;
            if (item.Kind == "url")
            {
                Uri url;
                if (!Uri.TryCreate(target, UriKind.Absolute, out url) || (url.Scheme != "http" && url.Scheme != "https") || String.IsNullOrEmpty(url.Host)) throw new ArgumentException("网页地址必须为完整的 http 或 https 网址。");
                return new ProcessStartInfo { FileName = target, UseShellExecute = true };
            }
            try
            {
                string root = Path.GetPathRoot(target);
                if (!Path.IsPathRooted(target) || String.IsNullOrEmpty(root) || (!root.StartsWith("\\\\", StringComparison.Ordinal) && (root.Length < 3 || root[1] != ':' || (root[2] != '\\' && root[2] != '/')))) throw new ArgumentException("请使用完整绝对路径。");
                string absolute = Path.GetFullPath(target);
                if (item.Kind == "folder")
                {
                    if (!Directory.Exists(absolute)) throw new ArgumentException("文件夹不存在或不可访问。");
                    return new ProcessStartInfo { FileName = absolute, UseShellExecute = true };
                }
                string extension = Path.GetExtension(absolute);
                if ((item.Kind == "program" && !String.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)) || (item.Kind == "shortcut" && !String.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)) || !File.Exists(absolute)) throw new ArgumentException("程序或快捷方式不存在，请重新选择。");
                return new ProcessStartInfo { FileName = absolute, Arguments = item.Arguments ?? "", UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(absolute) };
            }
            catch (NotSupportedException) { throw new ArgumentException("路径格式不受支持。"); }
            catch (System.Security.SecurityException) { throw new ArgumentException("当前没有访问此路径的权限。"); }
        }
        public static WorkspaceSceneResource FromShortcut(QuickShortcut shortcut)
        {
            if (shortcut == null) throw new ArgumentNullException("shortcut"); Uri uri;
            string kind = Uri.TryCreate(shortcut.Target, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https") ? "url" : String.Equals(Path.GetExtension(shortcut.Target), ".exe", StringComparison.OrdinalIgnoreCase) ? "program" : String.Equals(Path.GetExtension(shortcut.Target), ".lnk", StringComparison.OrdinalIgnoreCase) ? "shortcut" : "folder";
            return new WorkspaceSceneResource { Name = shortcut.Name, Target = shortcut.Target, Kind = kind, Arguments = "" };
        }
        public static Task<WorkspaceSceneLaunchResult> RunAsync(IEnumerable<WorkspaceSceneResource> resources, bool demo, IWorkspaceSceneLaunchAdapter adapter, CancellationToken cancellation)
        {
            if (resources == null) throw new ArgumentNullException("resources"); if (adapter == null) throw new ArgumentNullException("adapter");
            var snapshot = resources.Take(WorkspaceSceneStore.MaximumResources + 1).Select(x => x.Copy()).ToList();
            if (snapshot.Count > WorkspaceSceneStore.MaximumResources) throw new ArgumentException("每个场景最多启动 30 个资源。");
            return Task.Run(() =>
            {
                var result = new WorkspaceSceneLaunchResult();
                foreach (var resource in snapshot)
                {
                    if (cancellation.IsCancellationRequested) { result.Skipped++; continue; }
                    try { var start = CreateStartInfo(resource); if (demo || cancellation.IsCancellationRequested) result.Skipped++; else { adapter.Start(start); result.Opened++; } }
                    catch (Exception error) { result.Errors.Add(resource.Name + "：" + Explain(error)); }
                }
                return result;
            });
        }
        private static string Explain(Exception error)
        {
            if (error is ArgumentException) return error.Message;
            if (error is UnauthorizedAccessException || error is System.Security.SecurityException) return "没有访问权限。";
            if (error is System.ComponentModel.Win32Exception) return "Windows 无法打开此资源，请检查关联程序或应用安装。";
            return "打开失败，请检查路径与应用是否可用。";
        }
    }
}
