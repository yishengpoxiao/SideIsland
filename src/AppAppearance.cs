using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace CodexIsland
{
    internal static class AppAppearance
    {
        private static readonly List<WeakReference> Windows = new List<WeakReference>();
        private static bool classHandlerInstalled;
        internal static event Action Changed;
        internal static void Configure(string language, string theme)
        {
            UiTheme.Configure(theme); UiLocalization.Configure(language);
            var application = Application.Current;
            if (application != null) UiTheme.RefreshResourceBrushes(application.Resources);
            for (int i = Windows.Count - 1; i >= 0; i--) { var window = Windows[i].Target as Window; if (window == null) Windows.RemoveAt(i); else RegisterTree(window); }
            var changed = Changed; if (changed != null) changed();
        }
        internal static void Refresh() { Configure(UiLocalization.Language, UiTheme.Name); }
        internal static void Apply(Window root, string language, string theme) { Configure(language, theme); Register(root); }
        internal static void Register(Window window)
        {
            if (window == null) return;
            if (!classHandlerInstalled) { classHandlerInstalled = true; EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, e) => { var candidate = s as Window; if (candidate != null) Register(candidate); })); }
            bool existing = false; foreach (var reference in Windows) if (Object.ReferenceEquals(reference.Target, window)) existing = true;
            if (!existing) { Windows.Add(new WeakReference(window)); UiTheme.Install(window.Resources); window.Loaded += (s, e) => RegisterTree(window); }
            RegisterTree(window);
        }
        internal static void RegisterTree(DependencyObject root)
        {
            var seen = new HashSet<DependencyObject>(); Visit(root, seen, false);
        }
        private static void Visit(DependencyObject item, HashSet<DependencyObject> seen, bool protectedParent)
        {
            if (item == null || !seen.Add(item)) return;
            bool protectedColors = protectedParent || UiTheme.IsColorProtected(item);
            if (!protectedColors) UiTheme.RefreshLiteralBrushes(item);
            var element = item as FrameworkElement;
            if (element != null)
            {
                if (!protectedColors) UiTheme.RefreshResourceBrushes(element.Resources);
                var menu = element.ContextMenu;
                if (menu != null) { UiTheme.Install(menu.Resources); Visit(menu, seen, protectedColors); }
                var visualTip = element.ToolTip as DependencyObject;
                if (visualTip != null) Visit(visualTip, seen, protectedColors);
                string tip = element.ToolTip as string;
                if (tip != null && UiLocalization.HasTranslation(tip) && !UiLocalization.IsProtected(item)) UiLocalization.ToolTip(element, tip);
                if (!protectedColors) UiTheme.Prepare(element);
            }
            var documentElement = item as FrameworkContentElement;
            if (documentElement != null)
            {
                if (!protectedColors) UiTheme.RefreshResourceBrushes(documentElement.Resources);
                if (documentElement.ContextMenu != null) { UiTheme.Install(documentElement.ContextMenu.Resources); Visit(documentElement.ContextMenu, seen, protectedColors); }
                var documentTip = documentElement.ToolTip as DependencyObject; if (documentTip != null) Visit(documentTip, seen, protectedColors);
            }
            if (!UiLocalization.IsProtected(item))
            {
                var window = item as Window; if (window != null && UiLocalization.HasTranslation(window.Title)) UiLocalization.Mark(window, Window.TitleProperty, window.Title);
                var content = item as ContentControl;
                if (content != null && (content is Button || content is CheckBox || content is RadioButton || content is Label || content is ToolTip))
                {
                    string value = content.Content as string; if (value != null && UiLocalization.HasTranslation(value)) UiLocalization.Content(content, value);
                }
                var header = item as HeaderedItemsControl;
                if (header != null) { string value = header.Header as string; if (value != null && UiLocalization.HasTranslation(value)) UiLocalization.Header(header, value); }
                var expander = item as Expander;
                if (expander != null) { string value = expander.Header as string; if (value != null && UiLocalization.HasTranslation(value)) UiLocalization.Mark(expander, HeaderedContentControl.HeaderProperty, value); }
                var combo = item as ComboBox; if (combo != null) UiLocalization.Combo(combo);
            }
            // Popup roots and FlowDocument content are not guaranteed to appear
            // in their owner's visual tree, particularly while currently closed.
            var popup = item as Popup; if (popup != null) Visit(popup.Child, seen, protectedColors);
            var richText = item as RichTextBox; if (richText != null) Visit(richText.Document, seen, protectedColors);
            var contentOwner = item as ContentControl; if (contentOwner != null) Visit(contentOwner.Content as DependencyObject, seen, protectedColors);
            var headerOwner = item as HeaderedContentControl; if (headerOwner != null) Visit(headerOwner.Header as DependencyObject, seen, protectedColors);
            var headerItems = item as HeaderedItemsControl; if (headerItems != null) Visit(headerItems.Header as DependencyObject, seen, protectedColors);
            var children = new List<DependencyObject>();
            try { foreach (object child in LogicalTreeHelper.GetChildren(item)) { var dependency = child as DependencyObject; if (dependency != null) children.Add(dependency); } } catch (InvalidOperationException) { }
            foreach (var child in children) Visit(child, seen, protectedColors);
            if (item is Visual || item is System.Windows.Media.Media3D.Visual3D) { int count = VisualTreeHelper.GetChildrenCount(item); for (int index = 0; index < count; index++) Visit(VisualTreeHelper.GetChild(item, index), seen, protectedColors); }
            // Frozen palette snapshots do not send Color-change notifications.
            // Existing custom-drawn child visuals must record their next frame
            // using the current palette, not retain the old DrawingContext data.
            var visual = item as UIElement; if (visual != null) visual.InvalidateVisual();
        }
    }
}
