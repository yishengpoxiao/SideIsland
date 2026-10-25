using System;
using System.Windows;
using System.Windows.Controls;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        private Button languageButton, themeButton;

        private void BuildAppearanceControls(Panel actions)
        {
            languageButton = Button("EN", ToggleLanguage, false);
            themeButton = Button("☼", ToggleTheme, false);
            foreach (var button in new[] { languageButton, themeButton })
            {
                button.Width = 32; button.Height = 32; button.Padding = new Thickness(0);
                button.Margin = new Thickness(0, 0, 6, 0); actions.Children.Add(button);
            }
            UiLocalization.ToolTip(languageButton, "切换为英文");
            UiLocalization.ToolTip(themeButton, "切换为亮色模式");
        }

        private void ToggleLanguage() { settings.Language = settings.Language == "en" ? "zh" : "en"; ApplyAppearance(); SaveSettings(); }
        private void ToggleTheme() { settings.Theme = settings.Theme == "light" ? "dark" : "light"; ApplyAppearance(); SaveSettings(); }

        private void ApplyAppearance()
        {
            StopPageTransition();
            // Replace old-language/theme content immediately, while the outline
            // keeps its current clock and clip. Committing its target here used
            // to turn a rapid appearance click into a visible size jump.
            DiscardTransitionSnapshot();
            AppAppearance.Configure(settings.Language, settings.Theme);
            RefreshAppearanceControls();
            FilterToolButtons(); RefreshToolPickerHeader(); RebuildFavorites();
            notificationCards.Clear(); UpdateNotificationList(); RefreshPeekText();
            UpdateDisplay();
            if (mediaPanel != null) mediaPanel.RefreshAppearance();
            RefreshNextFeatureAppearance();
            InvalidateVisual();
        }

        private void RefreshAppearanceControls()
        {
            if (languageButton != null)
            {
                languageButton.Content = settings.Language == "en" ? "中" : "EN";
                // Protected button contents stay explicit; tooltips are UI text.
                languageButton.ToolTip = settings.Language == "en" ? "Switch to Chinese" : "切换为英文";
                themeButton.Content = settings.Theme == "light" ? "☾" : "☼";
                themeButton.ToolTip = UiLocalization.Localize(settings.Theme == "light" ? "切换为暗色模式" : "切换为亮色模式");
            }
            if (tray != null && tray.ContextMenuStrip != null)
            {
                var menu = tray.ContextMenuStrip;
                foreach (System.Windows.Forms.ToolStripItem item in menu.Items)
                {
                    string source = item.Tag as string;
                    if (source != null) item.Text = UiLocalization.Localize(source);
                }
                TrayMenuAppearance.Apply(menu);
            }
        }
    }
}
