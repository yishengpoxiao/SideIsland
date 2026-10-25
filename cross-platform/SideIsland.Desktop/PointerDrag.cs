using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Desktop;

public sealed partial class IslandWindow
{
    private PixelPoint? dragOrigin;
    private Point dragGrip;
    private Size dragSourceSize;
    private int? dragSlot;

    // Keep capture until release. Native move loops may consume the release,
    // leaving hover disabled and the island permanently marked as dragging.
    private void AttachIslandDrag(Control target, int? slot, Func<object?, bool>? accepts = null)
    {
        target.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || accepts?.Invoke(e.Source) == false) return;
            hover.Stop(); collapse.Stop();
            dragGrip = e.GetPosition(this); dragOrigin = this.PointToScreen(dragGrip); dragSourceSize = Bounds.Size; dragSlot = slot;
            e.Pointer.Capture(this); e.Handled = true;
        };
    }

    private void InitializeDragHandlers()
    {
        PointerMoved += (_, e) =>
        {
            if (dragOrigin is not PixelPoint start) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { EndCapturedDrag(e.Pointer); return; }
            var point = this.PointToScreen(e.GetPosition(this));
            if (!dragging)
            {
                var threshold = 7 * RenderScaling;
                if (Math.Pow(point.X - start.X, 2) + Math.Pow(point.Y - start.Y, 2) < threshold * threshold) return;
                hover.Stop(); collapse.Stop();
                if (realNotch) { prefs.PreferNotch = false; MacNotchBridge.TryRestoreWindowPolicy(this, prefs.AlwaysOnTop, out var restoreError); }
                pinned = false; Collapse(); Place();
                dragGrip = new Point(Math.Clamp(dragGrip.X / Math.Max(1, dragSourceSize.Width) * Width, 4, Math.Max(4, Width - 4)),
                    Math.Clamp(dragGrip.Y / Math.Max(1, dragSourceSize.Height) * Height, 4, Math.Max(4, Height - 4)));
                dragging = true;
            }
            Position = new PixelPoint(point.X - (int)Math.Round(dragGrip.X * RenderScaling), point.Y - (int)Math.Round(dragGrip.Y * RenderScaling));
            e.Handled = true;
        };
        PointerReleased += (_, e) =>
        {
            if (dragOrigin is null) return;
            var click = !dragging;
            EndCapturedDrag(e.Pointer);
            if (click && dragSlot is int index) QuickAction(index == 0 ? prefs.Slot1 : prefs.Slot2);
            e.Handled = true;
        };
        PointerCaptureLost += (_, _) =>
        {
            var snap = dragging; dragging = false; dragOrigin = null;
            if (snap && !closing) Snap();
        };
    }

    private void EndCapturedDrag(IPointer pointer)
    {
        var snap = dragging; dragging = false; dragOrigin = null;
        pointer.Capture(null);
        if (snap && !closing) Snap();
    }
}
