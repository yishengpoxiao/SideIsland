using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    // Reuses the all-pages fixture's demo HWND. Every track, lyric and pixel is
    // generated here; no player command, clipboard, capture or service is used.
    internal static class CompactMediaStateTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static void Run(IslandWindow window, Dictionary<string, UIElement> panels)
        {
            Stage("setup", "enter");
            var panel = (MediaPanel)panels["media"];
            Check(Read<bool>(panel, "demo"), "fixture must use isolated demo data");
            var settings = Read<Settings>(window, "settings");
            int originalWidth = settings.ExpandedWidth;
            string originalTool = Read<string>(window, "selectedTool"), originalLastTool = settings.LastTool;
            bool originalExpanded = Read<bool>(window, "expanded");
            string language = UiLocalization.Language, theme = UiTheme.Name;
            string[] fields = { "media", "lyricsTrack", "currentLyric", "hasLyrics", "lyricMatchReason", "lyricsDocument", "renderedLyricsDocument", "legacyLyricsCall", "preferredSourceId", "displayedPlayerOptions", "playerOptionsKey", "generation", "updatingPlayerSelection" };
            var saved = fields.ToDictionary(name => name, name => Read<object>(panel, name));
            var fullLyrics = Read<Expander>(panel, "fullLyrics"); bool expandedLyrics = fullLyrics.IsExpanded;
            var fullText = Read<TextBox>(panel, "fullLyricsText"); string fullTextValue = fullText == null ? null : fullText.Text;
            int fullSelection = fullText == null ? 0 : fullText.SelectionStart, fullSelectionLength = fullText == null ? 0 : fullText.SelectionLength;
            double fullFont = fullText == null ? 0 : fullText.FontSize, fullVertical = fullText == null ? 0 : fullText.VerticalOffset, fullHorizontal = fullText == null ? 0 : fullText.HorizontalOffset;
            var status = Read<TextBlock>(panel, "lyricsStatus"); string statusText = status.Text; Visibility statusVisibility = status.Visibility;
            var font = Read<TextBox>(panel, "lyricFontInput"); var offset = Read<TextBox>(panel, "lyricOffsetInput");
            string fontText = font.Text, offsetText = offset.Text; double fontSize = Read<TextBlock>(panel, "lyricsLine").FontSize;
            int externalActions = 0, scenarios = 0;
            Action<string> selected = value => externalActions++;
            Action<MediaSnapshot> imported = value => externalActions++;
            Action retried = () => externalActions++;
            panel.PlayerSelectionChanged += selected; panel.LyricsImportRequested += imported; panel.RetryLyricsRequested += retried;
            try
            {
                fullLyrics.IsExpanded = false;
                string title = "原文歌曲 Song title — " + String.Join(" ", Enumerable.Repeat("很长的歌名 Long track name", 9));
                string artist = "Original artist / 原文歌手 " + String.Join(" · ", Enumerable.Repeat("Long artist credits 合作歌手", 7));
                string lyric = String.Join(" ", Enumerable.Repeat("原文歌词保持完整 Long synchronized lyric line 😀", 12));
                var document = LrcDocument.Parse("[00:01]" + lyric + "\n[00:20]Second line 第二句");
                const string playerId = "Fixture.Player.With.Long.Application.Identity.For.Compact.Layout!Player";
                var players = new[] { new MediaPlayerOption { SourceId = playerId, Playing = true }, new MediaPlayerOption { SourceId = "Fixture.SecondPlayer", Playing = false } };
                Stage("setup", "before generated artwork");
                BitmapSource artwork = GeneratedArtwork();
                Stage("setup", "after generated artwork");
                panel.ConfigurePreferredPlayer(playerId);
                panel.ConfigureLyricDisplay(new MediaUiPreferences { LyricFontSize = 16, LyricOffsetMilliseconds = -10000 });
                Call(window, "SelectToolsPage"); Call(window, "SelectTool", "media");
                foreach (string testLanguage in new[] { "zh", "en" })
                foreach (string testTheme in new[] { "light", "dark" })
                foreach (int width in new[] { 320, 900 })
                {
                    string layoutContext = testLanguage + "/" + testTheme + "/" + width;
                    Stage(layoutContext, "before appearance and width");
                    AppAppearance.Configure(testLanguage, testTheme); settings.ExpandedWidth = width; window.SetExpanded(true, false);
                    Stage(layoutContext, "after appearance and width");
                    foreach (bool hasArtwork in new[] { true, false })
                    {
                        string context = layoutContext + "/art=" + hasArtwork;
                        Stage(context, "before media snapshot");
                        var track = new MediaSnapshot { Available = true, Playing = true, Title = title, Artist = artist, Source = playerId,
                            Duration = TimeSpan.FromMinutes(3), Position = TimeSpan.FromSeconds(10), SampledAtUtc = DateTime.UtcNow,
                            CanToggle = true, CanPrevious = true, CanNext = true, Players = players, Artwork = hasArtwork ? artwork : null,
                            PreferredSourceId = playerId, Status = "Fixture playback state" };
                        Stage(context, "before RenderMedia"); Call(panel, "RenderMedia", track); Stage(context, "after RenderMedia");
                        panel.ShowLyrics(track, lyric, true, "本地歌词已加载，按播放器进度显示", document); Stage(context, "after ShowLyrics");
                        panel.SetLyricsStatus(""); Stage(context, "before Settle"); Settle(window, context); Stage(context, "after Settle"); scenarios++;
                        
                        Stage(context, "before text and horizontal checks"); CheckHorizontalControls(panel, context);
                        CheckText(Read<TextBlock>(panel, "title"), title, context + ": original long title");
                        CheckText(Read<TextBlock>(panel, "lyricsLine"), lyric, context + ": original long lyric");
                        var artistLabel = Read<TextBlock>(panel, "artist");
                        Check(artistLabel.Text == artist && Convert.ToString(artistLabel.ToolTip) == artist, context + ": long artist retains a complete tooltip");
                        Check(status.Visibility == Visibility.Collapsed, context + ": empty lyric action status reserves no space");
                        Check(font.Text == "16" && offset.Text == "-10000", context + ": extreme valid lyric settings survive appearance changes");
                        Stage(context, "before input glyph checks"); CheckInput(font, context); CheckInput(offset, context); Stage(context, "after input glyph checks");
                        var cover = Read<Image>(panel, "cover"); var column = Read<ColumnDefinition>(panel, "artworkColumn");
                        if (hasArtwork)
                        {
                            Check(cover.IsVisible && Object.ReferenceEquals(cover.Source, artwork) && Math.Abs(column.ActualWidth - 74) < 1, context + ": artwork is displayed with its intended gutter");
                            Stage(context, "before cover RenderTargetBitmap.Render");
                            var bitmap = new RenderTargetBitmap(62, 62, 96, 96, PixelFormats.Pbgra32); bitmap.Render(cover);
                            Stage(context, "after cover RenderTargetBitmap.Render");
                            var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(31, 31, 1, 1), pixel, 4, 0); Stage(context, "after cover CopyPixels");
                            Check(pixel[0] == 191 && pixel[1] == 125 && pixel[2] == 50 && pixel[3] == 255, context + ": generated artwork pixels remain intact across themes");
                        }
                        else Check(cover.Visibility == Visibility.Collapsed && column.ActualWidth < .1, context + ": absent artwork leaves no vacant column");
                        var choice = Read<ComboBox>(panel, "playerSelection");
                        Check(choice.Items.Count == 3 && choice.SelectedItem != null && Convert.ToString(choice.SelectedItem.GetType().GetProperty("SourceId").GetValue(choice.SelectedItem, null)) == playerId,
                            context + ": a long player identity remains selected without raising player actions");
                        foreach (var caption in Descendants<TextBlock>(choice).Where(value => value.IsVisible && value.Text.Length > 0))
                        {
                            Point origin = caption.TranslatePoint(new Point(), choice);
                            bool clipped = origin.X < -1 || origin.X + caption.ActualWidth > choice.ActualWidth + 1 || caption.DesiredSize.Height > caption.ActualHeight + 1;
                            Check(!clipped || Convert.ToString(choice.ToolTip).Contains(playerId), context + ": selected player caption is cut without an accessible full label");
                        }
                    }
                }
                Check(externalActions == 0, "populating and changing appearance never invokes external player or lyric actions");
                Console.WriteLine("PASS: " + scenarios + " populated media layouts with long original song/artist/lyrics, real generated artwork pixels, absent-art column removal, long player selection and extreme valid lyric inputs across Chinese/English, light/dark and narrow/wide windows; no external actions.");
            }
            finally
            {
                panel.PlayerSelectionChanged -= selected; panel.LyricsImportRequested -= imported; panel.RetryLyricsRequested -= retried;
                AppAppearance.Configure(language, theme);
                foreach (var item in saved) panel.GetType().GetField(item.Key, Private).SetValue(panel, item.Value);
                // Repaint from the original references without emitting UI actions.
                panel.GetType().GetField("playerOptionsKey", Private).SetValue(panel, null);
                Call(panel, "RenderMedia", saved["media"]); panel.ShowPlayerSessions((MediaPlayerOption[])saved["displayedPlayerOptions"]);
                fullLyrics.IsExpanded = expandedLyrics; font.Text = fontText; offset.Text = offsetText; Read<TextBlock>(panel, "lyricsLine").FontSize = fontSize;
                if (fullText != null) { fullText.Text = fullTextValue; fullText.FontSize = fullFont; fullText.Select(fullSelection, fullSelectionLength); fullText.ScrollToVerticalOffset(fullVertical); fullText.ScrollToHorizontalOffset(fullHorizontal); }
                status.Text = statusText; status.Visibility = statusVisibility;
                settings.ExpandedWidth = originalWidth;
                if (!String.IsNullOrEmpty(originalTool)) Call(window, "SelectTool", originalTool);
                settings.LastTool = originalLastTool; window.SetExpanded(originalExpanded, false); Settle(window, "finally");
            }
        }
        private static void Stage(string context, string stage)
        {
            NativeLayoutCrashTrace.Mark("media", context, stage);
            if (NativeLayoutCrashTrace.Quiet) return;
            Console.WriteLine("CHECK: compact media " + context + " · " + stage); Console.Out.Flush();
        }

        private static BitmapSource GeneratedArtwork()
        {
            byte[] pixels = Enumerable.Range(0, 16).SelectMany(index => new byte[] { 191, 125, 50, 255 }).ToArray();
            var image = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16); image.Freeze(); return image;
        }
        private static void CheckText(TextBlock value, string expected, string context)
        {
            Check(value.IsVisible && value.Text == expected && Double.IsPositiveInfinity(value.MaxHeight), context + " is not translated or height-limited");
            Check(value.DesiredSize.Width - value.Margin.Left - value.Margin.Right <= value.ActualWidth + 1 && value.DesiredSize.Height - value.Margin.Top - value.Margin.Bottom <= value.ActualHeight + 1, context + " is fully measured without clipping");
        }
        private static void CheckInput(TextBox input, string context)
        {
            for (int index = 0; index < input.Text.Length; index++)
            {
                Rect glyph = input.GetRectFromCharacterIndex(index);
                Check(!glyph.IsEmpty && glyph.Left >= -1 && glyph.Right <= input.ActualWidth + 1 && glyph.Top >= -1 && glyph.Bottom <= input.ActualHeight + 1,
                    context + ": lyric input glyph is cropped at index " + index + " (" + glyph + ")");
            }
        }
        private static void CheckHorizontalControls(FrameworkElement panel, string context)
        {
            foreach (var control in Descendants<Control>(panel).Where(value => value.IsVisible && (value is Button || value is TextBox || value is ComboBox || value is CheckBox)))
            {
                Point origin = control.TranslatePoint(new Point(), panel);
                Check(origin.X >= -1 && origin.X + control.ActualWidth <= panel.ActualWidth + 1, context + ": control outside page " + control.GetType().Name);
                var button = control as Button;
                if (button != null) foreach (var caption in Descendants<TextBlock>(button).Where(value => value.IsVisible))
                    Check(caption.DesiredSize.Width <= caption.ActualWidth + 1 && caption.DesiredSize.Height <= caption.ActualHeight + 1, context + ": cropped button caption " + button.Content);
            }
        }
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            { var child = VisualTreeHelper.GetChild(root, index); if (child is T) yield return (T)child; foreach (var nested in Descendants<T>(child)) yield return nested; }
        }
        private static T Read<T>(object target, string field) { return (T)target.GetType().GetField(field, Private).GetValue(target); }
        private static object Call(object target, string method, params object[] args)
        { try { return target.GetType().GetMethod(method, Private).Invoke(target, args); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static void Settle(IslandWindow window, string context = "unspecified")
        {
            // SelectTool/SetExpanded can restart the demo music UI timer. Stop
            // it before yielding so it cannot replace this fixture's lyrics.
            foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop();
            for (int pass = 0; pass < 2; pass++)
            {
                NativeLayoutCrashTrace.Mark("media.settle", context, "layout.before pass=" + pass);
                window.UpdateLayout();
                NativeLayoutCrashTrace.Mark("media.settle", context, "pump.before pass=" + pass);
                var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
                NativeLayoutCrashTrace.Mark("media.settle", context, "pump.after pass=" + pass);
                foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop();
            }
            NativeLayoutCrashTrace.Mark("media.settle", context, "final-layout.before"); window.UpdateLayout();
            NativeLayoutCrashTrace.Mark("media.settle", context, "final-layout.after");
        }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception("Compact populated media: " + message); }
    }
}
