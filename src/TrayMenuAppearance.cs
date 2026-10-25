using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace CodexIsland
{
    internal static class TrayMenuAppearance
    {
        private static readonly Font MenuFont = new Font("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point);

        internal static void Apply(Forms.ContextMenuStrip menu)
        {
            ApplyMenu(menu);
        }

        private static void ApplyMenu(Forms.ToolStripDropDownMenu menu)
        {
            if (!(menu.Renderer is CompactRenderer))
            {
                menu.Renderer = new CompactRenderer();
                menu.Font = MenuFont;
                menu.ShowImageMargin = true;
                menu.ShowCheckMargin = false;
                menu.Opening += (s, e) => Layout(menu);
                // WinForms computes image/shortcut margins again when the first
                // popup HWND adopts its DPI. Finish sizing after that layout.
                menu.Opened += (s, e) => Layout(menu);
                menu.LayoutCompleted += (s, e) => ConstrainRows(menu);
                menu.SizeChanged += (s, e) => Round(menu);
            }
            Layout(menu);
            menu.Invalidate();
        }

        private static Color Surface { get { return UiTheme.IsLight ? Color.White : Color.FromArgb(28, 28, 30); } }
        private static Color Text { get { return UiTheme.IsLight ? Color.FromArgb(28, 28, 30) : Color.FromArgb(235, 235, 240); } }
        private static Color Muted { get { return UiTheme.IsLight ? Color.FromArgb(107, 107, 115) : Color.FromArgb(152, 152, 159); } }
        private static Color Border { get { return UiTheme.IsLight ? Color.FromArgb(221, 222, 229) : Color.FromArgb(58, 58, 60); } }
        private static Color Hover { get { return UiTheme.IsLight ? Color.FromArgb(237, 237, 243) : Color.FromArgb(58, 58, 64); } }
        private static Color Accent { get { return UiTheme.IsLight ? Color.FromArgb(0, 122, 255) : Color.FromArgb(10, 132, 255); } }

        private static float Scale(Forms.ToolStrip menu)
        {
            using (Graphics graphics = menu.CreateGraphics()) return Math.Max(1, graphics.DpiX / 96f);
        }

        private static string Shortcut(Forms.ToolStripItem item)
        {
            var command = item as Forms.ToolStripMenuItem;
            if (command == null || !command.ShowShortcutKeys) return "";
            if (!String.IsNullOrEmpty(command.ShortcutKeyDisplayString)) return command.ShortcutKeyDisplayString;
            return command.ShortcutKeys == Forms.Keys.None ? "" : new Forms.KeysConverter().ConvertToString(command.ShortcutKeys);
        }

        private static int ShortcutWidth(Forms.ToolStripItem item, float scale, IDeviceContext context)
        {
            string value = Shortcut(item);
            return String.IsNullOrEmpty(value) ? 0 : Forms.TextRenderer.MeasureText(context, value, MenuFont, Size.Empty, Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPadding).Width + (int)Math.Round(12 * scale);
        }

        private static int CaptionLeft(float scale) { return (int)Math.Round(26 * scale); }
        private static int CaptionRight(float scale) { return (int)Math.Round(16 * scale); }

        private static void Layout(Forms.ToolStripDropDownMenu menu)
        {
            using (Graphics graphics = menu.CreateGraphics())
            {
            float scale = Math.Max(1, graphics.DpiX / 96f);
            int gap = (int)Math.Round(6 * scale), allowance = CaptionLeft(scale) + CaptionRight(scale);
            menu.Padding = new Forms.Padding(gap);
            menu.BackColor = Surface; menu.ForeColor = Text;
            Rectangle work = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
            int maximum = Math.Max(80, Math.Min((int)(360 * scale), work.Width - gap * 2));
            int width = Math.Min(maximum, (int)(180 * scale));
            foreach (Forms.ToolStripItem item in menu.Items)
            {
                item.Font = MenuFont; item.BackColor = Surface; item.ForeColor = Text;
                int natural = Forms.TextRenderer.MeasureText(graphics, item.Text, MenuFont, Size.Empty, Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPadding).Width + (int)Math.Ceiling(4 * scale);
                width = Math.Min(maximum, Math.Max(width, natural + allowance + ShortcutWidth(item, scale, graphics) + gap * 2));
            }
            int itemWidth = Math.Max(48, width - gap * 2), textWidth = Math.Max(20, itemWidth - allowance);
            for (int index = 0; index < menu.Items.Count; index++)
            {
                Forms.ToolStripItem item = menu.Items[index];
                item.AutoSize = false;
                // ToolStripDropDownMenu owns Padding and recalculates it from
                // its native image column. Item margins provide a stable inset
                // without replacing keyboard navigation or scroll buttons.
                item.Margin = new Forms.Padding(gap, index == 0 ? Math.Max(0, gap - 2) : 0, gap, index == menu.Items.Count - 1 ? Math.Max(0, gap - 2) : 0);
                if (item is Forms.ToolStripSeparator) { item.Size = new Size(itemWidth, gap * 2 + 1); continue; }
                Size caption = Forms.TextRenderer.MeasureText(graphics, item.Text, MenuFont, new Size(Math.Max(20, textWidth - ShortcutWidth(item, scale, graphics)), Int32.MaxValue), Forms.TextFormatFlags.WordBreak | Forms.TextFormatFlags.NoPadding);
                item.Padding = new Forms.Padding(gap, gap, gap, gap);
                item.Size = new Size(itemWidth, Math.Max((int)Math.Round(32 * scale), caption.Height + gap * 2));
                item.TextAlign = ContentAlignment.MiddleLeft;
                var command = item as Forms.ToolStripMenuItem;
                var child = command == null || !command.HasDropDownItems ? null : command.DropDown as Forms.ToolStripDropDownMenu;
                if (child != null) ApplyMenu(child);
            }
            menu.MaximumSize = new Size(width, Math.Max(80, work.Height - gap * 2));
            menu.MinimumSize = new Size(width, 0);
            menu.PerformLayout(); Round(menu);
            }
        }

        private static void ConstrainRows(Forms.ToolStripDropDownMenu menu)
        {
            // The native menu layout measures unwrapped captions and can give a
            // row one extra pixel (or the full width of a long caption). Keep
            // that layout's scrolling/positions, then fit the painted rows to
            // the actual constrained popup rather than clipping their content.
            int inset = (int)Math.Round(6 * Scale(menu));
            menu.SuspendLayout();
            try
            {
                foreach (Forms.ToolStripItem item in menu.Items)
                {
                    int width = Math.Max(20, menu.ClientSize.Width - item.Bounds.Left - inset);
                    if (item.Width != width) item.Width = width;
                }
            }
            finally { menu.ResumeLayout(false); }
        }

        private static void Round(Forms.ToolStrip menu)
        {
            if (menu.Width < 2 || menu.Height < 2) return;
            using (GraphicsPath path = Rounded(new RectangleF(0, 0, menu.Width, menu.Height), 12 * Scale(menu)))
            {
                Region previous = menu.Region;
                menu.Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        }

        private static GraphicsPath Rounded(RectangleF rect, float radius)
        {
            float diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            var path = new GraphicsPath();
            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }

        private sealed class CompactRenderer : Forms.ToolStripProfessionalRenderer
        {
            protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
            {
                e.Graphics.Clear(Surface);
            }
            protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Rounded(new RectangleF(.5f, .5f, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 12 * Scale(e.ToolStrip)))
                using (var pen = new Pen(Border)) e.Graphics.DrawPath(pen, path);
            }
            protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e) { }
            protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Enabled || !e.Item.Selected) return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Rounded(new RectangleF(0, 0, e.Item.Width, e.Item.Height), 8 * Scale(e.ToolStrip)))
                using (var brush = new SolidBrush(Hover)) e.Graphics.FillPath(brush, path);
            }
            protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
            {
                float scale = Math.Max(1, e.Graphics.DpiX / 96f);
                int inset = (int)Math.Round(8 * scale);
                if (!String.Equals(e.Text, e.Item.Text, StringComparison.Ordinal))
                {
                    int width = Forms.TextRenderer.MeasureText(e.Graphics, e.Text, e.TextFont, Size.Empty, Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPadding).Width;
                    var gesture = new Rectangle(Math.Max(inset, e.Item.Width - inset * 2 - width), 0, width, e.Item.Height);
                    Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, gesture, Muted, Forms.TextFormatFlags.Right | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPadding);
                    return;
                }
                Rectangle bounds = new Rectangle(CaptionLeft(scale), 0, Math.Max(20, e.Item.Width - CaptionLeft(scale) - CaptionRight(scale) - ShortcutWidth(e.Item, scale, e.Graphics)), e.Item.Height);
                var flags = (e.TextFormat & ~(Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.EndEllipsis | Forms.TextFormatFlags.VerticalCenter)) | Forms.TextFormatFlags.WordBreak | Forms.TextFormatFlags.NoPadding;
                Size size = Forms.TextRenderer.MeasureText(e.Graphics, e.Text, e.TextFont, new Size(bounds.Width, Int32.MaxValue), flags);
                bounds.Y = Math.Max(0, (e.Item.Height - size.Height) / 2); bounds.Height = size.Height;
                Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, bounds, e.Item.Enabled ? Text : Muted, flags);
            }
            protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
            {
                float scale = Scale(e.ToolStrip), x = e.ImageRectangle.Left + (e.ImageRectangle.Width - 10 * scale) / 2, y = (e.Item.Height - 8 * scale) / 2;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Accent, 1.6f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    e.Graphics.DrawLines(pen, new[] { new PointF(x, y + 4 * scale), new PointF(x + 3 * scale, y + 7 * scale), new PointF(x + 10 * scale, y) });
            }
            protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
            {
                float scale = Scale(e.Item.Owner);
                float x = e.Item is Forms.ToolStripMenuItem ? e.Item.Width - 12 * scale : e.ArrowRectangle.Left + e.ArrowRectangle.Width / 2f;
                float y = e.Item is Forms.ToolStripMenuItem ? e.Item.Height / 2f : e.ArrowRectangle.Top + e.ArrowRectangle.Height / 2f;
                PointF[] points = e.Direction == Forms.ArrowDirection.Left ? new[] { new PointF(x + 2 * scale, y - 4 * scale), new PointF(x - 2 * scale, y), new PointF(x + 2 * scale, y + 4 * scale) } :
                    e.Direction == Forms.ArrowDirection.Down ? new[] { new PointF(x - 4 * scale, y - 2 * scale), new PointF(x, y + 2 * scale), new PointF(x + 4 * scale, y - 2 * scale) } :
                    e.Direction == Forms.ArrowDirection.Up ? new[] { new PointF(x - 4 * scale, y + 2 * scale), new PointF(x, y - 2 * scale), new PointF(x + 4 * scale, y + 2 * scale) } :
                    new[] { new PointF(x - 2 * scale, y - 4 * scale), new PointF(x + 2 * scale, y), new PointF(x - 2 * scale, y + 4 * scale) };
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Muted, 1.5f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round }) e.Graphics.DrawLines(pen, points);
            }
            protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
            {
                int inset = (int)Math.Round(8 * Scale(e.ToolStrip));
                using (var pen = new Pen(Border)) e.Graphics.DrawLine(pen, inset, e.Item.Height / 2, e.Item.Width - inset, e.Item.Height / 2);
            }
        }
    }
}
