using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CodexIsland
{
    internal sealed class HotkeyRegistration : IDisposable
    {
        public const uint Alt = 0x0001, Control = 0x0002, Shift = 0x0004, NoRepeat = 0x4000;
        private const int HotkeyMessage = 0x0312;
        private readonly Window window;
        private readonly HwndSource source;
        private readonly IntPtr handle;
        private readonly Func<int, uint, uint, bool> registerKey;
        private readonly Action<int> unregisterKey;
        private readonly Dictionary<int, Action> actions = new Dictionary<int, Action>();
        private readonly Dictionary<string, HotkeyChord> configured = new Dictionary<string, HotkeyChord>();
        private Action<string> configuredAction;
        private int nextId = 0x4900;
        private bool disposed;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        public HotkeyRegistration(Window owner) : this(owner, null, null) { }

        internal HotkeyRegistration(Window owner, Func<int, uint, uint, bool> register, Action<int> unregister)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            owner.Dispatcher.VerifyAccess();
            window = owner;
            handle = new WindowInteropHelper(owner).EnsureHandle();
            registerKey = register ?? ((id, modifiers, key) => RegisterHotKey(handle, id, modifiers, key));
            unregisterKey = unregister ?? (id => { UnregisterHotKey(handle, id); });
            source = HwndSource.FromHwnd(handle);
            if (source != null) source.AddHook(WindowMessage);
            window.Closed += OnWindowClosed;
        }

        // Use after the owner is loaded. False means the shortcut is occupied
        // or unavailable; callers should keep the equivalent visible button.
        public bool Add(uint modifiers, uint key, Action action)
        {
            window.Dispatcher.VerifyAccess();
            if (action == null) throw new ArgumentNullException("action");
            if (disposed || source == null || nextId > 0xBFFF || key == 0 || key > 0xFF) return false;
            int id = nextId++;
            if (!registerKey(id, modifiers | NoRepeat, key)) return false;
            actions.Add(id, action);
            return true;
        }

        internal bool Replace(Dictionary<string, HotkeyChord> bindings, Action<string> action, out string error)
        {
            window.Dispatcher.VerifyAccess(); error = "";
            if (disposed || source == null) { error = "快捷键暂不可用。"; return false; }
            var previous = new Dictionary<string, HotkeyChord>(configured); var previousAction = configuredAction;
            ClearRegistrations();
            bool success = RegisterSet(bindings, action);
            if (!success)
            {
                ClearRegistrations(); bool restored = RegisterSet(previous, previousAction);
                error = restored ? "快捷键被其他应用占用，已保留原快捷键。" : "快捷键被其他应用占用，原快捷键恢复失败；可继续使用托盘和按钮。";
                return false;
            }
            return true;
        }

        private bool RegisterSet(Dictionary<string, HotkeyChord> bindings, Action<string> action)
        {
            foreach (var binding in bindings)
            {
                string key = binding.Key; HotkeyChord chord = binding.Value;
                if (!Add(chord.Modifiers, chord.Key, () => { if (action != null) action(key); })) return false;
            }
            configured.Clear(); foreach (var binding in bindings) configured.Add(binding.Key, binding.Value); configuredAction = action; return true;
        }

        private void ClearRegistrations()
        {
            foreach (int id in actions.Keys) unregisterKey(id);
            actions.Clear();
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            Action action;
            if (!disposed && message == HotkeyMessage && actions.TryGetValue(wParam.ToInt32(), out action))
            {
                handled = true;
                // Returning before invoking the UI action avoids reentrancy in
                // the native message hook (for example when opening a dialog).
                window.Dispatcher.BeginInvoke(new Action(delegate { if (!disposed) action(); }));
            }
            return IntPtr.Zero;
        }

        private void OnWindowClosed(object sender, EventArgs e) { Dispose(); }

        public void Dispose()
        {
            window.Dispatcher.VerifyAccess();
            if (disposed) return;
            disposed = true;
            ClearRegistrations(); configured.Clear();
            if (source != null && !source.IsDisposed) source.RemoveHook(WindowMessage);
            window.Closed -= OnWindowClosed;
        }
    }
}
