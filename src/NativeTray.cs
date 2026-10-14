using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

sealed class NativeTray : IDisposable
{
    const int Callback = 0x8001;
    readonly IntPtr hwnd;
    readonly HwndSource source;
    readonly Window window;
    Icon icon;
    readonly Action activate;
    readonly List<KeyValuePair<string, Action>> menu;
    readonly uint taskbarCreated;
    NotifyData data;
    public bool Registered { get; private set; }
    public int BoundsResult { get; private set; }
    public NativeTray(Window owner, Icon ownedIcon, Action onActivate, List<KeyValuePair<string, Action>> items)
    {
        window = owner;
        hwnd = new WindowInteropHelper(window).Handle;
        source = HwndSource.FromHwnd(hwnd);
        icon = ownedIcon; activate = onActivate; menu = items;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        data = new NotifyData { cbSize = (uint)Marshal.SizeOf(typeof(NotifyData)), hWnd = hwnd, uID = 1,
            uFlags = 1 | 2 | 4 | 128, uCallbackMessage = Callback, hIcon = icon.Handle, szTip = "Brisa", szInfo = "", szInfoTitle = "" };
        source.AddHook(Hook); Add();
    }
    void Add() { Registered=Shell_NotifyIcon(0, ref data); data.uVersion = 4; Shell_NotifyIcon(4, ref data); }
    public string Text { set { data.szTip = value.Length > 127 ? value.Substring(0,127) : value; Shell_NotifyIcon(1,ref data); } }
    public Icon Icon { set { var old = icon; icon = value; data.hIcon = icon.Handle; Shell_NotifyIcon(1, ref data); old.Dispose(); } }
    IntPtr Hook(IntPtr handle, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if ((uint)message == taskbarCreated) Add();
        if (message == Callback)
        {
            int action = (int)(l.ToInt64() & 0xffff);
            int id = (int)((l.ToInt64() >> 16) & 0xffff);
            if (id != 0 && id != data.uID) return IntPtr.Zero;
            if (action == 0x202 || action == 0x203 || action == 0x400 || action == 0x401) { activate(); handled = true; }
            else if (action == 0x205 || action == 0x7b) { ShowMenu(); handled = true; }
        }
        return IntPtr.Zero;
    }
    void ShowMenu()
    {
        IntPtr popup = CreatePopupMenu();
        try
        {
            for (int i = 0; i < menu.Count; i++)
            {
                if (menu[i].Key == null) AppendMenu(popup, 0x800, UIntPtr.Zero, null);
                else AppendMenu(popup, 0, (UIntPtr)(uint)(i + 1), menu[i].Key);
            }
            SetMenuDefaultItem(popup, 1, 0);
            NativeWindow.Point cursor;
            NativeWindow.GetCursorPos(out cursor);
            var area = new NativeWindow.Rect { Left = cursor.X, Top = cursor.Y, Right = cursor.X + 1, Bottom = cursor.Y + 1 };
            var info = new NativeWindow.Monitor { Size = (uint)Marshal.SizeOf(typeof(NativeWindow.Monitor)) };
            bool lower = !NativeWindow.GetMonitorInfo(NativeWindow.MonitorFromRect(ref area, 2), ref info) || cursor.Y > (info.Bounds.Top + info.Bounds.Bottom) / 2;
            uint flags = 0x100 | 0x80 | 0x2 | (lower ? 0x20u : 0u) | (GetSystemMetrics(40) != 0 ? 0x8u : 0u);
            SetForegroundWindow(hwnd);
            int command = TrackPopupMenuEx(popup, flags, cursor.X, cursor.Y, hwnd, IntPtr.Zero);
            PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);
            if (command > 0 && command <= menu.Count && menu[command - 1].Value != null) window.Dispatcher.BeginInvoke(menu[command - 1].Value);
        }
        finally { DestroyMenu(popup); }
    }
    public bool Bounds(out NativeWindow.Rect bounds)
    {
        var identity = new NotifyIdentity { cbSize = (uint)Marshal.SizeOf(typeof(NotifyIdentity)), hWnd = hwnd, uID = 1 };
        BoundsResult=Shell_NotifyIconGetRect(ref identity, out bounds); return BoundsResult == 0;
    }
    public void Dispose() { Shell_NotifyIcon(2,ref data); source.RemoveHook(Hook); icon.Dispose(); }
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
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu,uint flags,UIntPtr id,string text);
    [DllImport("user32.dll")] static extern bool SetMenuDefaultItem(IntPtr menu,uint item,uint byPosition);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu,uint flags,int x,int y,IntPtr owner,IntPtr parameters);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr handle,uint message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
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
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor,int kind,out uint x,out uint y);
    public static double Scale(IntPtr monitor)
    {
        uint x,y; try { if(GetDpiForMonitor(monitor,0,out x,out y)==0) return x/96.0; } catch(EntryPointNotFoundException) {} catch(DllNotFoundException) {}
        return 1;
    }
    public static double PrimaryScale()
    {
        var origin = new Rect { Left = 0, Top = 0, Right = 1, Bottom = 1 };
        return Scale(MonitorFromRect(ref origin, 1));
    }
}
