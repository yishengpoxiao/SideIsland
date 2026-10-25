using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        internal const int ToolTransitionMilliseconds = 150;
        private UIElement toolTransitionPanel;
        private Stopwatch toolTransitionWatch;
        private bool toolTransitionActive;
        private int toolTransitionVersion;

        private UIElement CurrentToolPanel
        {
            get
            {
                UIElement panel;
                return toolPanels.TryGetValue(selectedTool ?? "", out panel) && IsToolVisible(selectedTool) ? panel : null;
            }
        }

        private void BeginToolTransition(UIElement panel, bool animate)
        {
            StopToolTransition();
            if (!animate || !SystemParameters.ClientAreaAnimation || panel == null || !panel.IsVisible ||
                !toolsPageSelected || !expanded || peeking || visualTransitionActive || pageTransitionActive ||
                !IsLoaded || !IsVisible || Opacity <= 0 || shuttingDown || dragging || resizing) return;
            toolTransitionPanel = panel; toolTransitionWatch = null; toolTransitionActive = true;
            ++toolTransitionVersion;
            // Fade the live tool only. Picker, favorites and header stay clear,
            // and no old text, clipboard or image pixels are retained.
            panel.Opacity = .35;
            CompositionTarget.Rendering += OnToolTransitionRendering;
        }

        private void OnToolTransitionRendering(object sender, EventArgs e)
        {
            if (!toolTransitionActive || toolTransitionPanel == null || shuttingDown || !IsVisible || Opacity <= 0 ||
                !expanded || peeking || !toolsPageSelected || dragging || resizing || visualTransitionActive ||
                pageTransitionActive || !SystemParameters.ClientAreaAnimation || !toolTransitionPanel.IsVisible ||
                !ReferenceEquals(toolTransitionPanel, CurrentToolPanel))
            { StopToolTransition(); return; }
            if (toolTransitionWatch == null) toolTransitionWatch = Stopwatch.StartNew();
            double elapsed = Math.Min(1, toolTransitionWatch.Elapsed.TotalMilliseconds / ToolTransitionMilliseconds);
            toolTransitionPanel.Opacity = .35 + .65 * (1 - Math.Cos(Math.PI * elapsed)) / 2;
            if (elapsed >= 1) StopToolTransition();
        }

        private void StopToolTransition()
        {
            CompositionTarget.Rendering -= OnToolTransitionRendering;
            bool hadTransition = toolTransitionActive || toolTransitionPanel != null || toolTransitionWatch != null;
            if (toolTransitionPanel != null) toolTransitionPanel.Opacity = 1;
            toolTransitionPanel = null; toolTransitionWatch = null; toolTransitionActive = false;
            if (hadTransition) ++toolTransitionVersion;
        }
    }
}
