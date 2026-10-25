using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SideIsland.Desktop.Platforms;

/// <summary>AppKit display coordinates are points with a bottom-left origin.</summary>
public sealed record MacNotchDisplay(
    string ScreenId, string Name, double X, double Y, double Width, double Height,
    double Scale, double NotchX, double NotchWidth, double NotchHeight, double BottomInset = 0);

/// <summary>All returned content measurements are local, top-left window points.</summary>
public sealed record MacNotchPlacement(
    string ScreenId, bool HasNotch, double WindowWidth, double WindowHeight,
    double NotchLeft, double NotchWidth, double NotchHeight, double ContentTop);

/// <summary>A frame in native AppKit bottom-left points, without Retina multiplication.</summary>
public readonly record struct MacWindowFrame(double X, double Y, double Width, double Height);

/// <summary>
/// Optional public-AppKit interop. Native geometry stays in AppKit points so
/// mixed-scale and negative-origin monitors never use an invented pixel mapping.
/// This class does not activate, show, hide, or take keyboard focus from a window.
/// </summary>
public static class MacNotchBridge
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const ulong CanJoinAllSpaces = 1UL << 0;
    private const ulong Stationary = 1UL << 4;
    private const ulong IgnoresCycle = 1UL << 6;
    private const ulong FullScreenAuxiliary = 1UL << 8;
    private const ulong FullScreenNone = 1UL << 9;
    private const int NormalWindowLevelKey = 4;
    private const int FloatingWindowLevelKey = 5;
    private const int StatusWindowLevelKey = 9;
    private static IntPtr appKitLibrary;
    private static readonly ConditionalWeakTable<Window, NativeWindowPolicy> originalPolicies = new();
    private sealed record NativeWindowPolicy(IntPtr Handle, nuint CollectionBehavior, bool? HidesOnDeactivate);

    public static bool TryGetPreferredDisplay(out MacNotchDisplay display, out string? error,
        string? preferredDisplayId = null)
    {
        display = null!;
        error = null;
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            EnsureMainThread();
            display = SelectDisplay(ReadDisplays(), preferredDisplayId)!;
            return display is not null;
        }
        catch (Exception exception) when (Recoverable(exception))
        {
            error = "macOS notch display query failed: " + exception.GetType().Name;
            return false;
        }
    }

    public static bool TryApply(Window window, bool expanded, bool preferNotch,
        bool stayAcrossSpaces, bool showOverFullscreen, double expandedWidth,
        double expandedHeight, out MacNotchPlacement placement, out string? error,
        string? preferredDisplayId = null)
    {
        placement = null!;
        error = null;
        if (!OperatingSystem.IsMacOS() || !preferNotch) return false;
        try
        {
            ArgumentNullException.ThrowIfNull(window);
            EnsureMainThread();
            var display = SelectDisplay(ReadDisplays(), preferredDisplayId);
            if (display is null) return false;
            var platform = window.TryGetPlatformHandle();
            if (platform is null || platform.Handle == IntPtr.Zero)
            {
                error = "macOS native window is not ready.";
                return false;
            }
            IntPtr nativeWindow = ResolveWindow(platform.Handle, platform.HandleDescriptor);
            if (nativeWindow == IntPtr.Zero)
            {
                error = "The platform handle is neither an NSWindow nor an attached NSView.";
                return false;
            }

            if (!TryCalculatePlacement(display, expanded, expandedWidth, expandedHeight,
                out var target, out _, out error)) return false;
            var frame = new NativeRect { X = target.X, Y = target.Y, Width = target.Width, Height = target.Height };
            double top = display.Y + display.Height;
            RememberPolicy(window, nativeWindow);

            // Keep the framework's explicit size in agreement with AppKit before
            // finally anchoring its native frame. Otherwise a later layout could
            // restore the constructor's old Width/Height over the native result.
            window.Width = target.Width;
            window.Height = target.Height;
            window.CanResize = false;

            // Status-window level includes the menu-bar area. Full-screen support
            // is explicitly opt-in; this does not claim automatic app detection.
            long level = CGWindowLevelForKey(StatusWindowLevelKey);
            SendInteger(nativeWindow, Selector("setLevel:"), (nint)level);
            ulong behavior = Stationary | IgnoresCycle;
            if (stayAcrossSpaces) behavior |= CanJoinAllSpaces;
            behavior |= showOverFullscreen ? FullScreenAuxiliary : FullScreenNone;
            SendUnsigned(nativeWindow, Selector("setCollectionBehavior:"), (nuint)behavior);
            if (Responds(nativeWindow, "setHidesOnDeactivate:"))
                SendBooleanArgument(nativeWindow, Selector("setHidesOnDeactivate:"), false);
            SendFrame(nativeWindow, Selector("setFrame:display:"), frame, true);

            // Read back rather than claim the requested size was accepted. AppKit
            // or the framework can impose min/max dimensions during a resize.
            var actual = ReadRect(nativeWindow, "frame");
            if (!Valid(actual) || actual.Width < display.NotchWidth ||
                Math.Abs(actual.Y + actual.Height - top) > 2 ||
                actual.X > display.NotchX + 1 || actual.X + actual.Width < display.NotchX + display.NotchWidth - 1)
            {
                error = "AppKit did not retain a window frame covering the cutout.";
                return false;
            }
            placement = new MacNotchPlacement(display.ScreenId, true, actual.Width, actual.Height,
                display.NotchX - actual.X, display.NotchWidth, display.NotchHeight,
                expanded ? display.NotchHeight + 12 : 0);
            return true;
        }
        catch (Exception exception) when (Recoverable(exception))
        {
            error = "macOS notch positioning failed: " + exception.GetType().Name;
            return false;
        }
    }

    /// <summary>
    /// Leaves the notch overlay policy without moving or activating the window.
    /// Restore before normal edge placement. The original collection behavior is
    /// kept in an ephemeron table: no native retain or strong window key survives.
    /// </summary>
    public static bool TryRestoreWindowPolicy(Window window, bool topmost, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            ArgumentNullException.ThrowIfNull(window);
            EnsureMainThread();
            if (!originalPolicies.TryGetValue(window, out var original)) return false;
            var platform = window.TryGetPlatformHandle();
            if (platform is null || platform.Handle == IntPtr.Zero)
            {
                error = "macOS native window is not ready for policy restoration.";
                return false;
            }
            var nativeWindow = ResolveWindow(platform.Handle, platform.HandleDescriptor);
            if (nativeWindow == IntPtr.Zero || nativeWindow != original.Handle)
            {
                // A recreated native window has its own framework policy. Never
                // send a message to a borrowed handle from the disposed window.
                originalPolicies.Remove(window);
                error = "The native window changed before notch policy restoration.";
                return false;
            }
            window.Topmost = topmost;
            nint level = CGWindowLevelForKey(topmost ? FloatingWindowLevelKey : NormalWindowLevelKey);
            SendInteger(nativeWindow, Selector("setLevel:"), level);
            SendUnsigned(nativeWindow, Selector("setCollectionBehavior:"), original.CollectionBehavior);
            if (original.HidesOnDeactivate is bool hides && Responds(nativeWindow, "setHidesOnDeactivate:"))
                SendBooleanArgument(nativeWindow, Selector("setHidesOnDeactivate:"), hides);
            if (SendSignedResult(nativeWindow, Selector("level")) != level ||
                SendUnsignedResult(nativeWindow, Selector("collectionBehavior")) != original.CollectionBehavior)
            {
                error = "AppKit did not retain the restored window policy.";
                return false;
            }
            originalPolicies.Remove(window);
            return true;
        }
        catch (Exception exception) when (Recoverable(exception))
        {
            error = "macOS window policy restoration failed: " + exception.GetType().Name;
            return false;
        }
    }

    private static void RememberPolicy(Window window, IntPtr handle)
    {
        if (originalPolicies.TryGetValue(window, out var known))
        {
            if (known.Handle == handle) return;
            originalPolicies.Remove(window);
        }
        bool? hides = Responds(handle, "hidesOnDeactivate") && Responds(handle, "setHidesOnDeactivate:")
            ? SendBoolean(handle, Selector("hidesOnDeactivate")) : null;
        originalPolicies.Add(window, new NativeWindowPolicy(handle,
            SendUnsignedResult(handle, Selector("collectionBehavior")), hides));
    }

    /// <summary>
    /// The exact geometry used by TryApply, without Objective-C calls or a window.
    /// Input and frame use AppKit bottom-left points; placement is window-local
    /// top-left points. Scale is metadata and never a second coordinate multiplier.
    /// </summary>
    public static bool TryCalculatePlacement(MacNotchDisplay? display, bool expanded,
        double expandedWidth, double expandedHeight, out MacWindowFrame frame,
        out MacNotchPlacement placement, out string? error)
    {
        frame = default;
        placement = null!;
        error = null;
        if (display is null || string.IsNullOrWhiteSpace(display.ScreenId)
            || !Finite(display.X, display.Y, display.Width, display.Height, display.Scale,
                display.NotchX, display.NotchWidth, display.NotchHeight, display.BottomInset)
            || display.Width <= 24 || display.Height <= 16 || display.Scale <= 0
            || display.BottomInset < 0 || display.BottomInset >= display.Height
            || !double.IsFinite(display.X + display.Width) || !double.IsFinite(display.Y + display.Height)
            || display.NotchWidth <= 0 || display.NotchWidth >= display.Width / 2
            || display.NotchHeight <= 0 || display.NotchHeight > display.Height / 3
            || display.NotchX < display.X || display.NotchX + display.NotchWidth > display.X + display.Width)
        {
            error = "Invalid macOS cutout display geometry.";
            return false;
        }
        double maximumWidth = display.Width - 24;
        double compactWidth = Math.Min(maximumWidth, display.NotchWidth + 168);
        double maximumHeight = display.Height - display.BottomInset - 16;
        if (compactWidth < display.NotchWidth + 96 || display.NotchHeight + 8 > maximumHeight)
        {
            error = "The cutout leaves insufficient room for compact controls.";
            return false;
        }
        double width = expanded
            ? ClampFinite(expandedWidth, Math.Min(320, maximumWidth), Math.Min(900, maximumWidth), 520)
            : compactWidth;
        width = Math.Max(width, compactWidth);
        double height = expanded
            ? ClampFinite(expandedHeight, Math.Min(360, maximumHeight), Math.Min(1000, maximumHeight), 680)
            : Math.Min(maximumHeight, Math.Max(42, display.NotchHeight + 8));
        height = Math.Max(height, display.NotchHeight + 8);
        double center = display.NotchX + display.NotchWidth / 2;
        double x = Math.Clamp(center - width / 2, display.X, display.X + display.Width - width);
        double localNotch = display.NotchX - x;
        if (localNotch < 48 || width - localNotch - display.NotchWidth < 48)
        {
            error = "The cutout leaves insufficient display pixels beside its controls.";
            return false;
        }
        frame = new MacWindowFrame(x, display.Y + display.Height - height, width, height);
        placement = new MacNotchPlacement(display.ScreenId, true, width, height,
            localNotch, display.NotchWidth, display.NotchHeight, expanded ? display.NotchHeight + 12 : 0);
        return true;
    }

    /// <summary>Descriptors accepted by ResolveWindow; unknown native pointers are never guessed.</summary>
    public static bool IsSupportedHandleDescriptor(string? descriptor) => descriptor is "NSWindow" or "NSView";

    private static MacNotchDisplay? SelectDisplay(IEnumerable<MacNotchDisplay> displays, string? id)
    {
        // Explicitly selecting an external display must not pull the user's
        // window back to a different internal display behind their back.
        return string.IsNullOrWhiteSpace(id)
            ? displays.FirstOrDefault()
            : displays.FirstOrDefault(display => display.ScreenId == id);
    }

    private static List<MacNotchDisplay> ReadDisplays()
    {
        var results = new List<MacNotchDisplay>();
        var screenClass = objc_getClass("NSScreen");
        if (screenClass == IntPtr.Zero) throw new InvalidOperationException("NSScreen unavailable.");
        var screens = Send(screenClass, Selector("screens"));
        nuint count = SendUnsignedResult(screens, Selector("count"));
        for (nuint index = 0; index < count && index < 64; index++)
        {
            var screen = SendIndex(screens, Selector("objectAtIndex:"), index);
            if (screen == IntPtr.Zero || !Responds(screen, "safeAreaInsets") ||
                !Responds(screen, "auxiliaryTopLeftArea") || !Responds(screen, "auxiliaryTopRightArea")) continue;
            var frame = ReadRect(screen, "frame");
            var inset = ReadInsets(screen, "safeAreaInsets");
            if (!Valid(frame) || !double.IsFinite(inset.Top) || inset.Top <= 0 || inset.Top > frame.Height / 3) continue;
            var left = ReadRect(screen, "auxiliaryTopLeftArea");
            var right = ReadRect(screen, "auxiliaryTopRightArea");
            if (!Valid(left) || !Valid(right)) continue;
            double notchLeft = left.X + left.Width;
            double notchRight = right.X;
            double width = notchRight - notchLeft;
            if (!double.IsFinite(width) || width <= 0 || width >= frame.Width / 2 ||
                notchLeft < frame.X || notchRight > frame.X + frame.Width ||
                Math.Abs(left.Y + left.Height - frame.Y - frame.Height) > 2 ||
                Math.Abs(right.Y + right.Height - frame.Y - frame.Height) > 2) continue;
            double scale = Responds(screen, "backingScaleFactor") ? SendDouble(screen, Selector("backingScaleFactor")) : 1;
            if (!double.IsFinite(scale) || scale <= 0) scale = 1;
            var visible = ReadRect(screen, "visibleFrame");
            double bottomInset = Valid(visible) ? Math.Clamp(visible.Y - frame.Y, 0, frame.Height) : 0;
            string id = DisplayId(screen, index);
            string name = Responds(screen, "localizedName") ? StringValue(Send(screen, Selector("localizedName"))) : "Mac display";
            results.Add(new MacNotchDisplay(id, name, frame.X, frame.Y, frame.Width, frame.Height,
                scale, notchLeft, width, inset.Top, bottomInset));
        }
        return results;
    }

    private static string DisplayId(IntPtr screen, nuint fallback)
    {
        var description = Send(screen, Selector("deviceDescription"));
        var key = SendCString(objc_getClass("NSString"), Selector("stringWithUTF8String:"), "NSScreenNumber");
        var number = SendPointerArgument(description, Selector("objectForKey:"), key);
        return number != IntPtr.Zero
            ? SendUInt32(number, Selector("unsignedIntValue")).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "screen-" + fallback.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IntPtr ResolveWindow(IntPtr handle, string? descriptor)
    {
        if (handle == IntPtr.Zero || !IsSupportedHandleDescriptor(descriptor)) return IntPtr.Zero;
        var windowClass = objc_getClass("NSWindow");
        if (descriptor == "NSWindow" && SendClassCheck(handle, Selector("isKindOfClass:"), windowClass)) return handle;
        if (descriptor == "NSView" && SendClassCheck(handle, Selector("isKindOfClass:"), objc_getClass("NSView")))
        {
            var window = Send(handle, Selector("window"));
            if (window != IntPtr.Zero && SendClassCheck(window, Selector("isKindOfClass:"), windowClass)) return window;
        }
        return IntPtr.Zero;
    }

    private static void EnsureMainThread()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.X64))
            throw new PlatformNotSupportedException("A 64-bit Apple Silicon or Intel process is required.");
        if (appKitLibrary == IntPtr.Zero) appKitLibrary = NativeLibrary.Load(AppKit);
        var threadClass = objc_getClass("NSThread");
        if (threadClass == IntPtr.Zero || !SendBoolean(threadClass, Selector("isMainThread")))
            throw new InvalidOperationException("AppKit access requires the main thread.");
    }

    private static bool Recoverable(Exception exception) => exception is DllNotFoundException or EntryPointNotFoundException
        or BadImageFormatException or PlatformNotSupportedException or InvalidOperationException or ArgumentException;
    private static bool Valid(NativeRect rectangle) => double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y)
        && double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height) && rectangle.Width > 0 && rectangle.Height > 0;
    private static double ClampFinite(double value, double minimum, double maximum, double fallback)
        => Math.Clamp(double.IsFinite(value) ? value : fallback, minimum, maximum);
    private static bool Finite(params double[] values) => values.All(double.IsFinite);
    private static IntPtr Selector(string name) => sel_registerName(name);
    private static bool Responds(IntPtr instance, string selector) => SendClassCheck(instance, Selector("respondsToSelector:"), Selector(selector));
    private static string StringValue(IntPtr value)
    {
        if (value == IntPtr.Zero) return "Mac display";
        var utf8 = Send(value, Selector("UTF8String"));
        return Marshal.PtrToStringUTF8(utf8) ?? "Mac display";
    }
    private static NativeRect ReadRect(IntPtr instance, string selector)
    {
        var name = Selector(selector);
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64) { SendRectStret(out var result, instance, name); return result; }
        return SendRect(instance, name);
    }
    private static NativeInsets ReadInsets(IntPtr instance, string selector)
    {
        var name = Selector(selector);
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64) { SendInsetsStret(out var result, instance, name); return result; }
        return SendInsets(instance, name);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public double X, Y, Width, Height; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInsets { public double Top, Left, Bottom, Right; }

    [DllImport(ObjC)] private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr self, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendIndex(IntPtr self, IntPtr selector, nuint index);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPointerArgument(IntPtr self, IntPtr selector, IntPtr value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendCString(IntPtr self, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nuint SendUnsignedResult(IntPtr self, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendSignedResult(IntPtr self, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern uint SendUInt32(IntPtr self, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr self, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBoolean(IntPtr self, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendClassCheck(IntPtr self, IntPtr selector, IntPtr value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendBooleanArgument(IntPtr self, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendInteger(IntPtr self, IntPtr selector, nint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendUnsigned(IntPtr self, IntPtr selector, nuint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NativeRect SendRect(IntPtr self, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NativeInsets SendInsets(IntPtr self, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void SendRectStret(out NativeRect result, IntPtr self, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void SendInsetsStret(out NativeInsets result, IntPtr self, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendFrame(IntPtr self, IntPtr selector, NativeRect frame, [MarshalAs(UnmanagedType.I1)] bool display);
    [DllImport(CoreGraphics)] private static extern int CGWindowLevelForKey(int key);
}
