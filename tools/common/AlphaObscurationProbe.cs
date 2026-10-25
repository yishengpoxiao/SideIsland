using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class AlphaObscurationProbe {
  [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
  [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L,T,R,B; }
  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h,uint f);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder s,int n);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out Rect r);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int n);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h,int a,out int v,int n);
  [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint a,bool i,uint p);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h,uint f,StringBuilder s,ref uint n);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  public sealed class Info { public string Sample,Handle,Root,Class,ProcessName,Image; public uint ProcessId; public bool Visible; public int Left,Top,Right,Bottom,ExtendedStyle,Cloaked,CloakedResult; }
  public static Info At(int x,int y) { return Read(WindowFromPoint(new Point {X=x,Y=y}),x+","+y); }
  public static Info Foreground() { return Read(GetForegroundWindow(),"foreground"); }
  static Info Read(IntPtr h,string sample) { var r=new Info { Sample=sample,Handle="0x"+h.ToInt64().ToString("X"),Root="0x"+GetAncestor(h,2).ToInt64().ToString("X") }; var s=new StringBuilder(256); GetClassName(h,s,s.Capacity); r.Class=s.ToString(); uint p;GetWindowThreadProcessId(h,out p);r.ProcessId=p;try {r.ProcessName=Process.GetProcessById((int)p).ProcessName;}catch{} var ph=OpenProcess(0x1000,false,p); if(ph!=IntPtr.Zero)try{var name=new StringBuilder(32768);uint n=(uint)name.Capacity;if(QueryFullProcessImageName(ph,0,name,ref n))r.Image=name.ToString();}finally{CloseHandle(ph);} Rect b;if(GetWindowRect(h,out b)){r.Left=b.L;r.Top=b.T;r.Right=b.R;r.Bottom=b.B;}r.Visible=IsWindowVisible(h);r.ExtendedStyle=GetWindowLong(h,-20);int cloak;r.CloakedResult=DwmGetWindowAttribute(h,14,out cloak,4);r.Cloaked=cloak;return r; }
}
