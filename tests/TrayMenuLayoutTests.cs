using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodexIsland
{
    internal static class TrayMenuLayoutTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Check(bool value, string message) { if (!value) throw new Exception("Tray menu layout: " + message); }

        internal static void Run(string directory = null)
        {
            string language = UiLocalization.Language, theme = UiTheme.Name;
            string previousData = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string folder = Path.Combine(Path.GetTempPath(), "CodexIsland.TrayMenu." + Guid.NewGuid().ToString("N"));
            int scenarios = 0;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                foreach (string locale in new[] { "zh", "en" })
                    foreach (string appearance in new[] { "light", "dark" })
                    {
                        AppAppearance.Configure(locale, appearance);
                        new SettingsStore().Save(new Settings { Language = locale, Theme = appearance, NotificationsEnabled = false });
                        var window = new IslandWindow(true);
                        window.Show(); Pump();
                        StopTimers(window);
                        Forms.ContextMenuStrip actual = null;
                        try
                        {
                            var tray = (Forms.NotifyIcon)typeof(IslandWindow).GetField("tray", Private).GetValue(window);
                            actual = tray.ContextMenuStrip;
                            Check(actual.Items.Count == 11, "all existing tray commands are retained");
                            string canonical = actual.Items[0].Tag as string;
                            Check(actual.Items[0].Text == UiLocalization.Localize(canonical), "real tray command follows its selected language");
                            Show(actual);
                            Save(actual, directory, "tray-" + locale + "-" + appearance + ".png");
                            Verify(actual, appearance, true);
                            actual.Close(); Pump();
                            Check(!actual.Visible, "real tray popup closes normally");
                            scenarios++;
                        }
                        finally { if (actual != null) { actual.Close(); actual.Dispose(); } StopTimers(window); window.Close(); Pump(); }

                        using (var fixture = new Forms.ContextMenuStrip())
                        {
                            int clicks = 0;
                            var enabled = new Forms.ToolStripMenuItem(UiLocalization.Localize("显示灵动岛")); enabled.Click += (s, e) => clicks++;
                            var checkedItem = new Forms.ToolStripMenuItem(UiLocalization.Localize("开启最近边缘吸附")) { Checked = true };
                            var disabled = new Forms.ToolStripMenuItem(UiLocalization.Localize("立即刷新")) { Enabled = false }; disabled.Click += (s, e) => clicks++;
                            string original = locale == "en" ? "A long user supplied command caption that must wrap completely without losing words or overlapping the keyboard shortcut" : "用户提供的较长菜单标题，完整换行后仍保留原文，并且不与快捷键或子菜单箭头重叠";
                            var longItem = new Forms.ToolStripMenuItem(original) { ShortcutKeys = Forms.Keys.Control | Forms.Keys.Shift | Forms.Keys.R, ShortcutKeyDisplayString = "Ctrl+Shift+R" };
                            var parent = new Forms.ToolStripMenuItem(UiLocalization.Localize("收起文字方向"));
                            parent.DropDownItems.Add(UiLocalization.Localize("自动正向"));
                            parent.DropDownItems.Add(UiLocalization.Localize("旋转 180°"));
                            parent.DropDownItems.Add(UiLocalization.Localize("左右镜像"));
                            fixture.Items.Add(enabled); fixture.Items.Add(checkedItem); fixture.Items.Add(disabled);
                            fixture.Items.Add(new Forms.ToolStripSeparator()); fixture.Items.Add(longItem); fixture.Items.Add(parent);
                            TrayMenuAppearance.Apply(fixture); Show(fixture);
                            Save(fixture, directory, "tray-fixture-" + locale + "-" + appearance + ".png");
                            Verify(fixture, appearance, false);
                            Check(longItem.Text == original && longItem.ShortcutKeyDisplayString == "Ctrl+Shift+R", "long user text and keyboard gesture remain unchanged");
                            Check(longItem.Height > enabled.Height, "long caption grows to complete wrapped rows");
                            disabled.PerformClick(); Check(clicks == 0, "disabled command cannot execute");
                            Check(checkedItem.Checked && !disabled.Enabled, "styling preserves checked and disabled state");
                            Save(fixture, directory, "tray-fixture-" + locale + "-" + appearance + ".png");
                            parent.ShowDropDown(); Pump();
                            var submenu = (Forms.ToolStripDropDownMenu)parent.DropDown;
                            Check(submenu.Visible && submenu.Items.Count == 3, "native submenu opens with all original commands");
                            Verify(submenu, appearance, true);
                            Save(submenu, directory, "tray-submenu-" + locale + "-" + appearance + ".png");
                            parent.HideDropDown(); Pump(); Check(!submenu.Visible, "submenu closes normally");
                            enabled.PerformClick(); Pump(); Check(clicks == 1, "enabled command executes its original click handler once");
                            fixture.Close(); Pump(); Check(!fixture.Visible, "fixture menu releases its popup");
                            scenarios++;
                        }
                    }
                RunArrows();
                Console.WriteLine("PASS: " + scenarios + " real and fixture tray menu layouts, Chinese/English, light/dark, original commands, wrapped captions, shortcuts, checks, disabled state, nested menus and rounded rendering.");
            }
            finally
            {
                AppAppearance.Configure(language, theme);
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previousData);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        internal static void RunArrows()
        {
            var rendererType = typeof(TrayMenuAppearance).GetNestedType("CompactRenderer", BindingFlags.NonPublic);
            Check(rendererType != null, "compact renderer is available for isolated arrow drawing");
            var renderer = (Forms.ToolStripRenderer)Activator.CreateInstance(rendererType, true);
            using (var strip = new Forms.ToolStrip { AutoSize = false, Size = new Size(220, 90), Visible = false })
            {
                var item = new Forms.ToolStripButton { AutoSize = false, Size = new Size(180, 70) };
                strip.Items.Add(item);
                Check(!strip.Visible && !strip.IsHandleCreated, "arrow fixture starts without a visible native window");
                float scale;
                using (Graphics native = strip.CreateGraphics()) scale = Math.Max(1, native.DpiX / 96f);
                Rectangle target = new Rectangle(40, 20, 32, 36);
                float centerX = target.Left + target.Width / 2f, centerY = target.Top + target.Height / 2f;
                foreach (Forms.ArrowDirection direction in new[] { Forms.ArrowDirection.Up, Forms.ArrowDirection.Down, Forms.ArrowDirection.Left, Forms.ArrowDirection.Right })
                    using (var bitmap = new Bitmap(220, 90))
                    {
                        using (Graphics graphics = Graphics.FromImage(bitmap))
                        {
                            graphics.Clear(Color.Transparent);
                            renderer.DrawArrow(new Forms.ToolStripArrowRenderEventArgs(graphics, item, target, Color.Red, direction));
                        }
                        var pixels = new List<PointF>();
                        int left = bitmap.Width, right = -1, top = bitmap.Height, bottom = -1;
                        for (int y = 0; y < bitmap.Height; y++)
                            for (int x = 0; x < bitmap.Width; x++)
                                if (bitmap.GetPixel(x, y).A > 60)
                                {
                                    pixels.Add(new PointF(x + .5f, y + .5f));
                                    left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                                }
                        string context = direction + " non-MenuItem arrow";
                        Check(pixels.Count > 4, context + " produces visible pixels");
                        Check(Math.Abs((left + right + 1) / 2f - centerX) <= 1.1 && Math.Abs((top + bottom + 1) / 2f - centerY) <= 1.1,
                            context + " stays centered in the provided ArrowRectangle rather than a menu item's right edge");
                        Check(left >= target.Left && right < target.Right && top >= target.Top && bottom < target.Bottom, context + " fits entirely inside the supplied arrow area");
                        bool vertical = direction == Forms.ArrowDirection.Up || direction == Forms.ArrowDirection.Down;
                        Check(vertical ? right - left > bottom - top : bottom - top > right - left, context + " has the expected chevron orientation");
                        double centerTotal = 0, wingTotal = 0; int centers = 0, wings = 0;
                        foreach (PointF point in pixels)
                        {
                            float distance = vertical ? Math.Abs(point.X - centerX) : Math.Abs(point.Y - centerY);
                            double axis = vertical ? point.Y : point.X;
                            if (distance <= scale) { centerTotal += axis; centers++; }
                            if (distance >= 2.8f * scale) { wingTotal += axis; wings++; }
                        }
                        Check(centers > 0 && wings > 0, context + " contains a tip and both outer wings");
                        double tip = centerTotal / centers, wing = wingTotal / wings;
                        bool pointsNegative = direction == Forms.ArrowDirection.Up || direction == Forms.ArrowDirection.Left;
                        Check(pointsNegative ? tip < wing - scale : tip > wing + scale, context + " points toward its requested direction");
                        Check(!strip.Visible, context + " does not show a native fixture window");
                    }
            }
            Console.WriteLine("PASS: four isolated native arrow directions, supplied-rectangle pixel centers, orientation and up/down/left/right tips; no visible HWND.");
        }

        private static void Show(Forms.ToolStripDropDownMenu menu)
        {
            Rectangle area = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
            menu.Show(new Point(area.Left + area.Width / 3, area.Top + area.Height / 5));
            Pump();
            Check(menu.Visible, "native popup is visible for its own render and geometry checks");
        }

        private static void Verify(Forms.ToolStripDropDownMenu menu, string appearance, bool oneLine)
        {
            Check(menu.Renderer.GetType().Name == "CompactRenderer", "root and child popups use the compact renderer");
            Check(menu.Font.Name == "Segoe UI" && Math.Abs(menu.Font.SizeInPoints - 9.75) < .01, "menu uses the compact 13 px font");
            Rectangle work = Forms.Screen.FromControl(menu).WorkingArea;
            Check(menu.Width <= work.Width && menu.Height <= work.Height, "popup stays inside the available work area");
            Check(menu.Region != null && !menu.Region.IsVisible(0, 0) && menu.Region.IsVisible(menu.Width / 2, menu.Height / 2), "native popup has rounded corners and a solid center");
            Color expected = appearance == "light" ? Color.White : Color.FromArgb(28, 28, 30);
            Check(menu.BackColor.ToArgb() == expected.ToArgb(), "native surface matches the selected iOS palette");
            int? rowHeight = null; int previousBottom = -1;
            string rowSummary = "";
            foreach (Forms.ToolStripItem row in menu.Items)
            {
                Size natural = Forms.TextRenderer.MeasureText(row.Text, row.Font, Size.Empty, Forms.TextFormatFlags.SingleLine);
                rowSummary += " [" + row.Text + ":" + row.Height + ",natural=" + natural + ",fontHeight=" + row.Font.Height + ",bounds=" + row.Bounds + "]";
            }
            foreach (Forms.ToolStripItem item in menu.Items)
            {
                Check(item.Bounds.Left >= 0 && item.Bounds.Right <= menu.ClientSize.Width, "item fits horizontally inside its menu: text=" + item.Text + "; bounds=" + item.Bounds + "; menu=" + menu.Size + "; client=" + menu.ClientSize + "; padding=" + menu.Padding + "; maximum=" + menu.MaximumSize + "; owner=" + (menu.OwnerItem == null ? "root" : menu.OwnerItem.Text));
                Check(previousBottom <= item.Bounds.Top, "rows retain their order without overlap"); previousBottom = item.Bounds.Bottom;
                if (item is Forms.ToolStripSeparator) continue;
                Check(item.Height >= 30 && item.Font.Equals(menu.Font), "captions use consistent fonts and comfortable compact rows");
                if (oneLine) { if (rowHeight.HasValue) Check(item.Height == rowHeight.Value, "single-line commands share one row height: text=" + item.Text + "; actual=" + item.Height + "; expected=" + rowHeight.Value + "; menu=" + menu.Size + "; appearance=" + appearance + "; language=" + UiLocalization.Language + "; rows=" + rowSummary); else rowHeight = item.Height; }
            }
            using (var bitmap = new Bitmap(menu.Width, menu.Height))
            {
                menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                Color rendered = bitmap.GetPixel(bitmap.Width / 2, Math.Min(3, bitmap.Height - 1));
                Check(rendered.ToArgb() == expected.ToArgb(), "rendered menu background follows its selected theme");
            }
        }

        private static void Save(Forms.ToolStripDropDownMenu menu, string directory, string name)
        {
            if (String.IsNullOrEmpty(directory)) return;
            using (var bitmap = new Bitmap(menu.Width, menu.Height))
            {
                menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(directory, name), ImageFormat.Png);
            }
        }

        private static void StopTimers(IslandWindow window)
        {
            foreach (var field in typeof(IslandWindow).GetFields(Private))
            { var timer = field.GetValue(window) as DispatcherTimer; if (timer != null) timer.Stop(); }
        }

        private static void Pump()
        {
            Forms.Application.DoEvents();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Forms.Application.DoEvents();
        }
    }
}
