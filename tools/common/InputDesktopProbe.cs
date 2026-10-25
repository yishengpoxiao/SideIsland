using System;
using System.Runtime.InteropServices;
using System.Text;
public static class OwnedInputDesktopProbe {
  [DllImport("user32.dll", SetLastError=true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
  [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, uint bytes, out uint needed);
  [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr handle);
  public static string Read() { var h=OpenInputDesktop(0,false,1); if(h==IntPtr.Zero) return "error:"+Marshal.GetLastWin32Error(); try { var s=new StringBuilder(256); uint n; return GetUserObjectInformation(h,2,s,512,out n)?s.ToString():"error:"+Marshal.GetLastWin32Error(); } finally { CloseDesktop(h); } }
}
