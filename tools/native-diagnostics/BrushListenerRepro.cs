using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

// Independent WPF/native brush-listener diagnostic. This file references no
// application code and deliberately preserves the ordinary hardware rendering
// path. There are no sleeps, retries, native-fault handlers or frame barriers.
internal static class BrushListenerRepro
{
    private static TextWriter Record;

    [STAThread]
    private static int Main(string[] args)
    {
        StreamWriter file = null;
        try
        {
            Options options = Options.Parse(args);
            if (options.Help)
            {
                Console.WriteLine("BrushListenerRepro: --frozen-mode --count N --elements N --iterations N --seconds N --draw-kind both|text|rect --log project-file.txt");
                Console.WriteLine("Collector compatibility: --suite ToolPageLayoutTests selects mutable default; --suite BrushFrozen enables frozen replacement.");
                Console.WriteLine("Defaults: mutable bound brush, count=192 pairs per element, elements=4, iterations=2000, seconds=20, draw-kind=both.");
                Console.WriteLine("count is DrawText/DrawRectangle groups per OnRender; both uses the same brush twice per group. Runtime loop is capped at 30 seconds.");
                return 0;
            }
            if (options.LogPath != null)
            {
                string root = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string output = Path.GetFullPath(options.LogPath);
                if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Log must be inside the current project directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                file = new StreamWriter(output, false) { AutoFlush = true };
            }
            Record = file;
            Log("CONFIG pid=" + Process.GetCurrentProcess().Id + " version=" + Assembly.GetExecutingAssembly().GetName().Version +
                " mode=" + (options.Frozen ? "frozen-replacement" : "mutable-color-binding") + " count=" + options.Count + " elements=" + options.Elements +
                " brush-uses-per-element=" + (options.Count * (options.DrawKind == "both" ? 2 : 1)) + " draw-kind=" + options.DrawKind +
                " iterations=" + options.Iterations + " max-seconds=" + options.Seconds + " clr=" + Environment.Version +
                " os=" + Environment.OSVersion + " process-render-mode=" + RenderOptions.ProcessRenderMode + " render-tier=" + (RenderCapability.Tier >> 16));

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var color = new ColorSource();
            SolidColorBrush brush = MakeBrush(color, options.Frozen);
            var grid = new UniformGrid { Columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(options.Elements))) };
            var panels = new PaintPanel[options.Elements];
            for (int index = 0; index < panels.Length; index++)
            {
                panels[index] = new PaintPanel(options.Count, options.DrawKind, index) { Ink = brush };
                grid.Children.Add(panels[index]);
            }
            var window = new Window {
                Title = "WPF brush listener diagnostic [" + (options.Frozen ? "frozen" : "mutable") + "] " + Process.GetCurrentProcess().Id,
                Width = 720, Height = 520, Left = 32, Top = 32,
                AllowsTransparency = true, WindowStyle = WindowStyle.None,
                Background = Brushes.Transparent, Content = grid,
                ShowInTaskbar = true, Topmost = false, ResizeMode = ResizeMode.CanResize
            };
            Log("SHOW-BEGIN brush-frozen=" + brush.IsFrozen + " color-bound=" + BindingOperations.IsDataBound(brush, SolidColorBrush.ColorProperty));
            window.Show();
            window.UpdateLayout();
            PumpBackground();
            Log("SHOW-END renders=" + TotalRenders(panels));

            var elapsed = Stopwatch.StartNew();
            int completed = 0;
            for (int iteration = 0; iteration < options.Iterations && elapsed.Elapsed.TotalSeconds < options.Seconds; iteration++)
            {
                // Record before mutation: a native termination cannot falsely
                // appear to have completed the next iteration or cleanup.
                Log("ITER-BEGIN iteration=" + iteration + " ms=" + elapsed.ElapsedMilliseconds + " renders=" + TotalRenders(panels));
                color.Color = Palette(iteration);
                if (options.Frozen)
                {
                    brush = MakeBrush(color, true);
                    foreach (var panel in panels) panel.Ink = brush;
                }
                foreach (var panel in panels) { panel.Generation = iteration + 1; panel.InvalidateVisual(); }
                // Deliberately matches the old layout workload: replace render
                // data, mutate a shared bound brush, and resize a layered HWND.
                // UpdateLayout/Background Pump is not a compositor-frame fence.
                window.Width = (iteration & 1) == 0 ? 680 : 720;
                window.Height = (iteration & 2) == 0 ? 500 : 520;
                window.UpdateLayout();
                PumpBackground();
                completed++;
                Log("ITER-END iteration=" + iteration + " ms=" + elapsed.ElapsedMilliseconds + " renders=" + TotalRenders(panels) +
                    " size=" + window.ActualWidth.ToString("F1", CultureInfo.InvariantCulture) + "x" + window.ActualHeight.ToString("F1", CultureInfo.InvariantCulture) +
                    " color=" + brush.Color + " brush-frozen=" + brush.IsFrozen);
            }
            Log("WORKLOAD-END completed=" + completed + " ms=" + elapsed.ElapsedMilliseconds + " renders=" + TotalRenders(panels) +
                " limit=" + (completed == options.Iterations ? "iterations" : "seconds"));
            Log("CLOSE-BEGIN");
            window.Close();
            PumpBackground();
            app.Shutdown();
            GC.KeepAlive(color); GC.KeepAlive(brush); GC.KeepAlive(panels); GC.KeepAlive(app);
            Log("PASS workload-and-close-completed");
            return 0;
        }
        catch (Exception error)
        {
            // Managed failures remain failures; native fast-fail is untouched.
            Log("FAIL managed=" + error);
            return 1;
        }
        finally { Record = null; if (file != null) file.Dispose(); }
    }

    private static SolidColorBrush MakeBrush(ColorSource source, bool frozen)
    {
        var brush = new SolidColorBrush(source.Color);
        if (frozen) brush.Freeze();
        else BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, new Binding("Color") { Source = source, Mode = BindingMode.OneWay });
        return brush;
    }
    private static Color Palette(int index)
    {
        return (index & 1) == 0 ? Color.FromRgb(22, 27, 38) : Color.FromRgb(234, 238, 246);
    }
    private static long TotalRenders(PaintPanel[] panels) { long count = 0; foreach (var panel in panels) count += panel.RenderCount; return count; }
    private static void PumpBackground()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { frame.Continue = false; }));
        Dispatcher.PushFrame(frame);
    }
    private static void Log(string message)
    {
        string line = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + message;
        Console.WriteLine(line); Console.Out.Flush();
        if (Record != null) Record.WriteLine(line);
    }

    private sealed class ColorSource : INotifyPropertyChanged
    {
        private Color current = Color.FromRgb(22, 27, 38);
        public Color Color
        {
            get { return current; }
            set { if (current == value) return; current = value; var handler = PropertyChanged; if (handler != null) handler(this, new PropertyChangedEventArgs("Color")); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    private sealed class PaintPanel : FrameworkElement
    {
        internal static readonly DependencyProperty InkProperty = DependencyProperty.Register("Ink", typeof(Brush), typeof(PaintPanel), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        private static readonly Typeface Font = new Typeface("Segoe UI");
        private readonly int count, index;
        private readonly string kind;
        internal int Generation;
        internal long RenderCount;
        internal Brush Ink { get { return (Brush)GetValue(InkProperty); } set { SetValue(InkProperty, value); } }
        internal PaintPanel(int count, string kind, int index) { this.count = count; this.kind = kind; this.index = index; ClipToBounds = true; }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            Brush ink = Ink;
            if (ink == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            RenderCount++;
            int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count)));
            int rows = (count + columns - 1) / columns;
            double cellWidth = ActualWidth / columns, cellHeight = ActualHeight / rows;
            for (int item = 0; item < count; item++)
            {
                double x = (item % columns) * cellWidth, y = (item / columns) * cellHeight;
                if (kind != "text") drawing.DrawRectangle(ink, null, new Rect(x, y, Math.Max(1, cellWidth * 0.45), Math.Max(1, cellHeight * 0.45)));
                if (kind != "rect")
                {
                    var text = new FormattedText(index + ":" + Generation + ":" + item, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 7, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    drawing.DrawText(text, new Point(x + cellWidth * 0.45, y + cellHeight * 0.4));
                }
            }
        }
    }

    private sealed class Options
    {
        internal bool Frozen, Help;
        internal int Count = 192, Elements = 4, Iterations = 2000, Seconds = 20;
        internal string DrawKind = "both", LogPath;
        internal static Options Parse(string[] args)
        {
            var options = new Options();
            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index], value = null;
                int equals = argument.IndexOf('='); if (equals >= 0) { value = argument.Substring(equals + 1); argument = argument.Substring(0, equals); }
                if (argument == "--frozen-mode") { if (value != null) throw new ArgumentException("--frozen-mode takes no value."); options.Frozen = true; continue; }
                if (argument == "--help") { options.Help = true; continue; }
                if (value == null) { if (++index >= args.Length) throw new ArgumentException("Missing value for " + argument); value = args[index]; }
                switch (argument)
                {
                    case "--count": options.Count = Number(value, 1, 8192, argument); break;
                    case "--elements": options.Elements = Number(value, 1, 16, argument); break;
                    case "--iterations": options.Iterations = Number(value, 1, 100000, argument); break;
                    case "--seconds": options.Seconds = Number(value, 1, 30, argument); break;
                    case "--draw-kind": if (value != "both" && value != "text" && value != "rect") throw new ArgumentException("--draw-kind must be both, text or rect."); options.DrawKind = value; break;
                    case "--log": options.LogPath = value; break;
                    case "--suite":
                        if (value == "BrushFrozen") options.Frozen = true;
                        else if (value != "ToolPageLayoutTests") throw new ArgumentException("Fixture collector --suite must be ToolPageLayoutTests or BrushFrozen.");
                        break;
                    default: throw new ArgumentException("Unknown option: " + argument);
                }
            }
            return options;
        }
        private static int Number(string value, int minimum, int maximum, string name)
        {
            int number; if (!Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < minimum || number > maximum) throw new ArgumentException(name + " must be between " + minimum + " and " + maximum + "."); return number;
        }
    }
}
