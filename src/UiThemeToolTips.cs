using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CodexIsland
{
    internal static partial class UiTheme
    {
        private static bool toolTipSupportInstalled;
        private static DataTemplate toolTipTextTemplate;

        private static void InstallToolTipSupport()
        {
            if (toolTipSupportInstalled) return;
            toolTipSupportInstalled = true;
            EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent,
                new RoutedEventHandler((s, e) => PrepareToolTip((ToolTip)s)));
        }

        internal static void PrepareToolTip(ToolTip tip)
        {
            if (tip == null) return;
            Install(tip.Resources);
            Size available = MenuWorkArea(tip.PlacementTarget as FrameworkElement ?? tip);
            tip.MaxWidth = available.Width;
            tip.MaxHeight = System.Math.Min(280, available.Height);
            if (ReferenceEquals(tip.ContentTemplate, toolTipTextTemplate) && (!(tip.Content is string) || tip.ContentTemplateSelector != null))
                tip.ClearValue(ContentControl.ContentTemplateProperty);
            // Preserve custom visual tips and explicit templates. String tips
            // get a real TextBlock: TextWrapping is not an attached property.
            if (tip.Content is string && tip.ContentTemplate == null && tip.ContentTemplateSelector == null)
            {
                if (toolTipTextTemplate == null)
                {
                    var text = new FrameworkElementFactory(typeof(TextBlock));
                    text.SetBinding(TextBlock.TextProperty, new Binding());
                    text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
                    text.SetValue(TextBlock.LineHeightProperty, 16.0);
                    toolTipTextTemplate = new DataTemplate { VisualTree = text };
                }
                tip.ContentTemplate = toolTipTextTemplate;
            }
        }
    }
}
