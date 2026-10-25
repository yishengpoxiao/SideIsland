using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace CodexIsland
{
    internal static partial class UiTheme
    {
        private static bool menuSupportInstalled;
        private static DataTemplate menuHeaderTemplate;

        private static void InstallMenuSupport()
        {
            if (menuSupportInstalled) return;
            menuSupportInstalled = true;
            // A ContextMenu has its own popup tree. This also reaches the native
            // WPF editor menu without replacing its commands or mouse capture.
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
                new RoutedEventHandler((s, e) => PrepareMenu((ContextMenu)s)));
            EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent,
                new RoutedEventHandler((s, e) => {
                    var item = (MenuItem)s;
                    if (!ReferenceEquals(e.OriginalSource, item)) return;
                    PrepareMenuItems(item, MenuWorkArea(item));
                }));
        }

        internal static void PrepareMenu(ContextMenu menu)
        {
            if (menu == null) return;
            Install(menu.Resources);
            Size available = MenuWorkArea(menu.PlacementTarget as FrameworkElement ?? menu);
            menu.MaxWidth = available.Width;
            menu.MinWidth = Math.Min(160, available.Width);
            menu.MaxHeight = available.Height;
            PrepareMenuItems(menu, available);
        }

        private static Size MenuWorkArea(FrameworkElement target)
        {
            Rect area = SystemParameters.WorkArea;
            try
            {
                if (target != null && target.IsVisible)
                {
                    Point center = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
                    var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)center.X, (int)center.Y));
                    var source = PresentationSource.FromVisual(target);
                    Matrix transform = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
                    area = new MatrixTransform(transform).TransformBounds(new Rect(screen.WorkingArea.Left, screen.WorkingArea.Top, screen.WorkingArea.Width, screen.WorkingArea.Height));
                }
            }
            catch (InvalidOperationException) { }
            return new Size(Math.Max(80, Math.Min(360, area.Width - 16)), Math.Max(80, area.Height - 16));
        }

        private static void PrepareMenuItems(ItemsControl owner, Size available)
        {
            for (int index = 0; index < owner.Items.Count; index++)
            {
                var item = owner.Items[index] as MenuItem ?? owner.ItemContainerGenerator.ContainerFromIndex(index) as MenuItem;
                if (item == null) continue;
                item.MaxWidth = Math.Max(60, available.Width - 14);
                item.MaxHeight = available.Height;
                // Strings get a wrapping AccessText while visual headers retain
                // their original template, bindings and application data.
                if (item.Header is string && item.HeaderTemplate == null && item.HeaderTemplateSelector == null)
                {
                    if (menuHeaderTemplate == null)
                    {
                        var text = new FrameworkElementFactory(typeof(AccessText));
                        text.SetBinding(AccessText.TextProperty, new Binding());
                        text.SetValue(AccessText.TextWrappingProperty, TextWrapping.Wrap);
                        menuHeaderTemplate = new DataTemplate { VisualTree = text };
                    }
                    item.HeaderTemplate = menuHeaderTemplate;
                }
                if (item.Header is string && item.ReadLocalValue(FrameworkElement.ToolTipProperty) == DependencyProperty.UnsetValue)
                    item.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Header") { Source = item });
                PrepareMenuItems(item, available);
            }
        }
    }
}
