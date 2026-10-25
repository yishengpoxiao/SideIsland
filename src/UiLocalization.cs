using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CodexIsland
{
    // Only explicitly registered UI properties are translated. TextBox.Text,
    // documents, images, notifications, file names and other user data are never
    // traversed or rewritten by the localization service.
    internal static partial class UiLocalization
    {
        private sealed class Entry
        {
            internal DependencyObject Target;
            internal DependencyProperty Property;
            internal string Source;
            internal bool Updating, Attached;
            internal DependencyPropertyDescriptor Descriptor;
            internal EventHandler Handler;
            internal void Attach()
            {
                if (Attached) return; Attached = true;
                Descriptor.AddValueChanged(Target, Handler);
                string current = Target.GetValue(Property) as string;
                if (current != Translate(Source)) Source = Canonical(current);
                Refresh();
            }
            internal void Detach() { if (!Attached) return; Descriptor.RemoveValueChanged(Target, Handler); Attached = false; }
            internal void Refresh()
            {
                if (IsProtected(Target)) return;
                string value = Translate(Source);
                if (Object.Equals(Target.GetValue(Property), value)) return;
                try { Updating = true; Target.SetCurrentValue(Property, value); }
                finally { Updating = false; }
            }
        }
        private sealed class Template
        {
            internal Regex Pattern;
            internal Regex Reverse;
            internal string English;
            internal string Chinese;
        }
        private static readonly Dictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Chinese = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly List<Template> Templates = new List<Template>();
        private static readonly List<WeakReference> Entries = new List<WeakReference>();
        private static readonly DependencyProperty RegistrationsProperty = DependencyProperty.RegisterAttached("Registrations", typeof(Dictionary<DependencyProperty, Entry>), typeof(UiLocalization));
        private static readonly DependencyProperty ProtectedProperty = DependencyProperty.RegisterAttached("Protected", typeof(bool), typeof(UiLocalization), new PropertyMetadata(false));
        private static string language = "zh";
        private sealed class LanguageState : DependencyObject
        {
            internal static readonly DependencyProperty VersionProperty = DependencyProperty.Register("Version", typeof(int), typeof(LanguageState), new PropertyMetadata(0));
            internal int Version { get { return (int)GetValue(VersionProperty); } set { SetValue(VersionProperty, value); } }
        }
        private sealed class ComboConverter : IMultiValueConverter
        {
            public object Convert(object[] values, Type type, object parameter, CultureInfo culture)
            {
                if (values.Length == 0) return "";
                object item = values[0];
                if (item is string) return Localize((string)item);
                // These are the application's immutable built-in units, not a
                // user's process/resource model. Keep the original item selected.
                if (ConversionTools.Categories.Any(category => Object.ReferenceEquals(category, item) || category.Units.Any(unit => Object.ReferenceEquals(unit, item)))) return Localize(item.ToString());
                return item;
            }
            public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) { throw new NotSupportedException(); }
        }
        private static readonly LanguageState State = new LanguageState();
        static UiLocalization() { BuildCatalog(); }
        internal static string Language { get { return language; } }
        internal static bool IsEnglish { get { return language == "en"; } }
        internal static CultureInfo Culture { get { return CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "zh-CN"); } }
        internal static int CatalogCount { get { return English.Count; } }
        internal static bool HasTranslation(string value) { return English.ContainsKey(value ?? ""); }
        internal static void Configure(string value)
        {
            language = String.Equals(value, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
            State.Version++;
            for (int i = Entries.Count - 1; i >= 0; i--) { var entry = Entries[i].Target as Entry; if (entry == null) Entries.RemoveAt(i); else entry.Refresh(); }
        }
        internal static T Protect<T>(T target) where T : DependencyObject
        {
            target.SetValue(ProtectedProperty, true);
            var registrations = target.GetValue(RegistrationsProperty) as Dictionary<DependencyProperty, Entry>;
            if (registrations != null) foreach (var entry in registrations.Values) { entry.Detach(); entry.Updating = true; try { target.SetCurrentValue(entry.Property, entry.Source); } finally { entry.Updating = false; } }
            return target;
        }
        internal static bool IsProtected(DependencyObject target) { return (bool)target.GetValue(ProtectedProperty); }
        internal static T Mark<T>(T target, DependencyProperty property, string source) where T : DependencyObject
        {
            if (target == null || IsProtected(target)) return target;
            var registrations = target.GetValue(RegistrationsProperty) as Dictionary<DependencyProperty, Entry>;
            if (registrations == null) { registrations = new Dictionary<DependencyProperty, Entry>(); target.SetValue(RegistrationsProperty, registrations); }
            Entry entry;
            if (registrations.TryGetValue(property, out entry)) { entry.Source = Canonical(source); entry.Refresh(); return target; }
            entry = new Entry { Target = target, Property = property, Source = Canonical(source), Descriptor = DependencyPropertyDescriptor.FromProperty(property, target.GetType()) };
            if (entry.Descriptor == null) return target;
            entry.Handler = (s, e) => { if (entry.Updating || IsProtected(target)) return; entry.Source = Canonical(target.GetValue(property) as string); entry.Refresh(); };
            registrations[property] = entry; Entries.Add(new WeakReference(entry));
            var element = target as FrameworkElement;
            if (element != null) { element.Loaded += (s, e) => { if (!IsProtected(target)) entry.Attach(); }; element.Unloaded += (s, e) => entry.Detach(); if (element.IsLoaded) entry.Attach(); }
            else entry.Attach();
            entry.Refresh(); return target;
        }
        internal static TextBlock Text(TextBlock target, string source) { return Mark(target, TextBlock.TextProperty, source); }
        internal static T Content<T>(T target, string source) where T : ContentControl { return Mark(target, ContentControl.ContentProperty, source); }
        internal static T Header<T>(T target, string source) where T : HeaderedItemsControl { return Mark(target, HeaderedItemsControl.HeaderProperty, source); }
        internal static T ToolTip<T>(T target, string source) where T : FrameworkElement { return Mark(target, FrameworkElement.ToolTipProperty, source); }
        internal static ComboBox Combo(ComboBox target)
        {
            if (target == null || target.ItemTemplate != null || !String.IsNullOrEmpty(target.DisplayMemberPath) || target.IsEditable || IsProtected(target)) return target;
            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            var binding = new MultiBinding { Converter = new ComboConverter() };
            binding.Bindings.Add(new Binding()); binding.Bindings.Add(new Binding("Version") { Source = State });
            label.SetBinding(TextBlock.TextProperty, binding);
            target.ItemTemplate = new DataTemplate { VisualTree = label }; return target;
        }
        internal static string Localize(string value) { return Translate(value); }
        internal static string Format(string value, params object[] args) { return String.Format(Culture, Translate(value), args); }
        private static string Canonical(string value)
        {
            if (String.IsNullOrEmpty(value)) return value ?? "";
            if (English.ContainsKey(value)) return value;
            string result; if (Chinese.TryGetValue(value, out result)) return result;
            foreach (var template in Templates) { Match match = template.Reverse.Match(value); if (match.Success) return Expand(template.Chinese, match); }
            if (value.IndexOf(" · ", StringComparison.Ordinal) >= 0) return String.Join(" · ", value.Split(new[] { " · " }, StringSplitOptions.None).Select(CanonicalSegment));
            return value;
        }
        private static string CanonicalSegment(string value)
        {
            if (English.ContainsKey(value)) return value;
            string result; if (Chinese.TryGetValue(value, out result)) return result;
            foreach (var template in Templates) { Match match = template.Reverse.Match(value); if (match.Success) return Expand(template.Chinese, match); }
            return value;
        }
        private static string Expand(string value, Match match) { return Regex.Replace(value, @"\{(\d+)\}", m => match.Groups["p" + m.Groups[1].Value].Value); }
        private static string Translate(string value)
        {
            if (String.IsNullOrEmpty(value) || !IsEnglish) return value ?? "";
            string translated;
            if (English.TryGetValue(value, out translated)) return translated;
            foreach (var template in Templates)
            {
                Match match = template.Pattern.Match(value);
                if (match.Success) return Expand(template.English, match);
            }
            // Separators are used by app-authored status summaries. Each complete
            // segment still requires an exact catalog entry or anchored template.
            if (value.IndexOf(" · ", StringComparison.Ordinal) >= 0) return String.Join(" · ", value.Split(new[] { " · " }, StringSplitOptions.None).Select(TranslateSegment));
            return value;
        }
        private static string TranslateSegment(string value)
        {
            string translated; if (English.TryGetValue(value, out translated)) return translated;
            foreach (var template in Templates) { Match match = template.Pattern.Match(value); if (match.Success) return Expand(template.English, match); }
            return value;
        }
        private static void Add(string source, string translated)
        {
            English[source] = translated; if (!Chinese.ContainsKey(translated)) Chinese.Add(translated, source);
            if (source.StartsWith(" · ", StringComparison.Ordinal) && translated.StartsWith(" · ", StringComparison.Ordinal))
            {
                string segment = source.Substring(3), englishSegment = translated.Substring(3);
                English[segment] = englishSegment; if (!Chinese.ContainsKey(englishSegment)) Chinese.Add(englishSegment, segment);
            }
        }
        private static void Map(string lines)
        {
            foreach (string line in lines.Replace("\r", "").Split('\n')) { if (line.Length == 0) continue; int split = line.IndexOf('|'); if (split <= 0) continue; Add(line.Substring(0, split).Replace("\\n", "\n"), line.Substring(split + 1).Replace("\\n", "\n")); }
        }
        private static void Pattern(string source, string english)
        {
            Templates.Add(new Template { Pattern = TemplateRegex(source), Reverse = TemplateRegex(english), English = english, Chinese = source });
            string[] originalSegments = source.Split(new[] { " · " }, StringSplitOptions.None), translatedSegments = english.Split(new[] { " · " }, StringSplitOptions.None);
            if (originalSegments.Length == translatedSegments.Length && originalSegments.Length > 1)
                for (int index = 0; index < originalSegments.Length; index++)
                {
                    // A segment containing only a placeholder is user data. It
                    // must never become a catch-all forward/reverse template.
                    if (Regex.Replace(originalSegments[index], @"\{\d+\}", "").Trim().Length == 0 || Regex.Replace(translatedSegments[index], @"\{\d+\}", "").Trim().Length == 0) continue;
                    Templates.Add(new Template { Pattern = TemplateRegex(originalSegments[index]), Reverse = TemplateRegex(translatedSegments[index]), English = translatedSegments[index], Chinese = originalSegments[index] });
                }
        }
        private static Regex TemplateRegex(string source)
        {
            var regex = new System.Text.StringBuilder("\\A"); int start = 0;
            foreach (Match parameter in Regex.Matches(source, @"\{(\d+)\}")) { regex.Append(Regex.Escape(source.Substring(start, parameter.Index - start))); regex.Append("(?<p" + parameter.Groups[1].Value + ">.+?)"); start = parameter.Index + parameter.Length; }
            regex.Append(Regex.Escape(source.Substring(start))).Append("\\z");
            return new Regex(regex.ToString(), RegexOptions.CultureInvariant);
        }
    }
}
