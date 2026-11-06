using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Headless;
using SideIsland.Desktop;

namespace SideIsland.Tests;
internal static class LayoutTests
{
    public static void Run(string? artifactDirectory)
    {
        var folder = Path.Combine(Path.GetTempPath(), "side-island-layout-" + Guid.NewGuid().ToString("N"));
        if (artifactDirectory is not null) Directory.CreateDirectory(artifactDirectory);
        int cases = 0, captions = 0; var issues = new List<string>(); var toolCaptions = new Dictionary<string, int>();
        try
        {
            foreach (string language in new[] { "zh", "en" }) foreach (bool light in new[] { false, true })
            foreach (int width in new[] { 320, 480, 760 }) foreach (int height in new[] { 360, 720 })
            foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
            {
                var window = new IslandWindow(true, folder); window.Settings.ExpandedWidth = width; window.Settings.ExpandedHeight = height;
                window.SetRenderScaling(scale);
                window.Show(); window.Expand(); AppearanceTestActions.Set(window, language, light);
                foreach (var tool in IslandWindow.Tools)
                {
                    window.SelectToolForTest(tool.Id); window.Width = width; window.Height = height;
                    if (tool.Id == "workspaces")
                    {
                        // Loading is intentionally asynchronous. Do not count the
                        // temporary loading label as a completed editor layout.
                        var elapsed = System.Diagnostics.Stopwatch.StartNew();
                        while (!window.LatestWorkspaceLoad.IsCompleted && elapsed.ElapsedMilliseconds < 10000) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(2); }
                        if (!window.LatestWorkspaceLoad.IsCompleted) throw new TimeoutException("Workspace layout did not finish loading.");
                        window.LatestWorkspaceLoad.GetAwaiter().GetResult();
                        if (!window.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "workspace-save-scene"))
                            throw new InvalidOperationException("Workspace layout must inspect the loaded editor.");
                    }
                    Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                    AppearanceTestActions.Assert(window, language, light);
                    var context = $"{language}/{light}/{width}x{height}/{scale}/{tool.Id}";
                    if (Math.Abs(window.RenderScaling - scale) > .001) issues.Add(context + " requested scaling not applied");
                    if (!window.IsVisible || window.Bounds.Width <= 0) issues.Add(context + " invalid window");
                    foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible && b.Bounds.Width > 0))
                    {
                        captions++; toolCaptions[tool.Id] = toolCaptions.GetValueOrDefault(tool.Id) + 1; var label = button.Content as TextBlock;
                        if (label is not null && label.Bounds.Height > button.Bounds.Height + 2) issues.Add(context + " cropped action " + label.Text);
                        var p = button.TranslatePoint(default, window); if (p is Point pt && (pt.X < -2 || pt.X + button.Bounds.Width > window.Bounds.Width + 2)) issues.Add(context + " horizontal action overflow");
                    }
                    var viewer = window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(v => v.Content is StackPanel);
                    if (viewer is not null)
                    {
                        viewer.ScrollToEnd(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); if (viewer.Offset.Y + viewer.Viewport.Height < viewer.Extent.Height - 3) issues.Add(context + " unreachable page end");
                        var presenter = viewer.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault(p => ReferenceEquals(p.TemplatedParent, viewer));
                        if (presenter is not null && viewer.Content is StackPanel content)
                            foreach (var field in content.GetVisualDescendants().OfType<Control>().Where(c => c is TextBox or ComboBox or Button && c.IsEffectivelyVisible && c.Bounds.Width > 0))
                            {
                                var origin = field.TranslatePoint(default, presenter);
                                if (origin is Point location && (location.X < -1 || location.X + field.Bounds.Width > presenter.Bounds.Width + 1)) issues.Add(context + " content clipped horizontally: " + field.GetType().Name);
                                if (origin is Point gutter && presenter.Bounds.Width - gutter.X - field.Bounds.Width < 5) issues.Add(context + " missing scrollbar clearance: " + field.GetType().Name);
                            }
                    }
                    if (artifactDirectory is not null && height == 720 &&
                        (scale == 1 && width == 480 && language == "zh" && !light && tool.Id is "focus" or "settings" or "media" or "qr" or "hash" ||
                         width == 320 && language == "en" && light && tool.Id is "screen-pin" or "notifications" or "batch-files" or "batch-images" or "hash"))
                    { AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); Thread.Sleep(220); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(3); using var image = new RenderTargetBitmap(new((int)Math.Round(width * scale), (int)Math.Round(height * scale)), new(96 * scale, 96 * scale)); image.Render(window); image.Save(Path.Combine(artifactDirectory, tool.Id + (tool.Id == "hash" ? "-" + language + "-" + width : "") + "-" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png"), PngBitmapEncoderOptions.Default); }
                    cases++;
                }
                window.Close(); Dispatcher.UIThread.RunJobs();
            }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        if (artifactDirectory is not null) File.WriteAllText(Path.Combine(artifactDirectory, "layout-result.json"), JsonSerializer.Serialize(new { Cases = cases, Captions = captions, ToolCaptions = toolCaptions, Issues = issues }, new JsonSerializerOptions { WriteIndented = true }));
        if (issues.Count > 0) throw new InvalidOperationException(String.Join("\n", issues.Take(50)));
        Console.WriteLine($"PASS: {cases} actual Avalonia tool layouts, {captions} action captions; zh/en, light/dark, three widths, two heights, 100/125/150/200% headless DPI changes and scroll-end access. Native desktop DPI/compositor behavior is not implied.");
    }
}
