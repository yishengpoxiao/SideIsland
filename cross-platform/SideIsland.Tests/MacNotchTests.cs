using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Tests;

internal static class MacNotchTests
{
    private const BindingFlags Private = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;

    public static void Run()
    {
        checks = 0;
        BasicGeometry(); RealCoordinateMatrix(); RejectedGeometry(); BoundsAndInsets(); InteropContracts(); NonMacShortCircuit();
        Console.WriteLine($"PASS: {checks} Mac notch managed checks; production frame calculation, real camera gaps, NS coordinates, Retina invariance, bottom Dock avoidance, ABI metadata and non-Mac short circuits. No AppKit execution or Mac hardware claim.");
    }

    private static MacNotchDisplay Display(double x = 0, double y = 0, double scale = 2,
        double notchWidth = 185, double notchHeight = 32)
    {
        const double width = 1512, height = 982;
        return new MacNotchDisplay("1", "Synthetic notched display", x, y, width, height,
            scale, x + (width - notchWidth) / 2, notchWidth, notchHeight);
    }

    private static void BasicGeometry()
    {
        var display = Display();
        var compact = Calculate(display, false, 520, 680, out var card);
        Near(compact.Width, 353, "camera width is measured, not replaced with a model constant");
        Near(compact.Height, 42, "compact minimum height");
        Near(compact.Y + compact.Height, 982, "compact attaches to NS frame top");
        Near(card.NotchLeft, 84, "left readable wing");
        Near(card.WindowWidth - card.NotchLeft - card.NotchWidth, 84, "right readable wing");
        Check(card.HasNotch && card.ScreenId == "1" && card.ContentTop == 0, "compact local descriptor");
        var expanded = Calculate(display, true, 520, 680, out var expandedCard);
        Near(expanded.Width, 520, "expanded width"); Near(expanded.Height, 680, "expanded height");
        Near(expanded.Y, 302, "expansion grows down from screen top in NS coordinates");
        Near(expandedCard.ContentTop, 44, "expanded controls start below physical camera");
        Near(compact.X + compact.Width / 2, expanded.X + expanded.Width / 2, "center is preserved through expansion");
    }

    private static void RealCoordinateMatrix()
    {
        foreach (double x in new[] { -3024d, -1512d, 0d, 1920d })
        foreach (double y in new[] { -1117d, 0d, 740d })
        foreach (double width in new[] { 80d, 160d, 185d, 240d, 400d })
        foreach (double inset in new[] { 22d, 28d, 32d, 37d, 44d, 68d })
        foreach (bool expanded in new[] { false, true })
        {
            var screen = Display(x, y, 1, width, inset);
            var frame = Calculate(screen, expanded, 590, 750, out var local);
            Check(frame.X >= screen.X && frame.X + frame.Width <= screen.X + screen.Width + .00001, "negative/global horizontal bounds");
            Check(frame.Y >= screen.Y && frame.Y + frame.Height <= screen.Y + screen.Height + .00001, "negative/global vertical bounds");
            Near(frame.Y + frame.Height, screen.Y + screen.Height, "native top stays anchored at any global origin");
            Near(frame.X + local.NotchLeft, screen.NotchX, "camera maps exactly into the window's local coordinates");
            Near(local.NotchWidth, width, "real camera width persists"); Near(local.NotchHeight, inset, "real top safe inset persists");
            Check(local.NotchLeft >= 48 && local.WindowWidth - local.NotchLeft - local.NotchWidth >= 48, "text and click wings exist outside the camera");
            if (expanded) Check(local.ContentTop > inset && local.ContentTop < frame.Height, "expanded UI is below the actual cutout");
            foreach (double scale in new[] { 1.25d, 1.5d, 2d, 3d })
            {
                var scaled = Calculate(screen with { Scale = scale }, expanded, 590, 750, out var scaledLocal);
                Check(scaled == frame && scaledLocal == local, "Retina is metadata, not a coordinate multiplier");
            }
        }
    }

    private static void RejectedGeometry()
    {
        var valid = Display();
        foreach (var invalid in new MacNotchDisplay?[] {
            null, valid with { ScreenId = "" }, valid with { Scale = 0 }, valid with { Scale = double.NaN },
            valid with { X = double.PositiveInfinity }, valid with { Y = double.NaN }, valid with { Width = -1 },
            valid with { Height = 12 }, valid with { NotchWidth = 0 }, valid with { NotchWidth = 800 },
            valid with { NotchHeight = 0 }, valid with { NotchHeight = 400 }, valid with { NotchHeight = double.NaN },
            valid with { NotchX = -1 }, valid with { NotchX = 1500 }, valid with { NotchX = 10 },
            valid with { BottomInset = -1 }, valid with { BottomInset = 982 }, valid with { BottomInset = double.NaN },
            valid with { X = double.MaxValue, Width = double.MaxValue }
        })
        {
            Check(!MacNotchBridge.TryCalculatePlacement(invalid, false, 520, 680, out var frame, out var placement, out var error)
                && frame == default && placement is null && !string.IsNullOrWhiteSpace(error), "invalid/camera-free displays fail without a fake placement");
        }
        Check(!MacNotchBridge.TryCalculatePlacement(valid with { Width = 220, NotchWidth = 108, NotchX = 56 }, false,
            520, 680, out _, out _, out _), "too little physical side space is rejected");
    }

    private static void BoundsAndInsets()
    {
        var display = Display() with { BottomInset = 72 };
        var huge = Calculate(display, true, double.MaxValue, double.MaxValue, out var local);
        Near(huge.Width, 900, "expanded maximum width"); Near(huge.Height, 894, "bottom Dock constrains expanded height");
        Near(huge.Y, 88, "window stays above the visible Dock plus spacing");
        Check(local.ContentTop < huge.Height, "safe top inset remains reachable");
        var fallback = Calculate(display, true, double.NaN, double.PositiveInfinity, out _);
        Near(fallback.Width, 520, "nonfinite width uses safe default"); Near(fallback.Height, 680, "nonfinite height uses safe default");
        var minimum = Calculate(display, true, -1, 0, out _);
        Near(minimum.Width, 353, "expanded width never shrinks below its camera-safe compact width"); Near(minimum.Height, 360, "expanded minimum height");
        var offset = Display() with { NotchX = 250 };
        var compact = Calculate(offset, false, 520, 680, out var actualGap);
        Near(compact.X + actualGap.NotchLeft, 250, "off-center camera uses its actual position");
        var clamped = Calculate(offset, true, 900, 750, out var clampedGap);
        Near(clamped.X, 0, "wide expansion clamps at display edge");
        Near(clampedGap.NotchLeft, 250, "clamped off-center window reports an asymmetric gap");
        Check(Math.Abs(clampedGap.NotchLeft - (clamped.Width - clampedGap.NotchWidth) / 2) > 1, "caller must use NotchLeft instead of assuming a centered gap");
    }

    private static void InteropContracts()
    {
        var type = typeof(MacNotchBridge);
        var rectangle = type.GetNestedType("NativeRect", BindingFlags.NonPublic)!;
        var insets = type.GetNestedType("NativeInsets", BindingFlags.NonPublic)!;
        Check(Marshal.SizeOf(rectangle) == 32 && Marshal.SizeOf(insets) == 32, "CGFloat4 storage is 32 bytes on supported 64-bit targets");
        foreach (var (field, offset) in new[] { ("X", 0), ("Y", 8), ("Width", 16), ("Height", 24) })
            Check(Marshal.OffsetOf(rectangle, field).ToInt32() == offset, "NSRect field offset " + field);
        foreach (var (field, offset) in new[] { ("Top", 0), ("Left", 8), ("Bottom", 16), ("Right", 24) })
            Check(Marshal.OffsetOf(insets, field).ToInt32() == offset, "NSEdgeInsets field offset " + field);
        foreach (var pair in new[] { ("SendRect", "SendRectStret", rectangle), ("SendInsets", "SendInsetsStret", insets) })
        {
            var direct = type.GetMethod(pair.Item1, Private)!;
            var stret = type.GetMethod(pair.Item2, Private)!;
            Check(direct.GetCustomAttribute<DllImportAttribute>()?.EntryPoint == "objc_msgSend" && direct.ReturnType == pair.Item3
                && direct.GetParameters().Length == 2, "arm64 direct aggregate-return declaration " + pair.Item1);
            Check(stret.GetCustomAttribute<DllImportAttribute>()?.EntryPoint == "objc_msgSend_stret" && stret.ReturnType == typeof(void)
                && stret.GetParameters()[0].IsOut && stret.GetParameters()[0].ParameterType == pair.Item3.MakeByRefType(), "x64 separate stret output declaration " + pair.Item2);
        }
        var boolean = type.GetMethod("SendBoolean", Private)!;
        Check(boolean.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>()?.Value == UnmanagedType.I1, "Objective-C BOOL is one byte");
        var frame = type.GetMethod("SendFrame", Private)!;
        Check(frame.GetParameters()[2].ParameterType == rectangle && frame.GetParameters()[3].GetCustomAttribute<MarshalAsAttribute>()?.Value == UnmanagedType.I1, "frame setter uses aggregate argument plus byte BOOL");
        Check(MacNotchBridge.IsSupportedHandleDescriptor("NSWindow") && MacNotchBridge.IsSupportedHandleDescriptor("NSView"), "both documented Avalonia Mac handle descriptors supported");
        foreach (var invalid in new string?[] { null, "", "HWND", "XID", "NSWindow-other", "NSView\0" })
            Check(!MacNotchBridge.IsSupportedHandleDescriptor(invalid), "unknown native handle descriptor rejected");
        Check(type.GetMethod("EnsureMainThread", Private) is not null, "main-thread prerequisite is present at the native boundary");
        var policies = type.GetField("originalPolicies", Private)!;
        Check(policies.FieldType.IsGenericType && policies.FieldType.GetGenericTypeDefinition() == typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>)
            && policies.FieldType.GetGenericArguments()[0] == typeof(Avalonia.Controls.Window), "saved policy uses ephemeron keys instead of retaining windows");
        var policy = type.GetNestedType("NativeWindowPolicy", BindingFlags.NonPublic)!;
        Check(Array.TrueForAll(policy.GetFields(Private), field => !typeof(Avalonia.Controls.Window).IsAssignableFrom(field.FieldType)), "saved policy does not capture the window through its value");
        Check((int)type.GetField("NormalWindowLevelKey", Private)!.GetRawConstantValue()! == 4
            && (int)type.GetField("FloatingWindowLevelKey", Private)!.GetRawConstantValue()! == 5
            && (int)type.GetField("StatusWindowLevelKey", Private)!.GetRawConstantValue()! == 9, "normal/floating/status use the public CoreGraphics window-level keys");
    }

    private static void NonMacShortCircuit()
    {
        if (OperatingSystem.IsMacOS()) return; // Do not execute AppKit in managed-only tests.
        var field = typeof(MacNotchBridge).GetField("appKitLibrary", Private)!;
        var before = (IntPtr)field.GetValue(null)!;
        Check(!MacNotchBridge.TryGetPreferredDisplay(out var display, out var error) && display is null && error is null, "non-Mac query does not invent a camera");
        Check(!MacNotchBridge.TryApply(null!, false, true, true, false, 520, 680, out var placement, out error)
            && placement is null && error is null, "non-Mac apply exits before window/native/thread access");
        Check(!MacNotchBridge.TryRestoreWindowPolicy(null!, false, out error) && error is null,
            "non-Mac restoration exits before accessing a window or native policy");
        var worker = Task.Run(() => !MacNotchBridge.TryGetPreferredDisplay(out _, out var workerError) && workerError is null).GetAwaiter().GetResult();
        Check(worker && (IntPtr)field.GetValue(null)! == before, "non-Mac worker query does not load AppKit or claim a native main-thread check ran");
    }

    private static MacWindowFrame Calculate(MacNotchDisplay display, bool expanded, double width, double height, out MacNotchPlacement local)
    {
        bool success = MacNotchBridge.TryCalculatePlacement(display, expanded, width, height, out var frame, out local, out var error);
        Check(success && error is null, "valid production placement: " + error);
        return frame;
    }
    private static void Near(double actual, double expected, string message) => Check(Math.Abs(actual - expected) < .00001, message + ": " + actual + " != " + expected);
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("Mac notch: " + message); }
}
