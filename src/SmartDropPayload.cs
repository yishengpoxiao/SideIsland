using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace CodexIsland
{
    internal enum SmartDropKind { None, Image, Files, Text }
    internal sealed class SmartDropPayload
    {
        internal SmartDropKind Kind;
        internal string[] Paths = new string[0];
        internal string Text = "";
    }

    internal static class SmartDropParser
    {
        internal const int MaximumFiles = 100, MaximumText = 120000;
        internal static bool Supports(IDataObject data)
        {
            try { return data != null && (data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text)); }
            catch { return false; }
        }
        internal static SmartDropPayload Read(IDataObject data)
        {
            var result = new SmartDropPayload();
            try
            {
                if (data == null) return result;
                if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    var paths = data.GetData(DataFormats.FileDrop) as string[];
                    if (paths == null || paths.Length == 0 || paths.Length > MaximumFiles || paths.Any(path => !FullyQualifiedPath(path))) return result;
                    result.Paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    string extension = result.Paths.Length == 1 ? Path.GetExtension(result.Paths[0]) : "";
                    result.Kind = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ? SmartDropKind.Image : SmartDropKind.Files;
                    return result;
                }
                string text = data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : data.GetDataPresent(DataFormats.Text) ? data.GetData(DataFormats.Text) as string : null;
                if (String.IsNullOrWhiteSpace(text) || text.Length > MaximumText || text.IndexOf('\0') >= 0) return result;
                result.Kind = SmartDropKind.Text; result.Text = text; return result;
            }
            catch { return new SmartDropPayload(); }
        }
        private static bool FullyQualifiedPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.Length > 32760 || path.IndexOf('\0') >= 0) return false;
            // C:folder and \folder depend on mutable process working directories.
            return path.StartsWith("\\\\", StringComparison.Ordinal) || (path.Length >= 3 && Char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'));
        }
    }
}
