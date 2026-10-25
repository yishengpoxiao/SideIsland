using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    internal sealed class ClipboardHistoryItem
    {
        public string Id { get; set; }
        public string Text { get; set; }
        [ScriptIgnore] public byte[] Png { get; set; }
        public string ImageBase64 { get { return Png == null ? null : Convert.ToBase64String(Png); } set { if (value != null && value.Length > 1400000) throw new ArgumentException("图片内容过多。"); Png = value == null ? null : Convert.FromBase64String(value); } }
        public string Source { get; set; }
        public DateTime CreatedUtc { get; set; }
        public bool Favorite { get; set; }
        public bool IsImage { get { return Png != null; } }
        public ClipboardHistoryItem Copy() { return new ClipboardHistoryItem { Id = Id, Text = Text, Png = Png == null ? null : (byte[])Png.Clone(), Source = Source, CreatedUtc = CreatedUtc, Favorite = Favorite }; }
    }
    internal sealed class ClipboardHistoryData
    {
        public List<ClipboardHistoryItem> Favorites { get; set; }
        public List<string> ExcludedApps { get; set; }
    }
    internal sealed class ClipboardHistoryBook
    {
        public const int MaximumEntries = 50, MaximumFavorites = 20, MaximumTextLength = 65536, MaximumImageBytes = 1024 * 1024, MaximumTotalBytes = 8 * 1024 * 1024;
        private readonly List<ClipboardHistoryItem> items = new List<ClipboardHistoryItem>();
        private readonly HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool Paused = true;
        public int Count { get { return items.Count; } }
        public IList<ClipboardHistoryItem> Items { get { return items.Select(x => x.Copy()).ToList(); } }
        public IList<string> ExcludedApps { get { return excluded.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(); } }
        public static string NormalizeApp(string name)
        {
            string value = (name ?? "").Trim();
            if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - 4);
            if (value.Length == 0 || value.Length > 100 || value.Any(c => Char.IsControl(c) || c == '\\' || c == '/' || c == ':' || c == '*' || c == '?')) throw new ArgumentException("请输入进程名称，例如 KeePass.exe。");
            return value.ToLowerInvariant();
        }
        public void SetExcludedApps(string value)
        {
            string[] values = (value ?? "").Split(new[] { ',', ';', '\r', '\n', '，', '；' }, StringSplitOptions.RemoveEmptyEntries).Select(NormalizeApp).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (values.Length > 40) throw new ArgumentException("最多排除 40 个应用。");
            excluded.Clear(); foreach (string app in values) excluded.Add(app);
        }
        public bool IsExcluded(string source) { return !String.IsNullOrWhiteSpace(source) && excluded.Contains(NormalizeApp(source)); }
        public bool Add(string text, byte[] png, string source, bool protectedContent)
        {
            if (Paused || protectedContent || IsExcluded(source)) return false;
            if (png != null && (png.Length == 0 || png.Length > MaximumImageBytes)) return false;
            if (png == null && (String.IsNullOrEmpty(text) || text.Length > MaximumTextLength)) return false;
            string origin = String.IsNullOrWhiteSpace(source) ? "" : NormalizeApp(source);
            var duplicate = items.FirstOrDefault(x => png == null ? !x.IsImage && x.Text == text : x.IsImage && x.Png.SequenceEqual(png));
            if (duplicate != null) { items.Remove(duplicate); duplicate.CreatedUtc = DateTime.UtcNow; duplicate.Source = origin; items.Insert(0, duplicate); return true; }
            var item = new ClipboardHistoryItem { Id = Guid.NewGuid().ToString("N"), Text = png == null ? text : null, Png = png == null ? null : (byte[])png.Clone(), Source = origin, CreatedUtc = DateTime.UtcNow };
            items.Insert(0, item); Trim(); return items.Contains(item);
        }
        private void Trim()
        {
            while (items.Count > MaximumEntries || items.Sum(item => Size(item)) > MaximumTotalBytes)
            {
                var removable = items.LastOrDefault(x => !x.Favorite); if (removable == null) break; items.Remove(removable);
            }
        }
        private static int Size(ClipboardHistoryItem item) { return item.IsImage ? item.Png.Length : (item.Text ?? "").Length * 2; }
        public void ToggleFavorite(string id)
        {
            var item = items.FirstOrDefault(x => x.Id == id); if (item == null) return;
            if (!item.Favorite && items.Count(x => x.Favorite) >= MaximumFavorites) throw new ArgumentException("最多收藏 20 项。");
            item.Favorite = !item.Favorite; Trim();
        }
        public void Remove(string id) { items.RemoveAll(x => x.Id == id); }
        public void Clear(bool includeFavorites) { items.RemoveAll(x => includeFavorites || !x.Favorite); }
        public IEnumerable<ClipboardHistoryItem> Search(string term, bool favoritesOnly)
        {
            string search = term ?? "";
            return items.Where(x => (!favoritesOnly || x.Favorite) && (search.Length == 0 || (x.Text ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || (x.Source ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)).Select(x => x.Copy()).ToList();
        }
        public ClipboardHistoryData Export() { return new ClipboardHistoryData { Favorites = items.Where(x => x.Favorite).Select(x => x.Copy()).ToList(), ExcludedApps = ExcludedApps.ToList() }; }
        public void Restore(ClipboardHistoryData data)
        {
            if (data == null) return;
            SetExcludedApps(String.Join(",", data.ExcludedApps ?? new List<string>()));
            foreach (var item in (data.Favorites ?? new List<ClipboardHistoryItem>()).AsEnumerable().Reverse())
                if (!items.Any(x => x.Id == item.Id)) items.Insert(0, item.Copy());
            Trim();
        }
    }
    internal sealed class ClipboardHistoryStore
    {
        private const int MaximumStoreBytes = 14 * 1024 * 1024;
        private readonly string path;
        private bool loadFailed, preserved;
        public bool LoadFailed { get { return loadFailed; } }
        public ClipboardHistoryStore(string location) { path = Path.GetFullPath(location); }
        public ClipboardHistoryData Load()
        {
            loadFailed = false; preserved = false;
            try
            {
                if (!File.Exists(path)) return Empty();
                if (new FileInfo(path).Length > MaximumStoreBytes) throw new ArgumentException();
                var data = new JavaScriptSerializer { MaxJsonLength = MaximumStoreBytes, RecursionLimit = 12 }.Deserialize<ClipboardHistoryData>(File.ReadAllText(path, new UTF8Encoding(false, true)));
                Validate(data); return data;
            }
            catch (Exception error) { if (!(error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException || error is FormatException)) throw; loadFailed = true; return Empty(); }
        }
        private static ClipboardHistoryData Empty() { return new ClipboardHistoryData { Favorites = new List<ClipboardHistoryItem>(), ExcludedApps = new List<string>() }; }
        private static void Validate(ClipboardHistoryData data)
        {
            if (data == null || data.Favorites == null || data.ExcludedApps == null || data.Favorites.Count > ClipboardHistoryBook.MaximumFavorites || data.ExcludedApps.Count > 40) throw new ArgumentException();
            var ids = new HashSet<string>(); long size = 0;
            foreach (var item in data.Favorites)
            {
                Guid identity;
                if (item == null || !Guid.TryParseExact(item.Id, "N", out identity) || !ids.Add(item.Id) || !item.Favorite || item.CreatedUtc > DateTime.UtcNow.AddDays(1)) throw new ArgumentException();
                if (item.Png == null) { if (String.IsNullOrEmpty(item.Text) || item.Text.Length > ClipboardHistoryBook.MaximumTextLength) throw new ArgumentException(); size += item.Text.Length * 2; }
                else { if (item.Png.Length == 0 || item.Png.Length > ClipboardHistoryBook.MaximumImageBytes || item.Text != null) throw new ArgumentException(); ClipboardHistoryImage.Decode(item.Png, 128); size += item.Png.Length; }
                if (!String.IsNullOrEmpty(item.Source)) ClipboardHistoryBook.NormalizeApp(item.Source);
            }
            if (size > ClipboardHistoryBook.MaximumTotalBytes) throw new ArgumentException();
            foreach (string app in data.ExcludedApps) ClipboardHistoryBook.NormalizeApp(app);
        }
        public void Save(ClipboardHistoryData data)
        {
            Validate(data); Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string json = new JavaScriptSerializer { MaxJsonLength = MaximumStoreBytes, RecursionLimit = 12 }.Serialize(data);
                byte[] bytes = new UTF8Encoding(false).GetBytes(json); if (bytes.Length > MaximumStoreBytes) throw new ArgumentException("收藏内容过多。");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (loadFailed && !preserved && File.Exists(path)) { File.Copy(path, path + ".unreadable-" + Guid.NewGuid().ToString("N") + ".bak", false); preserved = true; }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); loadFailed = false;
            }
            finally { if (File.Exists(temporary)) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
    internal static class ClipboardHistoryImage
    {
        public static BitmapSource Decode(byte[] png, int maximumEdge = 1600)
        {
            if (png == null || png.Length == 0 || png.Length > ClipboardHistoryBook.MaximumImageBytes) throw new ArgumentException("图片内容无效。");
            int width, height; ValidateHeader(png, out width, out height);
            using (var stream = new MemoryStream(png, false))
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.DecodePixelWidth = Math.Max(1, (int)(width * Math.Min(1, Math.Max(1, maximumEdge) / (double)Math.Max(width, height)))); bitmap.EndInit();
                if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || bitmap.PixelWidth > 1600 || bitmap.PixelHeight > 1600) throw new ArgumentException("图片尺寸过大。");
                bitmap.Freeze(); return bitmap;
            }
        }
        private static void ValidateHeader(byte[] bytes, out int width, out int height)
        {
            if (bytes.Length < 33 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71 || bytes[4] != 13 || bytes[5] != 10 || bytes[6] != 26 || bytes[7] != 10 || bytes[12] != 73 || bytes[13] != 72 || bytes[14] != 68 || bytes[15] != 82) throw new ArgumentException("图片内容无效。");
            long w = ((long)bytes[16] << 24) | ((long)bytes[17] << 16) | ((long)bytes[18] << 8) | bytes[19];
            long h = ((long)bytes[20] << 24) | ((long)bytes[21] << 16) | ((long)bytes[22] << 8) | bytes[23];
            if (w <= 0 || h <= 0 || w > 1600 || h > 1600) throw new ArgumentException("图片尺寸过大。");
            width = (int)w; height = (int)h;
        }
        public static byte[] Encode(BitmapSource source)
        {
            if (source == null || source.PixelWidth <= 0 || source.PixelHeight <= 0 || (long)source.PixelWidth * source.PixelHeight > 16000000) throw new ArgumentException("图片尺寸过大。");
            double scale = Math.Min(1, 1600.0 / Math.Max(source.PixelWidth, source.PixelHeight));
            BitmapSource resized = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(resized));
            using (var output = new MemoryStream()) { encoder.Save(output); if (output.Length > ClipboardHistoryBook.MaximumImageBytes) throw new ArgumentException("图片超过 1 MB，已略过。"); return output.ToArray(); }
        }
    }
    internal sealed class ClipboardHistoryPayload
    {
        public string Text, Source;
        public BitmapSource Image;
        public bool Protected;
    }
    internal interface IClipboardHistoryAdapter
    {
        ClipboardHistoryPayload Read();
        void Write(ClipboardHistoryItem item);
    }
    internal sealed class WindowsClipboardHistoryAdapter : IClipboardHistoryAdapter
    {
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        private readonly Func<string, bool> excluded;
        public WindowsClipboardHistoryAdapter(Func<string, bool> excludedApplication) { excluded = excludedApplication; }
        public ClipboardHistoryPayload Read()
        {
            uint sequence = GetClipboardSequenceNumber(); IntPtr owner = GetClipboardOwner();
            string source = ""; uint processId; GetWindowThreadProcessId(GetClipboardOwner(), out processId);
            if (processId > 0) try { using (var process = Process.GetProcessById((int)processId)) source = process.ProcessName; } catch (ArgumentException) { } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            if (excluded != null && excluded(source)) return new ClipboardHistoryPayload { Source = source, Protected = true };
            var data = Clipboard.GetDataObject(); if (data == null) return null;
            string[] formats = data.GetFormats(false);
            if (formats.Any(x => String.Equals(x, "ExcludeClipboardContentFromMonitorProcessing", StringComparison.OrdinalIgnoreCase)) || IsFalseFlag(data, formats, "CanIncludeInClipboardHistory")) return new ClipboardHistoryPayload { Source = source, Protected = true };
            var result = new ClipboardHistoryPayload { Source = source };
            if (data.GetDataPresent(DataFormats.UnicodeText, false)) result.Text = data.GetData(DataFormats.UnicodeText, false) as string;
            else if (Clipboard.ContainsImage()) { result.Image = Clipboard.GetImage(); if (result.Image != null && result.Image.CanFreeze) result.Image.Freeze(); }
            return sequence == GetClipboardSequenceNumber() && owner == GetClipboardOwner() ? result : new ClipboardHistoryPayload { Protected = true };
        }
        private static bool IsFalseFlag(IDataObject data, string[] formats, string key)
        {
            if (!formats.Contains(key)) return false;
            object value = data.GetData(key, false); if (value is int) return (int)value == 0; if (value is bool) return !(bool)value;
            var bytes = value as byte[]; if (bytes != null) return bytes.Length == 0 || bytes.All(x => x == 0);
            var stream = value as Stream;
            if (stream != null) { if (stream.CanSeek) stream.Position = 0; int next; for (int i = 0; i < 4 && (next = stream.ReadByte()) >= 0; i++) if (next != 0) return false; return true; }
            return true;
        }
        public void Write(ClipboardHistoryItem item) { if (item.IsImage) Clipboard.SetImage(ClipboardHistoryImage.Decode(item.Png)); else Clipboard.SetText(item.Text); }
    }
}


