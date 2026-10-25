using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class MediaSessionSelectionTests
    {
        internal static void Run()
        {
            RunPure(); PreferenceRoundtrip(); SelectorVisualLabels();
            Console.WriteLine("PASS: exact Windows source-app media selection, automatic mode, missing selected player, command identity, list deduplication and stale-read invalidation (fake endpoints only).");
        }
        internal static void RunPure()
        {
            CurrentSourceAcrossWrappers().GetAwaiter().GetResult();
            SelectionAndControlIdentity().GetAwaiter().GetResult(); SelectionChangeDuringRead().GetAwaiter().GetResult();
        }
        private static async Task CurrentSourceAcrossWrappers()
        {
            const string appleId = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";
            const string qqId = "QQMusic.exe";
            Check(!Object.Equals(new object(), new object()) && MediaSessionSelection.IsCurrentSource(new string(appleId.ToCharArray()), appleId), "Different WinRT wrappers retain their exact current app identity");
            Check(!MediaSessionSelection.IsCurrentSource(qqId, appleId), "A different playing app does not become the system current app");
            Check(!MediaSessionSelection.IsCurrentSource(appleId.ToLowerInvariant(), appleId), "Current source matching is ordinal and case sensitive");
            Check(!MediaSessionSelection.IsCurrentSource(null, null) && !MediaSessionSelection.IsCurrentSource("", "") && !MediaSessionSelection.IsCurrentSource(" ", " "), "Missing or whitespace identities never mark unrelated sessions current");
            Check(!MediaSessionSelection.IsCurrentSource(appleId, null) && !MediaSessionSelection.IsCurrentSource(null, appleId) && !MediaSessionSelection.IsCurrentSource(appleId + ".Other", appleId), "Unknown identities and prefixes cannot match the current app");
            string controlled = "";
            var qq = Endpoint(qqId, true, MediaSessionSelection.IsCurrentSource(qqId, appleId), id => controlled = id);
            var apple = Endpoint(appleId, true, MediaSessionSelection.IsCurrentSource(appleId, appleId), id => controlled = id);
            var duplicateApple = Endpoint(appleId, false, MediaSessionSelection.IsCurrentSource(appleId, appleId), id => controlled = id);
            var collection = new[] { qq, apple, duplicateApple };
            Check(apple.Current && duplicateApple.Current && MediaSessionSelection.Select(collection, "") == apple, "Same-app sessions share the current flag while automatic mode prefers its playing session over an earlier playing app");
            using (var service = new MediaService(token => Task.FromResult(collection)))
            {
                var automatic = await service.ReadAsync().ConfigureAwait(false);
                Check(automatic.Source == appleId, "Automatic production selection follows the exact current app when another app is also playing");
                await service.SendAsync("toggle").ConfigureAwait(false);
                Check(controlled == appleId, "Automatic control retains the metadata's current-app target");
                service.PreferredSourceId = qqId;
                var locked = await service.ReadAsync().ConfigureAwait(false);
                Check(locked.Source == qqId, "An explicit exact player lock remains stronger than the current-app fallback");
                await service.SendAsync("next").ConfigureAwait(false);
                Check(controlled == qqId, "Explicitly locked controls cannot switch to the system current app");
            }
        }
        private static void SelectorVisualLabels()
        {
            string language = UiLocalization.Language, theme = UiTheme.Name; Window window = null;
            var panel = new MediaPanel(true); int selectionChanges = 0;
            panel.PlayerSelectionChanged += id => selectionChanges++;
            const string playerId = "Synthetic.Music!Player";
            try
            {
                AppAppearance.Configure("zh", "dark");
                panel.ShowPlayerSessions(new[] { new MediaPlayerOption { SourceId = playerId, Playing = true }, new MediaPlayerOption { SourceId = "Other.Synthetic", Playing = false } });
                window = new Window { Width = 430, Height = 720, Content = panel, ShowActivated = false, ShowInTaskbar = false, IsHitTestVisible = false, Topmost = false };
                AppAppearance.Register(window); window.Show(); Settle(window);
                var selection = (ComboBox)typeof(MediaPanel).GetField("playerSelection", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
                Check(SelectedVisibleText(selection).Contains("自动选择"), "Shown default player selection contains actual visible Chinese text after AppAppearance registration");
                selection.SelectedIndex = 1; Settle(window);
                Check(selectionChanges == 1 && SelectedSource(selection) == playerId, "Selecting a synthetic source emits exactly one selection change");
                foreach (string locale in new[] { "en", "zh", "en", "zh" })
                {
                    AppAppearance.Configure(locale, locale == "en" ? "light" : "dark"); panel.RefreshAppearance(); Settle(window);
                    string actual = SelectedVisibleText(selection);
                    Check(actual.Contains(playerId) && actual.Contains(locale == "en" ? "Playing" : "播放中"), "Shown selection renders the source ID and translated playing label in " + locale);
                    Check(SelectedSource(selection) == playerId && selectionChanges == 1, "Locale/theme refresh preserves the selected source and does not emit control/selection actions");
                    Check(selection.Items.Count == 3, "Refreshing appearance preserves the independent monitor's current player list");
                }
                panel.ConfigurePreferredPlayer("Missing.Synthetic!Player"); AppAppearance.Configure("en", "light"); panel.RefreshAppearance(); Settle(window);
                Check(SelectedVisibleText(selection).Contains("Missing.Synthetic!Player") && SelectedVisibleText(selection).Contains("No session available"), "Missing selected player remains visibly named instead of becoming blank");
                Check(SelectedSource(selection) == "Missing.Synthetic!Player" && selectionChanges == 1, "Showing a missing selection never changes the configured source");
                panel.ConfigurePreferredPlayer(""); panel.RefreshAppearance(); Settle(window);
                Check(SelectedVisibleText(selection).Contains("Automatic") && SelectedSource(selection) == "" && selectionChanges == 1, "Returning to automatic mode renders its actual English label without sending a media action");
            }
            finally { panel.Dispose(); if (window != null) window.Close(); AppAppearance.Configure(language, theme); }
        }
        private static string SelectedSource(ComboBox combo)
        { return (string)combo.SelectedItem.GetType().GetProperty("SourceId").GetValue(combo.SelectedItem, null); }
        private static string SelectedVisibleText(ComboBox combo)
        {
            // Inspect instantiated visual text, not Label/SelectedItem alone: an
            // overriding ItemTemplate can otherwise leave a correct model blank.
            return String.Join("\n", VisualDescendants<TextBlock>(combo).Where(item => item.IsVisible && item.ActualWidth > 0 && item.ActualHeight > 0).Select(item => item.Text));
        }
        private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index); var match = child as T; if (match != null) yield return match;
                foreach (var nested in VisualDescendants<T>(child)) yield return nested;
            }
        }
        private static void Settle(Window window)
        {
            window.UpdateLayout(); var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); window.UpdateLayout();
        }
        private static void PreferenceRoundtrip()
        {
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.MediaPlayerPreference." + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "music.json"); var store = new MediaUiPreferencesStore(false, path);
                store.Save(new MediaUiPreferences { PreferredSourceId = "Synthetic.Player!App", LyricFontSize = 15, LyricOffsetMilliseconds = -1250 });
                var loaded = new MediaUiPreferencesStore(false, path).Load();
                Check(loaded.PreferredSourceId == "Synthetic.Player!App" && loaded.LyricFontSize == 15 && loaded.LyricOffsetMilliseconds == -1250, "Player lock persists across restarts together with lyric preferences");
                loaded.LyricFontSize = 11; store.Save(loaded); Check(store.Load().PreferredSourceId == "Synthetic.Player!App", "Updating lyric settings preserves the player lock");
                loaded.PreferredSourceId = ""; store.Save(loaded); Check(store.Load().PreferredSourceId == "", "Returning to automatic selection also persists");
            }
            finally { Directory.Delete(folder, true); }
        }
        private static MediaSessionEndpoint Endpoint(string id, bool playing, bool current, Action<string> onSend)
        {
            return new MediaSessionEndpoint { SourceId = id, Playing = playing, Current = current,
                Read = token => Task.FromResult(new MediaSnapshot { Available = true, Playing = playing, Title = "Same title in multiple apps", Source = "untrusted title-source", CanToggle = true }),
                Send = (command, token) => { onSend(id); return Task.FromResult("sent"); } };
        }
        private static async Task SelectionAndControlIdentity()
        {
            string controlled = ""; var qq = Endpoint("QQMusic.exe", false, false, id => controlled = id);
            var browser = Endpoint("Browser.App!session", true, true, id => controlled = id);
            var collection = new[] { browser, qq, Endpoint("QQMusic.exe", true, false, id => controlled = id) };
            Check(MediaSessionSelection.Select(collection, "QQMusic.exe").SourceId == "QQMusic.exe", "App identity, rather than equal song titles, selects the player");
            Check(MediaSessionSelection.Options(collection).Length == 2 && MediaSessionSelection.Options(collection).Single(item => item.SourceId == "QQMusic.exe").Playing, "Multiple sessions from the same app form one player option");
            using (var service = new MediaService(token => Task.FromResult(collection)))
            {
                service.PreferredSourceId = "QQMusic.exe"; var result = await service.ReadAsync().ConfigureAwait(false);
                Check(result.Available && result.Source == "QQMusic.exe" && result.Players.Length == 2, "Selected endpoint's official source identity is attached to the snapshot");
                await service.SendAsync("toggle").ConfigureAwait(false); Check(controlled == "QQMusic.exe", "Playback control targets exactly the endpoint selected for metadata");
                service.PreferredSourceId = "NetEaseMissing"; controlled = "";
                await service.SendAsync("toggle").ConfigureAwait(false); Check(controlled == "", "Changing the selection immediately clears the old control target");
                var absent = await service.ReadAsync().ConfigureAwait(false); Check(!absent.Available && absent.Status.Contains("等待"), "Missing selected app stays absent with an explicit waiting status");
                await service.SendAsync("next").ConfigureAwait(false); Check(controlled == "", "Missing app cannot fall back to a playing browser");
                service.PreferredSourceId = ""; var automatic = await service.ReadAsync().ConfigureAwait(false);
                Check(automatic.Source == browser.SourceId, "Automatic mode follows the current playing system session");
                await service.SendAsync("toggle").ConfigureAwait(false); Check(controlled == browser.SourceId, "Automatic controls use the same chosen system session");
            }
            var preferences = new MediaUiPreferences { PreferredSourceId = "QQMusic.exe", LyricFontSize = 14 }; var copy = preferences.Copy(); copy.Validate();
            Check(copy.PreferredSourceId == "QQMusic.exe" && copy.LyricFontSize == 14, "Preference copying preserves the chosen app independently of lyric display settings");
        }
        private static async Task SelectionChangeDuringRead()
        {
            var waiting = new TaskCompletionSource<MediaSnapshot>(); string controlled = "";
            var old = Endpoint("Old.Player", true, true, id => controlled = id); old.Read = token => waiting.Task;
            var fresh = Endpoint("New.Player", false, false, id => controlled = id);
            using (var service = new MediaService(token => Task.FromResult(new[] { old, fresh })))
            {
                service.PreferredSourceId = old.SourceId; var stale = service.ReadAsync();
                service.PreferredSourceId = fresh.SourceId;
                var accepted = await service.ReadAsync().ConfigureAwait(false); Check(accepted.Source == fresh.SourceId, "New player can finish while an old player is slow");
                waiting.SetResult(new MediaSnapshot { Available = true, Title = "Late old track", Source = old.SourceId });
                var rejected = await stale.ConfigureAwait(false); Check(!rejected.Available, "An old asynchronous read cannot restore the prior selection");
                await service.SendAsync("toggle").ConfigureAwait(false); Check(controlled == fresh.SourceId, "Stale completion cannot replace the new player's control target");
            }
        }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception("Media sessions: " + message); }
    }
}
