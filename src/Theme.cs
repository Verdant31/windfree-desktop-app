using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Draw = System.Drawing;

static class Theme
{
    public const string Snowflake = "M 8 1 L 8 15 M 14.06 4.5 L 1.94 11.5 M 14.06 11.5 L 1.94 4.5 M 8 3.6 L 9.84 1.76 M 8 3.6 L 6.16 1.76 M 11.81 5.8 L 14.32 6.47 M 11.81 5.8 L 12.48 3.29 M 11.81 10.2 L 12.48 12.71 M 11.81 10.2 L 14.32 9.53 M 8 12.4 L 6.16 14.24 M 8 12.4 L 9.84 14.24 M 4.19 10.2 L 1.68 9.53 M 4.19 10.2 L 3.52 12.71 M 4.19 5.8 L 3.52 3.29 M 4.19 5.8 L 1.68 6.47";
    public static string Forced;
    public static bool Dark { get; private set; }
    public static bool TaskbarDark { get; private set; }
    public static bool HighContrast { get; private set; }
    public static Color Air { get; private set; }
    public static Color Solid { get; private set; }

    public static void Load()
    {
        HighContrast = SystemParameters.HighContrast;
        Dark = Forced == "dark" || (Forced != "light" && Personalize("AppsUseLightTheme") == 0);
        TaskbarDark = Personalize("SystemUsesLightTheme") == 0;
    }

    static int Personalize(string name)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object value = key == null ? null : key.GetValue(name);
                return value is int ? (int)value : 1;
            }
        }
        catch { return 1; }
    }

    // AccentPalette guarda 8 tons RGBA, do mais claro ao mais escuro; o Windows usa Light2 no tema escuro e Dark1 no claro.
    static Color[] AccentPalette()
    {
        var palette = new[] { C(0xFF99EBFF), C(0xFF60CDFF), C(0xFF00B4FF), C(0xFF0078D4), C(0xFF005FB8), C(0xFF004C87), C(0xFF003D6B) };
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
            {
                var bytes = key == null ? null : key.GetValue("AccentPalette") as byte[];
                if (bytes != null && bytes.Length >= 28)
                    for (int i = 0; i < 7; i++) palette[i] = Color.FromRgb(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2]);
            }
        }
        catch { }
        return palette;
    }

    public static void Apply(ResourceDictionary resources)
    {
        var d = new ResourceDictionary();
        if (HighContrast) HighContrastPalette(d);
        else
        {
            var accents = AccentPalette();
            if (Dark) DarkPalette(d, accents[1]); else LightPalette(d, accents[4]);
        }
        resources.MergedDictionaries.Clear();
        resources.MergedDictionaries.Add(d);
    }

    static void DarkPalette(ResourceDictionary d, Color accent)
    {
        Solid = C(0xFF202020); Air = C(0xFF9CD3F2);
        Put(d, "TextPrimary", 0xFFFFFFFF); Put(d, "TextSecondary", 0xC5FFFFFF); Put(d, "TextTertiary", 0x87FFFFFF); Put(d, "TextDisabled", 0x5DFFFFFF);
        Put(d, "TextOnAccent", 0xFF000000); Put(d, "OnStatus", 0xFF000000);
        d["AccentFill"] = Paint(accent); Put(d, "AccentHover", 0x1A000000);
        d["AccentBorder"] = Elevation(C(0x14FFFFFF), C(0x23000000), 0.33, 3);
        Put(d, "ControlFill", 0x0FFFFFFF); Put(d, "ControlStroke", 0x12FFFFFF); Put(d, "ControlStrokeStrong", 0x8BFFFFFF);
        d["ControlBorder"] = Elevation(C(0x12FFFFFF), C(0x18FFFFFF), 0.33, 3);
        d["TextBoxBorder"] = Elevation(C(0x12FFFFFF), C(0x8BFFFFFF), 1, 2);
        d["TextBoxBorderFocused"] = Elevation(C(0x12FFFFFF), accent, 1, 2);
        Put(d, "InputActive", 0xB31E1E1E); Put(d, "SubtleHover", 0x0FFFFFFF); Put(d, "SubtlePressed", 0x0AFFFFFF);
        Put(d, "CardFill", 0x0DFFFFFF); Put(d, "CardStroke", 0x19000000); Put(d, "SolidControl", 0xFF454545);
        Put(d, "FlyoutFill", 0xFF2C2C2C); Put(d, "FlyoutStroke", 0x1AFFFFFF); Put(d, "Divider", 0x15FFFFFF); Put(d, "FooterFill", 0x1A000000);
        Put(d, "FocusOuter", 0xFFFFFFFF); Put(d, "FocusInner", 0xB3000000);
        Put(d, "Success", 0xFF6CCB5F); Put(d, "Caution", 0xFFFCE100); Put(d, "Critical", 0xFFFF99A4); Put(d, "CriticalBackground", 0xFF442726);
        Put(d, "ScrollThumb", 0x8BFFFFFF);
        d["WindowFill"] = Paint(Solid);
    }

    static void LightPalette(ResourceDictionary d, Color accent)
    {
        Solid = C(0xFFF3F3F3); Air = C(0xFF2F86C4);
        Put(d, "TextPrimary", 0xE4000000); Put(d, "TextSecondary", 0x9E000000); Put(d, "TextTertiary", 0x72000000); Put(d, "TextDisabled", 0x5C000000);
        Put(d, "TextOnAccent", 0xFFFFFFFF); Put(d, "OnStatus", 0xFFFFFFFF);
        d["AccentFill"] = Paint(accent); Put(d, "AccentHover", 0x1FFFFFFF);
        d["AccentBorder"] = Elevation(C(0x14FFFFFF), C(0x66000000), 0.33, 3);
        Put(d, "ControlFill", 0xB3FFFFFF); Put(d, "ControlStroke", 0x0F000000); Put(d, "ControlStrokeStrong", 0x72000000);
        d["ControlBorder"] = Elevation(C(0x0F000000), C(0x29000000), 0.33, 3);
        d["TextBoxBorder"] = Elevation(C(0x0F000000), C(0x72000000), 1, 2);
        d["TextBoxBorderFocused"] = Elevation(C(0x0F000000), accent, 1, 2);
        Put(d, "InputActive", 0xFFFFFFFF); Put(d, "SubtleHover", 0x09000000); Put(d, "SubtlePressed", 0x06000000);
        Put(d, "CardFill", 0xB3FFFFFF); Put(d, "CardStroke", 0x0F000000); Put(d, "SolidControl", 0xFFFFFFFF);
        Put(d, "FlyoutFill", 0xFFF9F9F9); Put(d, "FlyoutStroke", 0x1A000000); Put(d, "Divider", 0x14000000); Put(d, "FooterFill", 0x08000000);
        Put(d, "FocusOuter", 0xE4000000); Put(d, "FocusInner", 0xB3FFFFFF);
        Put(d, "Success", 0xFF0F7B0F); Put(d, "Caution", 0xFF9D5D00); Put(d, "Critical", 0xFFC42B1C); Put(d, "CriticalBackground", 0xFFFDE7E9);
        Put(d, "ScrollThumb", 0x72000000);
        d["WindowFill"] = Paint(Solid);
    }

    static void HighContrastPalette(ResourceDictionary d)
    {
        Color window = SystemColors.WindowColor, text = SystemColors.WindowTextColor, highlight = SystemColors.HighlightColor;
        Color highlightText = SystemColors.HighlightTextColor, gray = SystemColors.GrayTextColor;
        Solid = window; Air = text;
        foreach (var key in new[] { "TextPrimary", "TextSecondary", "TextTertiary", "ControlStroke", "ControlStrokeStrong", "ControlBorder", "TextBoxBorder", "CardStroke", "FlyoutStroke", "Divider", "FocusOuter", "Success", "Caution", "Critical", "ScrollThumb" }) d[key] = Paint(text);
        foreach (var key in new[] { "OnStatus", "ControlFill", "InputActive", "CardFill", "SolidControl", "FlyoutFill", "FooterFill", "FocusInner", "CriticalBackground", "WindowFill" }) d[key] = Paint(window);
        d["TextDisabled"] = Paint(gray); d["TextOnAccent"] = Paint(highlightText);
        d["AccentFill"] = Paint(highlight); d["AccentBorder"] = Paint(highlight); d["TextBoxBorderFocused"] = Paint(highlight);
        d["AccentHover"] = Paint(Colors.Transparent);
        d["SubtleHover"] = Paint(Color.FromArgb(0x40, highlight.R, highlight.G, highlight.B));
        d["SubtlePressed"] = Paint(Color.FromArgb(0x26, highlight.R, highlight.G, highlight.B));
    }

    static Brush Elevation(Color top, Color bottom, double edge, double length)
    {
        var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, length), RelativeTransform = new ScaleTransform(1, -1, 0, 0.5) };
        brush.GradientStops.Add(new GradientStop(bottom, edge));
        brush.GradientStops.Add(new GradientStop(top, 1));
        brush.Freeze();
        return brush;
    }

    static Color C(uint argb) { return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb); }
    static SolidColorBrush Paint(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    static void Put(ResourceDictionary d, string key, uint argb) { d[key] = Paint(C(argb)); }

    [StructLayout(LayoutKind.Sequential)] struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr handle, ref Margins margins);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr handle, int index, int value);

    public static void Frame(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        bool backdrop = false;
        int style = GetWindowLong(handle, -16);
        if ((style & 0x80000) != 0) SetWindowLong(handle, -16, style & ~0x80000);
        try
        {
            int dark = Dark && !HighContrast ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref dark, 4);
            int corner = 2;
            DwmSetWindowAttribute(handle, 33, ref corner, 4);
            int border = HighContrast ? unchecked((int)0xFFFFFFFF) : Dark ? 0x00454545 : 0x00DADADA;
            DwmSetWindowAttribute(handle, 34, ref border, 4);
            int type = HighContrast ? 1 : 2;
            backdrop = DwmSetWindowAttribute(handle, 38, ref type, 4) == 0 && !HighContrast;
            if (backdrop) { var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 }; DwmExtendFrameIntoClientArea(handle, ref margins); }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        var source = HwndSource.FromHwnd(handle);
        if (source != null && source.CompositionTarget != null) source.CompositionTarget.BackgroundColor = backdrop ? Colors.Transparent : Solid;
        window.Resources["WindowFill"] = backdrop ? Paint(Colors.Transparent) : Paint(Solid);
    }

    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();

    // O menu da bandeja acompanha o tema da barra de tarefas (API não documentada, usada por apps como o Terminal).
    public static void Menus()
    {
        if (Environment.OSVersion.Version.Build < 18362) return;
        try { SetPreferredAppMode(HighContrast ? 0 : TaskbarDark ? 2 : 3); FlushMenuThemes(); }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
    }

    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    public static Draw.Icon TrayIcon(double scale)
    {
        int size = 16;
        try { size = GetSystemMetricsForDpi(49, (uint)Math.Round(96 * scale)); } catch (EntryPointNotFoundException) { size = (int)Math.Round(16 * scale); }
        if (size < 16) size = 16;
        double unit = size / 16.0, stroke = Math.Max(1, Math.Round(unit)), shift = stroke % 2 == 1 ? -0.5 : 0;
        var geometry = Geometry.Parse(Snowflake).Clone();
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(unit, unit));
        transform.Children.Add(new TranslateTransform(shift, shift));
        geometry.Transform = transform;
        var ink = HighContrast ? SystemColors.WindowTextColor : TaskbarDark ? Colors.White : Colors.Black;
        var pen = new Pen(new SolidColorBrush(ink), stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawGeometry(null, pen, geometry);
        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var pixels = new byte[size * size * 4];
        new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, size * 4, 0);
        using (var bitmap = new Draw.Bitmap(size, size, Draw.Imaging.PixelFormat.Format32bppArgb))
        {
            var data = bitmap.LockBits(new Draw.Rectangle(0, 0, size, size), Draw.Imaging.ImageLockMode.WriteOnly, Draw.Imaging.PixelFormat.Format32bppArgb);
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            bitmap.UnlockBits(data);
            IntPtr handle = bitmap.GetHicon();
            try { return (Draw.Icon)Draw.Icon.FromHandle(handle).Clone(); }
            finally { DestroyIcon(handle); }
        }
    }
}
