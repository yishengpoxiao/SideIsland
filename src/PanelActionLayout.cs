using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CodexIsland
{
    // Gaps live in their own grid tracks so the last action is the same size as
    // its siblings. A caption template preserves Content and localization hooks.
    internal static class PanelActionLayout
    {
        // Measure against the actual page width, including when a page is first
        // revealed. Collapsed actions leave no holes and never reserve a row.
        private sealed class ActionGrid : Grid
        {
            private readonly double minimumActionWidth;
            private readonly int maximumColumns;
            private int columns = -1, count = -1;
            private string positions;
            internal ActionGrid(int maximum, double minimum) { maximumColumns = maximum; minimumActionWidth = minimum; Grid.SetIsSharedSizeScope(this, true); }
            protected override Size MeasureOverride(Size constraint)
            {
                int next = Double.IsInfinity(constraint.Width) ? maximumColumns : Math.Max(1, Math.Min(maximumColumns, (int)((constraint.Width + 6) / (minimumActionWidth + 6))));
                int visible = 0; string order = "";
                for (int index = 0; index < Children.Count; index++)
                    if (Children[index].Visibility != Visibility.Collapsed) { visible++; order += index + ","; }
                next = Math.Min(next, Math.Max(1, visible));
                if (next != columns || visible != count || order != positions)
                {
                    columns = next; count = visible; positions = order;
                    ColumnDefinitions.Clear(); RowDefinitions.Clear();
                    for (int column = 0; column < columns; column++)
                    {
                        if (column > 0) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                        ColumnDefinitions.Add(new ColumnDefinition());
                    }
                    int rows = (visible + columns - 1) / columns;
                    for (int row = 0; row < rows; row++)
                    {
                        if (row > 0) RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
                        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, SharedSizeGroup = "ActionHeight" });
                    }
                    int position = 0;
                    foreach (UIElement child in Children)
                    {
                        if (child.Visibility == Visibility.Collapsed) { SetColumn(child, 0); SetRow(child, 0); continue; }
                        SetColumn(child, (position % columns) * 2); SetRow(child, (position / columns) * 2); position++;
                    }
                }
                return base.MeasureOverride(constraint);
            }
        }
        internal static T Caption<T>(T button) where T : ContentControl
        {
            if (button == null) throw new ArgumentNullException("button");
            var caption = new FrameworkElementFactory(typeof(TextBlock));
            caption.SetBinding(TextBlock.TextProperty, new Binding());
            caption.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            caption.SetValue(TextBlock.TextAlignmentProperty, button is CheckBox || button is RadioButton ? TextAlignment.Left : TextAlignment.Center);
            button.ContentTemplate = new DataTemplate { VisualTree = caption };
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            return button;
        }

        internal static Grid Create(int columns, params Button[] buttons)
        {
            return Create(columns, 68, buttons);
        }

        internal static Grid Create(int columns, double minimumWidth, params Button[] buttons)
        {
            if (columns < 1) throw new ArgumentOutOfRangeException("columns");
            if (Double.IsNaN(minimumWidth) || Double.IsInfinity(minimumWidth) || minimumWidth < 1) throw new ArgumentOutOfRangeException("minimumWidth");
            if (buttons == null) throw new ArgumentNullException("buttons");
            var grid = new ActionGrid(columns, minimumWidth);
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = Caption(buttons[i]);
                button.Margin = new Thickness(0); button.HorizontalAlignment = HorizontalAlignment.Stretch; button.VerticalAlignment = VerticalAlignment.Stretch;
                button.MinHeight = Math.Max(32, button.MinHeight); button.Padding = new Thickness(8, 5, 8, 5);
                grid.Children.Add(button);
            }
            return grid;
        }
    }
}
