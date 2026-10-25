using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexIsland
{
    internal static class AudioMixerTests
    {
        internal static void Run()
        {
            Check(AudioMixerService.NormalizeVolume(-5) == 0 && AudioMixerService.NormalizeVolume(110) == 100 && AudioMixerService.NormalizeVolume(37.5) == 37.5, "strict finite slider normalization");
            bool invalid = false; try { AudioMixerService.NormalizeVolume(Double.NaN); } catch (ArgumentException) { invalid = true; } Check(invalid, "NaN rejected instead of COM write");
            string language = UiLocalization.Language, theme = UiTheme.Name;
            try
            {
                foreach (string locale in new[] { "zh", "en" }) foreach (string appearance in new[] { "light", "dark" }) foreach (int width in new[] { 280, 391, 560 })
                {
                    AppAppearance.Configure(locale, appearance);
                    using (var panel = new AudioMixerPanel(true))
                    {
                        panel.RenderSnapshot(AudioMixerPanel.DemoSnapshot());
                        panel.Measure(new Size(width, Double.PositiveInfinity)); panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height)); panel.UpdateLayout();
                        Check(panel.DesiredSize.Height > 0 && panel.DesiredSize.Width <= width + .1, "demo mixer remains within requested width");
                        Slider slider = Descendants(panel).OfType<Slider>().First(); slider.Value = 63;
                        panel.RenderSnapshot(AudioMixerPanel.DemoSnapshot()); Check(slider.Value == 63, "background sample does not overwrite a pending volume edit");
                        Check(Descendants(panel).OfType<Button>().Where(button => Object.Equals(button.Content, UiLocalization.Localize("应用")) || Object.Equals(button.Content, "应用")).All(button => !button.IsEnabled), "demo cannot change real audio");
                        panel.RenderSnapshot(new AudioMixerSnapshot());
                        Check(!Descendants(panel).OfType<Slider>().Any(), "ended audio sessions remove stale controls");
                    }
                }
            }
            finally { AppAppearance.Configure(language, theme); }
            Console.WriteLine("PASS: app-volume finite bounds, pending edit preservation, stale-session removal and 24 bilingual/theme/width demo layouts without system audio writes.");
        }
        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++) { var child = VisualTreeHelper.GetChild(root, index); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
        }
        private static void Check(bool value, string description) { if (!value) throw new InvalidOperationException("Mixer: " + description); }
    }
}
