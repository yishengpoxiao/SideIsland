using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // Own demo panel only: never invokes OCR, screen capture or clipboard APIs.
    internal static class OcrPresentationTests
    {
        internal static void Run()
        {
            string language = UiLocalization.Language, theme = UiTheme.Name;
            OcrPanel panel = null; Window window = null;
            try
            {
                AppAppearance.Configure("zh", "dark"); panel = new OcrPanel(true);
                var languages = (ComboBox)typeof(OcrPanel).GetField("languages", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
                var copy = (Button)typeof(OcrPanel).GetField("copyButton", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
                var automatic = (OcrLanguageChoice)languages.SelectedItem;
                var model = new OcrLanguageChoice { Tag = "zh-Hans-CN", Name = "开始" }; languages.Items.Add(model);
                var named = new ComboBox { DisplayMemberPath = "Label", ItemsSource = new[] { new NamedChoice { Label = "Mapped player label" } }, SelectedIndex = 0, MinHeight = 30 };
                var body = new StackPanel(); body.Children.Add(named); body.Children.Add(panel);
                window = new Window { Width = 560, Height = 760, Content = new ScrollViewer { Content = body }, ShowActivated = false, ShowInTaskbar = false, IsHitTestVisible = false, Title = "OCR presentation fixture" };
                AppAppearance.Register(window); window.Show(); Layout(window);
                Check(VisibleText(languages).Contains("自动（Windows 首选语言）"), "Chinese automatic language is present in the actual selected-item visual");
                Check(VisibleText(named).Contains("Mapped player label") && named.DisplayMemberPath == "Label", "existing DisplayMemberPath renders its own label after appearance registration");
                Check((string)copy.Content == "复制文字", "Chinese copy label is shown");
                int changes = 0; languages.SelectionChanged += (s, e) => changes++;
                AppAppearance.Configure("en", "light"); Layout(window);
                Check(VisibleText(languages).Contains("Automatic (Windows preferred language)"), "shown automatic selection updates to English without rebuilding the panel");
                Check((string)copy.Content == "Copy text", "OCR copy action is translated to English");
                Check(ReferenceEquals(languages.SelectedItem, automatic) && changes == 0, "language switching preserves automatic selection identity");
                languages.SelectedItem = model; Layout(window);
                Check(VisibleText(languages).Contains("开始"), "installed language name remains verbatim even when it matches a UI translation key");
                AppAppearance.Configure("zh", "dark"); Layout(window); AppAppearance.Configure("en", "light"); Layout(window);
                Check(VisibleText(languages).Contains("开始") && ReferenceEquals(languages.SelectedItem, model) && changes == 1, "model selection and native display name survive Chinese-English round trips");
                Check(model.Tag == "zh-Hans-CN" && model.Name == "开始" && automatic.Tag == "" && automatic.Name == "自动（Windows 首选语言）", "presentation never rewrites OCR model values");
                languages.SelectedItem = automatic; Layout(window); AppAppearance.Configure("zh", "dark"); Layout(window);
                Check(VisibleText(languages).Contains("自动（Windows 首选语言）") && (string)copy.Content == "复制文字" && changes == 2, "selected-item visual and copy action return to Chinese after reselecting automatic mode");
                Check(VisibleText(named).Contains("Mapped player label"), "explicit display-member labels also survive appearance round trips");
                Console.WriteLine("PASS: OCR selected-item visual labels, live Chinese-English round trips, preserved language model identity, translated copy action and DisplayMemberPath rendering.");
            }
            finally { if (window != null) window.Close(); if (panel != null) panel.Dispose(); AppAppearance.Configure(language, theme); }
        }
        private sealed class NamedChoice { public string Label { get; set; } }
        private static IEnumerable<string> VisibleText(DependencyObject root)
        {
            var text = root as TextBlock; if (text != null && text.IsVisible) yield return text.Text;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (string value in VisibleText(VisualTreeHelper.GetChild(root, index))) yield return value;
        }
        private static void Layout(Window window)
        {
            window.UpdateLayout(); var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); window.UpdateLayout();
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception("OCR presentation: " + message); }
    }
}
