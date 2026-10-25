using System;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace CodexIsland
{
    internal static class NativeDialogs
    {
        private sealed class OwnerHandle : Forms.IWin32Window
        {
            private readonly IntPtr handle;
            internal OwnerHandle(IntPtr value) { handle = value; }
            public IntPtr Handle { get { return handle; } }
        }

        internal static Forms.IWin32Window Owner(Window window)
        {
            if (window == null) return null;
            IntPtr handle = new WindowInteropHelper(window).Handle;
            return handle == IntPtr.Zero ? null : new OwnerHandle(handle);
        }

        internal static Forms.DialogResult ShowFolder(Forms.FolderBrowserDialog dialog, Window window)
        {
            var owner = Owner(window);
            return owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        }
    }
}
