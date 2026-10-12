using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

sealed class NativeTray : IDisposable
{
    const int Callback = 0x8001;
    readonly IntPtr hwnd;
    readonly HwndSource source;
    readonly Icon icon;
    readonly Action activate;
    readonly Forms.ContextMenuStrip menu;
    readonly uint taskbarCreated;
    NotifyData data;
    public bool Registered { get; private set; }
    public int BoundsResult { get; private set; }
    public NativeTray(Window window, Icon ownedIcon, Action onActivate, Forms.ContextMenuStrip contextMenu)
    {
        hwnd = new WindowInteropHelper(window).Handle;
        source = HwndSource.FromHwnd(hwnd);
        icon = ownedIcon; activate = onActivate; menu = contextMenu;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        data = new NotifyData { cbSize = (uint)Marshal.SizeOf(typeof(NotifyData)), hWnd = hwnd, uID = 1,
            uFlags = 1 | 2 | 4 | 128, uCallbackMessage = Callback, hIcon = icon.Handle, szTip = "Brisa", szInfo = "", szInfoTitle = "" };
        source.AddHook(Hook); Add();
    }
    void Add() { Registered=Shell_NotifyIcon(0, ref data); data.uVersion = 4; Shell_NotifyIcon(4, ref data); }
    public string Text { set { data.szTip = value.Length > 127 ? value.Substring(0,127) : value; Shell_NotifyIcon(1,ref data); } }
    IntPtr Hook(IntPtr handle, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if ((uint)message == taskbarCreated) Add();
        if (message == Callback)
        {
            int action = (int)(l.ToInt64() & 0xffff);
            int id = (int)((l.ToInt64() >> 16) & 0xffff);
            if (id != 0 && id != data.uID) return IntPtr.Zero;
            if (action == 0x202 || action == 0x203 || action == 0x400 || action == 0x401) { activate(); handled = true; }
            else if (action == 0x205 || action == 0x7b)
            {
                SetForegroundWindow(hwnd);
                var cursor = Forms.Cursor.Position; menu.Show(cursor.X, cursor.Y); handled = true;
            }
        }
        return IntPtr.Zero;
    }
    public bool Bounds(out NativeWindow.Rect bounds)
    {
        var identity = new NotifyIdentity { cbSize = (uint)Marshal.SizeOf(typeof(NotifyIdentity)), hWnd = hwnd, uID = 1 };
        BoundsResult=Shell_NotifyIconGetRect(ref identity, out bounds); return BoundsResult == 0;
    }
    public void Dispose() { Shell_NotifyIcon(2,ref data); source.RemoveHook(Hook); menu.Dispose(); icon.Dispose(); }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct NotifyData
    {
        public uint cbSize; public IntPtr hWnd; public uint uID,uFlags,uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string szTip;
        public uint dwState,dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] struct NotifyIdentity { public uint cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint message,ref NotifyData data);
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NotifyIdentity identity,out NativeWindow.Rect rect);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr handle);
}

static class NativeWindow
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; public int Width { get { return Right-Left; } } public int Height { get { return Bottom-Top; } } }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; public Point(int x,int y) { X=x; Y=y; } }
    [StructLayout(LayoutKind.Sequential)] public struct Monitor { public uint Size; public Rect Bounds,Work; public uint Flags; }
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromRect(ref Rect rect,uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor,ref Monitor info);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle,out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr handle,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor,int kind,out uint x,out uint y);
    [DllImport("gdi32.dll")] static extern IntPtr CreatePolygonRgn(Point[] points,int count,int fillMode);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr handle,IntPtr region,bool redraw);
    public static double Scale(IntPtr monitor)
    {
        uint x,y; try { if(GetDpiForMonitor(monitor,0,out x,out y)==0) return x/96.0; } catch(EntryPointNotFoundException) {} catch(DllNotFoundException) {}
        return 1;
    }
    public static void Shape(Window window,Geometry geometry)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        var presentation = PresentationSource.FromVisual(window);
        if(handle==IntPtr.Zero || presentation==null || presentation.CompositionTarget==null) return;
        var transform=presentation.CompositionTarget.TransformToDevice;
        var points=new List<Point>();
        var flattened=geometry.GetFlattenedPathGeometry(0.1,ToleranceType.Absolute);
        Transform shapeTransform=flattened.Transform ?? Transform.Identity;
        foreach(var figure in flattened.Figures)
        {
            var first=transform.Transform(shapeTransform.Transform(figure.StartPoint)); points.Add(new Point((int)Math.Round(first.X),(int)Math.Round(first.Y)));
            foreach(var segment in figure.Segments)
            {
                var line=segment as PolyLineSegment;
                if(line!=null) foreach(var p in line.Points) { var pixel=transform.Transform(shapeTransform.Transform(p)); points.Add(new Point((int)Math.Round(pixel.X),(int)Math.Round(pixel.Y))); }
                var single=segment as LineSegment;
                if(single!=null) { var pixel=transform.Transform(shapeTransform.Transform(single.Point)); points.Add(new Point((int)Math.Round(pixel.X),(int)Math.Round(pixel.Y))); }
            }
        }
        if(points.Count<3) return;
        IntPtr region=CreatePolygonRgn(points.ToArray(),points.Count,2);
        if(region!=IntPtr.Zero && SetWindowRgn(handle,region,true)==0) DeleteObject(region);
    }
}
