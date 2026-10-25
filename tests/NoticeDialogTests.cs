using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // All native handles belong to these fixtures. Button routing and modal
    // ownership are checked without sending keys or mouse input to the desktop.
    internal static class NoticeDialogTests
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowEnabled(IntPtr window);

        internal static void Run()
        {
            RunLayout(); RunLifecycle();
            Console.WriteLine("PASS: compact notice raw Unicode/exception preservation, narrow scrollable content and fixed actions; default/cancel flags, routed OK close, real owned modal disable/restore and native owner handles.");
        }
        internal static void RunLayout()
        {
            string language = UiLocalization.Language, theme = UiTheme.Name; int scenarios = 0;
            string raw = "保存设置\nSystem.IO.IOException: C:\\Fixture\\保存设置\\original file.txt\n" + String.Join("\n", Enumerable.Range(0, 45).Select(index => index + ": 原始异常详情 / original exception text / \"保存设置\" / 😀"));
            try
            {
                foreach (string testLanguage in new[] { "zh", "en" })
                foreach (string testTheme in new[] { "light", "dark" })
                {
                    AppAppearance.Configure(testLanguage, testTheme);
                    foreach (MessageBoxImage severity in new[] { MessageBoxImage.Information, MessageBoxImage.Warning, MessageBoxImage.Error })
                    {
                        var window = new NoticeWindow(raw, "原始标题 / Original title", severity);
                        try
                        {
                            var frame = (Border)window.Content; var grid = (Grid)frame.Child;
                            foreach (int width in new[] { 280, 320, 420 })
                            foreach (int height in new[] { 200, 280, 440 })
                            {
                                frame.Measure(new Size(width, height)); frame.Arrange(new Rect(0, 0, width, height)); frame.UpdateLayout();
                                string context = testLanguage + "/" + testTheme + "/" + severity + "/" + width + "x" + height;
                                Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, context + ": layout inspection created an HWND");
                                Check(window.Message.Text == raw && window.Heading.Text == "原始标题 / Original title" && UiLocalization.IsProtected(window.Message) && UiLocalization.IsProtected(window.Heading), context + ": raw title or exception details were translated");
                                Check(window.Message.TextWrapping == TextWrapping.Wrap && window.Heading.TextWrapping == TextWrapping.Wrap && window.Message.LineHeight == 18, context + ": notice text no longer wraps consistently");
                                Check(window.MessageScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && window.MessageScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled, context + ": notice body lost vertical-only scroll");
                                Check(window.MessageScroll.ScrollableHeight > 0 && window.MessageScroll.ViewportHeight > 0, context + ": long content cannot scroll in the visible body");
                                var footer = (Grid)VisualTreeHelper.GetParent(window.Accept);
                                Check(Grid.GetRow(footer) == 2 && Object.ReferenceEquals(VisualTreeHelper.GetParent(footer), grid), context + ": accept action moved inside the scrolling content");
                                Point actionTop = window.Accept.TranslatePoint(new Point(0, 0), frame), actionBottom = window.Accept.TranslatePoint(new Point(window.Accept.ActualWidth, window.Accept.ActualHeight), frame);
                                Check(actionTop.Y >= 0 && actionBottom.Y <= frame.ActualHeight + 1 && actionBottom.X <= frame.ActualWidth + 1 && window.Accept.ActualHeight >= 32, context + ": accept action is clipped or unreachable");
                                Check(window.Accept.IsDefault && window.Accept.IsCancel, context + ": Enter/Escape no longer route through the dialog's OK button");
                                Check(frame.CornerRadius == new CornerRadius(16) && frame.Padding == new Thickness(16), context + ": notice chrome spacing differs");
                                scenarios++;
                            }
                        }
                        finally { window.Close(); }
                    }
                }
                Console.WriteLine("PASS: " + scenarios + " notice layouts without HWNDs across Chinese/English, light/dark, three severity types, 280/320/420 DIP widths and three client heights; raw text, body scrolling, fixed OK and keyboard action flags.");
            }
            finally { AppAppearance.Configure(language, theme); }
        }
        internal static void RunLifecycle()
        {
            var owner = new Window { Title = "Notice ownership fixture", Width = 320, Height = 220, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            var unopened = new Window(); AppAppearance.Register(owner);
            try
            {
                Check(NativeDialogs.Owner(null) == null && NativeDialogs.Owner(unopened) == null, "native ownership fabricated an unopened window handle");
                owner.Show(); owner.UpdateLayout(); Pump(); IntPtr ownerHandle = new WindowInteropHelper(owner).Handle;
                Check(ownerHandle != IntPtr.Zero && IsWindowEnabled(ownerHandle), "own fixture owner did not start enabled");
                Check(NativeDialogs.Owner(owner) != null && NativeDialogs.Owner(owner).Handle == ownerHandle, "native dialogs do not receive the exact owning HWND");
                for (int pass = 0; pass < 2; pass++)
                {
                    var notice = new NoticeWindow("自己的提示框 / own notice", "Fixture notice", MessageBoxImage.Information, owner) { ShowActivated = false };
                    bool modalObserved = false, clickObserved = false, closed = false, timedOut = false; Exception callbackError = null;
                    notice.Accept.Click += (s, e) => clickObserved = true; notice.Closed += (s, e) => closed = true;
                    var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    watchdog.Tick += (s, e) => { timedOut = true; watchdog.Stop(); notice.Close(); };
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                    {
                        try
                        {
                            Check(notice.IsVisible && Object.ReferenceEquals(notice.Owner, owner), "notice did not show as the supplied owner's modal");
                            Check(!IsWindowEnabled(ownerHandle), "modal notice left its native owner interactive");
                            Check(NativeDialogs.Owner(notice) != null && NativeDialogs.Owner(notice).Handle == new WindowInteropHelper(notice).Handle, "a picker owned by the notice would receive the wrong HWND");
                            Check(notice.Accept.IsDefault && notice.Accept.IsCancel, "default/cancel keyboard behavior changed after Loaded");
                            modalObserved = true; notice.Accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, notice.Accept));
                        }
                        catch (Exception failure) { callbackError = failure; notice.Close(); }
                    }));
                    try { watchdog.Start(); notice.ShowDialog(); }
                    finally { watchdog.Stop(); notice.Close(); }
                    if (callbackError != null) throw callbackError;
                    Check(!timedOut && modalObserved && clickObserved && closed && !notice.IsVisible, "own routed OK did not terminate the modal promptly");
                    Check(owner.IsVisible && IsWindowEnabled(ownerHandle), "closing a notice failed to restore the owner's native input state");
                }
            }
            finally { unopened.Close(); owner.Close(); Pump(); }
            Console.WriteLine("PASS: two real owned notice modal lifecycles; local routed OK closes, Enter/Escape default/cancel flags remain set, own owner HWND disables and restores without global input.");
        }
        private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Notice dialog: " + message); }
    }
}
