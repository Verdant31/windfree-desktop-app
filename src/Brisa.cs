using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

public static class Brisa
{
    public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1048576 };

    [STAThread]
    public static int Main(string[] args)
    {
        bool created;
        using (var single = new Mutex(true, "Local\\Brisa.Samsung.Escritorio", out created))
        using (var reveal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Brisa.Samsung.Reveal"))
        {
            if (!created) { reveal.Set(); return 0; }
            try
            {
                var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                {
                    e.Handled = true;
                    System.Windows.MessageBox.Show("Não foi possível concluir a ação. Feche e abra o Brisa novamente.", "Brisa");
                };
                var panel = new RemotePanel(args, app);
                var waiter = new Thread(delegate()
                {
                    try { while (true) { reveal.WaitOne(); app.Dispatcher.BeginInvoke(new Action(panel.Reveal)); } }
                    catch (ObjectDisposedException) { }
                });
                waiter.IsBackground = true;
                waiter.Start();
                app.Run(panel.Window);
                panel.Dispose();
                return 0;
            }
            catch
            {
                    System.Windows.MessageBox.Show("O Brisa não pôde iniciar. Mantenha Brisa.exe e as pastas src, libs, runtime, assets e data juntas.", "Brisa");
                return 1;
            }
        }
    }

    public static object Get(IDictionary<string, object> value, string key)
    {
        object found; return value != null && value.TryGetValue(key, out found) ? found : null;
    }
    public static string Text(object value) { return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture); }
    public static double? Number(object value) { return value == null ? (double?)null : Convert.ToDouble(value, CultureInfo.InvariantCulture); }
}

interface IStateBackend : IDisposable
{
    Task<Dictionary<string, object>> Request(string operation, string control, object value);
    void Stop();
}
sealed class LocalBackend : IStateBackend
{
    Process process;
    int sequence;
    readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

    void Start()
    {
        if (process != null && !process.HasExited) return;
        if (process != null) process.Dispose();
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Brisa.Root, "runtime", "python.exe"),
            Arguments = "-u \"" + Path.Combine(Brisa.Root, "src", "local_backend.py") + "\"",
            WorkingDirectory = Brisa.Root, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        info.EnvironmentVariables["PYTHONUTF8"] = "1";
        process = new Process { StartInfo = info };
        process.ErrorDataReceived += delegate { };
        process.Start();
        process.BeginErrorReadLine();
    }

    public async Task<Dictionary<string, object>> Request(string operation, string control, object value)
    {
        await gate.WaitAsync();
        try
        {
            Start();
            int id = ++sequence;
            var request = new Dictionary<string, object> { { "id", id }, { "op", operation } };
            if (control != null) { request["control"] = control; request["value"] = value; }
            await process.StandardInput.WriteLineAsync(Brisa.Json.Serialize(request));
            await process.StandardInput.FlushAsync();
            var read = process.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(read, Task.Delay(45000)) != read)
            {
                Stop();
                throw new InvalidOperationException("O ar demorou a responder. Verifique o Wi-Fi e clique em Reconectar.");
            }
            string line = await read;
            if (line == null) throw new InvalidOperationException("A conexão local foi encerrada. Clique em Reconectar.");
            var result = Brisa.Json.Deserialize<Dictionary<string, object>>(line);
            if (Convert.ToInt32(Brisa.Get(result, "id")) != id) throw new InvalidOperationException("Resposta inesperada. Clique em Reconectar.");
            if (!object.Equals(Brisa.Get(result, "ok"), true)) throw new InvalidOperationException(Brisa.Text(Brisa.Get(result, "error")));
            return (Dictionary<string, object>)Brisa.Get(result, "state");
        }
        finally { gate.Release(); }
    }

    public void Stop()
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(2500)) process.Kill();
            }
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); process = null; }
    }
    public void Dispose() { Stop(); }
}

sealed class Choice
{
    public string Value;
    public string Label;
    public override string ToString() { return Label; }
}

sealed class DeviceCatalog
{
    public readonly List<Dictionary<string,object>> Devices;
    public string Selected;
    readonly string path = Path.Combine(Brisa.Root,"data","devices.json");
    public DeviceCatalog()
    {
        Dictionary<string,object> catalog;
        if(File.Exists(path)) catalog=Brisa.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
        else
        {
            var office=Brisa.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(Brisa.Root,"data","device.json")));
            office["id"]="office"; office["identified"]=true;
            catalog=new Dictionary<string,object>{{"selected_id","office"},{"devices",new object[]{office}}};
        }
        Devices=((IEnumerable)catalog["devices"]).Cast<Dictionary<string,object>>().ToList();
        Selected=Brisa.Text(Brisa.Get(catalog,"selected_id"));
        if(!Devices.Any(d=>Brisa.Text(d["id"])==Selected)) Selected=Brisa.Text(Devices[0]["id"]);
    }
    public Dictionary<string,object> Current { get { return Devices.First(d=>Brisa.Text(d["id"])==Selected); } }
    public string Name { get { return Brisa.Text(Current["name"]); } }
    public void Save()
    {
        var catalog=new Dictionary<string,object>{{"version",2},{"selected_id",Selected},{"devices",Devices}};
        string temp=path+".tmp"; File.WriteAllText(temp,Brisa.Json.Serialize(catalog),new UTF8Encoding(false));
        if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
    }
}

sealed class RemotePanel : IDisposable
{
    public readonly Window Window;
    readonly System.Windows.Application application;
    readonly IStateBackend backend;
    NativeTray tray;
    readonly DeviceCatalog catalog = new DeviceCatalog();
    readonly DispatcherTimer timer, temperatureCommit, restyle;
    readonly Button minus, plus, refresh, connect;
    readonly ToggleButton power, windfree, sleep;
    readonly ComboBox modes, fans, airflow, rooms;
    readonly Slider temperature;
    readonly TextBlock status, room, setpoint, unit, minimum, maximum, modeIcon, fanIcon, airflowIcon;
    readonly System.Windows.Shapes.Path airDrawing, modeSnow;
    readonly System.Windows.Shapes.Ellipse statusDot;
    readonly FrameworkElement content, scroll;
    readonly TranslateTransform airFlow = new TranslateTransform();
    Dictionary<string, object> state;
    bool busy, updating, live, exiting, configuring, draggingTemperature;
    bool placing, placedBelow, airOn, flowing;
    DateTime hiddenAt = DateTime.MinValue;
    readonly double designHeight;
    readonly string capture;
    readonly bool offline;
    readonly bool captureOnly, scrollBottom;

    T Find<T>(string name) where T : class { return Window.FindName(name) as T; }

    public RemotePanel(string[] args, System.Windows.Application app, IStateBackend suppliedBackend = null)
    {
        application = app;
        backend = suppliedBackend ?? new LocalBackend();
        offline = args.Contains("--offline");
        captureOnly = args.Contains("--capture-only");
        scrollBottom = args.Contains("--scroll-bottom");
        int index = Array.IndexOf(args, "--capture");
        if (index >= 0 && index + 1 < args.Length) capture = Path.GetFullPath(args[index + 1]);
        int themeIndex = Array.IndexOf(args, "--theme");
        if (themeIndex >= 0 && themeIndex + 1 < args.Length) Theme.Forced = args[themeIndex + 1];
        using (var stream = File.OpenRead(Path.Combine(Brisa.Root, "src", "Ui.xaml"))) Window = (Window)XamlReader.Load(stream);
        Theme.Load();
        Theme.Apply(Window.Resources);
        int heightIndex = Array.IndexOf(args, "--height");
        int diagnosticHeight;
        if (heightIndex >= 0 && heightIndex + 1 < args.Length && int.TryParse(args[heightIndex + 1], out diagnosticHeight)) Window.Height = Math.Max(380, Math.Min(900, diagnosticHeight));
        designHeight = Window.Height;
        power = Find<ToggleButton>("Power"); minus = Find<Button>("Minus"); plus = Find<Button>("Plus");
        refresh = Find<Button>("Refresh"); connect = Find<Button>("Connect"); windfree = Find<ToggleButton>("WindFree");
        modes = Find<ComboBox>("Modes"); fans = Find<ComboBox>("Fans"); airflow = Find<ComboBox>("Airflow"); rooms = Find<ComboBox>("Rooms");
        RoomChoices();
        sleep = Find<ToggleButton>("Sleep"); temperature = Find<Slider>("Temperature");
        minimum = Find<TextBlock>("Minimum"); maximum = Find<TextBlock>("Maximum");
        status = Find<TextBlock>("Status"); room = Find<TextBlock>("Room"); setpoint = Find<TextBlock>("Setpoint"); unit = Find<TextBlock>("Unit");
        modeIcon = Find<TextBlock>("ModeIcon"); fanIcon = Find<TextBlock>("FanIcon"); airflowIcon = Find<TextBlock>("AirflowIcon");
        modeSnow = Find<System.Windows.Shapes.Path>("ModeSnow"); statusDot = Find<System.Windows.Shapes.Ellipse>("StatusDot");
        airDrawing = Find<System.Windows.Shapes.Path>("AirDrawing"); content = Find<FrameworkElement>("PanelContent"); scroll = Find<FrameworkElement>("MainScroll");
        PaintAir();
        Find<System.Windows.Controls.Image>("Appliance").Source = new BitmapImage(new Uri(Path.Combine(Brisa.Root, "assets", "appliance.png")));
        temperatureCommit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        temperatureCommit.Tick += async delegate { temperatureCommit.Stop(); await CommitTemperature(); };
        temperature.ValueChanged += delegate
        {
            if (updating || !live || busy || offline) return;
            setpoint.Text = temperature.Value.ToString("0", CultureInfo.GetCultureInfo("pt-BR"));
            Enable();
            temperatureCommit.Stop();
            if (!draggingTemperature) temperatureCommit.Start();
        };
        temperature.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(delegate { draggingTemperature = true; temperatureCommit.Stop(); }));
        temperature.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(async delegate { draggingTemperature = false; await CommitTemperature(); }));
        power.Click += async delegate { await Toggle(power, "power"); };
        minus.Click += delegate { if (!busy && live) temperature.Value = Math.Max(temperature.Minimum, temperature.Value - 1); };
        plus.Click += delegate { if (!busy && live) temperature.Value = Math.Min(temperature.Maximum, temperature.Value + 1); };
        windfree.Click += async delegate { await Toggle(windfree, "windfree"); };
        sleep.Click += async delegate { await Toggle(sleep, "sleep"); };
        refresh.Click += async delegate { await Update(); };
        connect.Click += delegate { Configure(); };
        modes.SelectionChanged += async delegate { var item = modes.SelectedItem as Choice; if (!updating && !busy && item != null && !String.IsNullOrEmpty(item.Value)) await Change("mode", item.Value); };
        fans.SelectionChanged += async delegate { var item = fans.SelectedItem as Choice; if (!updating && !busy && item != null && !String.IsNullOrEmpty(item.Value)) await Change("fan", item.Value); };
        airflow.SelectionChanged += async delegate { var item = airflow.SelectedItem as Choice; if (!updating && !busy && item != null && !String.IsNullOrEmpty(item.Value)) await Change("airflow", item.Value); };
        rooms.SelectionChanged += async delegate
        {
            var item=rooms.SelectedItem as Choice;
            if(updating || busy || item==null || item.Value==catalog.Selected) return;
            catalog.Selected=item.Value;
            try { catalog.Save(); } catch { ShowError("Não foi possível salvar o cômodo selecionado. Verifique a permissão da pasta do Brisa."); }
            state=null; live=false; ClearRoom();
            await Run("select","device",item.Value);
        };
        Find<Grid>("Header").MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { if (e.Source is TextBlock || e.Source is Grid) Window.DragMove(); };
        Window.KeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Escape) Hide(); };
        Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!exiting) { e.Cancel = true; Hide(); } };
        Window.Deactivated += delegate { if (!configuring && capture == null) Hide(); };
        Window.IsVisibleChanged += delegate { Flow(); };
        var menu = new List<KeyValuePair<string, Action>>
        {
            new KeyValuePair<string, Action>("Abrir Brisa", Reveal),
            new KeyValuePair<string, Action>("Configurar conexão", delegate { Reveal(); Configure(); }),
            new KeyValuePair<string, Action>(null, null),
            new KeyValuePair<string, Action>("Sair do Brisa", delegate { exiting = true; timer.Stop(); Window.Close(); application.Shutdown(); })
        };
        restyle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        restyle.Tick += delegate { restyle.Stop(); Restyle(); };
        Window.SourceInitialized += delegate
        {
            Theme.Frame(Window);
            Theme.Menus();
            tray=new NativeTray(Window,Theme.TrayIcon(NativeWindow.PrimaryScale()),TrayActivate,menu);
            var source=HwndSource.FromHwnd(new WindowInteropHelper(Window).Handle);
            source.AddHook(delegate(IntPtr h,int msg,IntPtr w,IntPtr l,ref bool handled)
            {
                if(msg==0x02e0) Window.Dispatcher.BeginInvoke(new Action(delegate { if(Window.IsVisible) Place(); }));
                bool colors = msg==0x001A && (w.ToInt64()==0x43 || (l!=IntPtr.Zero && Marshal.PtrToStringUni(l)=="ImmersiveColorSet"));
                if(colors || msg==0x0320 || msg==0x031A || msg==0x007E) { restyle.Stop(); restyle.Start(); }
                return IntPtr.Zero;
            });
            Place();
        };
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += async delegate { if (!busy && live && !offline && !draggingTemperature && !temperatureCommit.IsEnabled) await Update(); };
        Window.ContentRendered += async delegate
        {
            Place();
            if (!offline) await Update();
            else { live = false; SetStatus("Sem conexão · Reconectar", "Critical"); ShowError("Sem conexão. Verifique o Wi-Fi e o IP do ar nas configurações."); Enable(); }
            if (capture != null)
            {
                if (scrollBottom) Find<ScrollViewer>("MainScroll").ScrollToEnd();
                await Task.Delay(400);
                Capture(capture);
            }
            if (captureOnly) { exiting = true; Window.Close(); application.Shutdown(); return; }
            timer.Start();
        };
    }

    void Restyle()
    {
        Theme.Load();
        Theme.Apply(Window.Resources);
        Theme.Frame(Window);
        Theme.Menus();
        PaintAir();
        if (tray != null) tray.Icon = Theme.TrayIcon(NativeWindow.PrimaryScale());
    }

    void Place()
    {
        if(placing) return;
        IntPtr handle=new WindowInteropHelper(Window).Handle;
        if(handle==IntPtr.Zero) return;
        placing=true;
        try
        {
            NativeWindow.Rect anchor;
            if(tray==null || !tray.Bounds(out anchor))
            {
                NativeWindow.Point cursor;
                NativeWindow.GetCursorPos(out cursor);
                anchor=new NativeWindow.Rect{Left=cursor.X,Right=cursor.X+1,Top=cursor.Y,Bottom=cursor.Y+1};
            }
            IntPtr monitor=NativeWindow.MonitorFromRect(ref anchor,2);
            var info=new NativeWindow.Monitor{Size=(uint)Marshal.SizeOf(typeof(NativeWindow.Monitor))};
            if(!NativeWindow.GetMonitorInfo(monitor,ref info)) return;
            double scale=NativeWindow.Scale(monitor);
            Window.Height=Math.Min(designHeight,Math.Max(380,info.Work.Height/scale-24));
            int width=(int)Math.Round(Window.Width*scale), height=(int)Math.Round(Window.Height*scale), gap=(int)Math.Round(12*scale);
            int center=(anchor.Left+anchor.Right)/2;
            int x=Math.Max(info.Work.Left+gap,Math.Min(info.Work.Right-width-gap,center-width/2));
            bool top=info.Work.Top>info.Bounds.Top;
            placedBelow=top;
            int y=top ? info.Work.Top+gap : info.Work.Bottom-height-gap;
            if(info.Work.Left>info.Bounds.Left) { x=info.Work.Left+gap; y=Math.Max(info.Work.Top+gap,Math.Min(info.Work.Bottom-height-gap,anchor.Top-height/2)); }
            if(info.Work.Right<info.Bounds.Right) { x=info.Work.Right-width-gap; y=Math.Max(info.Work.Top+gap,Math.Min(info.Work.Bottom-height-gap,anchor.Top-height/2)); }
            Window.Topmost=true;
            NativeWindow.SetWindowPos(handle,new IntPtr(-1),x,y,width,height,0x10|0x40);
        }
        finally { placing=false; }
    }
    void TrayActivate() { if ((DateTime.UtcNow - hiddenAt).TotalMilliseconds > 300) Reveal(); }
    public void Reveal()
    {
        bool shown = Window.IsVisible;
        Window.Show(); Place(); Window.Activate(); NativeWindow.SetForegroundWindow(new WindowInteropHelper(Window).Handle);
        if (!shown) Entrance();
    }
    void Entrance()
    {
        var move = content.RenderTransform as TranslateTransform;
        if (move != null) move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(placedBelow ? -16 : 16, 0, TimeSpan.FromMilliseconds(333)) { EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 } });
        content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(167)));
    }
    void Hide()
    {
        temperatureCommit.Stop(); draggingTemperature = false;
        if (state != null && !busy)
        {
            double? confirmed = Brisa.Number(Brisa.Get(state, "setpoint"));
            if (confirmed.HasValue)
            {
                updating = true;
                try { temperature.Value = confirmed.Value; setpoint.Text = confirmed.Value.ToString("0.#", CultureInfo.GetCultureInfo("pt-BR")); }
                finally { updating = false; }
            }
        }
        if (Window.IsVisible) hiddenAt = DateTime.UtcNow;
        Window.Hide(); Window.Topmost=false;
    }

    void Enable()
    {
        bool enabled = live && !busy && state != null && !offline;
        power.IsEnabled = enabled && Brisa.Get(state, "power") != null;
        double? current = Brisa.Number(Brisa.Get(state, "setpoint"));
        double? lower = Brisa.Number(Brisa.Get(state, "min")), upper = Brisa.Number(Brisa.Get(state, "max"));
        minus.IsEnabled = enabled && current.HasValue && lower.HasValue && temperature.Value > lower;
        plus.IsEnabled = enabled && current.HasValue && upper.HasValue && temperature.Value < upper;
        modes.IsEnabled = enabled && HasChoices(modes);
        fans.IsEnabled = enabled && HasChoices(fans);
        windfree.IsEnabled = enabled && object.Equals(Brisa.Get(state, "windfree_supported"), true);
        sleep.IsEnabled = enabled && object.Equals(Brisa.Get(state, "sleep_supported"), true);
        airflow.IsEnabled = enabled && HasChoices(airflow);
        temperature.IsEnabled = enabled && current.HasValue && lower.HasValue && upper.HasValue;
        refresh.IsEnabled = !busy && !offline;
        connect.IsEnabled = !busy && !offline;
        rooms.IsEnabled = !busy && !offline;
        connect.ToolTip = "Configurar " + catalog.Name;
        refresh.ToolTip = live ? catalog.Name + " · leitura atualizada. Clique para atualizar." : "Reconectar a " + catalog.Name;
    }

    void RoomChoices()
    {
        updating=true;
        try
        {
            Choices(rooms,catalog.Devices.Select(d=>new Choice{Value=Brisa.Text(d["id"]),Label=Brisa.Text(d["name"])+(object.Equals(Brisa.Get(d,"identified"),false)?" · identificar":"")}),catalog.Selected);
            Window.Title="Brisa — " + catalog.Name;
        }
        finally { updating=false; }
    }
    void ClearRoom()
    {
        updating=true;
        try
        {
            Window.Title="Brisa — " + catalog.Name;
            setpoint.Text="—"; unit.Text=""; room.Text="—"; minimum.Text="—"; maximum.Text="—";
            temperature.Value=temperature.Minimum; Air(false);
            Choices(modes,new Choice[0],""); Choices(fans,new Choice[0],""); Choices(airflow,new Choice[0],"");
            power.IsChecked=false; windfree.IsChecked=false; sleep.IsChecked=false;
            Glyphs();
            Find<Border>("ErrorPanel").Visibility=Visibility.Collapsed;
            if(tray!=null) tray.Text="Brisa — " + catalog.Name;
        }
        finally { updating=false; }
    }

    static string Label(string value)
    {
        switch (value)
        {
            case "Auto": return "Auto"; case "Cool": return "Resfriar";
            case "Dry": return "Secar"; case "Wind": case "Fan": return "Ventilar";
            case "Heat": return "Aquecer"; case "Low": return "Baixa";
            case "Mid": return "Média"; case "High": return "Alta";
            case "Fix": return "Fixo"; case "Up_And_Low": return "Vertical";
            case "Left_And_Right": return "Horizontal"; case "All": return "Ambos"; default: return value;
        }
    }

    void Glyphs()
    {
        var mode = modes.SelectedItem as Choice;
        string glyph;
        switch (mode == null ? "" : mode.Value)
        {
            case "Heat": glyph = ""; break; case "Dry": glyph = ""; break;
            case "Wind": case "Fan": glyph = ""; break; case "Auto": glyph = ""; break;
            default: glyph = ""; break;
        }
        modeIcon.Text = glyph;
        modeSnow.Visibility = glyph.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var fan = fans.SelectedItem as Choice;
        switch (fan == null ? "" : fan.Label)
        {
            case "Baixa": fanIcon.Text = ""; break; case "Alta": case "Turbo": fanIcon.Text = ""; break;
            default: fanIcon.Text = ""; break;
        }
        var flow = airflow.SelectedItem as Choice;
        switch (flow == null ? "" : flow.Value)
        {
            case "Up_And_Low": airflowIcon.Text = ""; break; case "Left_And_Right": airflowIcon.Text = ""; break;
            case "All": airflowIcon.Text = ""; break; default: airflowIcon.Text = ""; break;
        }
    }

    void PaintAir()
    {
        var color = Theme.Air;
        var stroke = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(0, 1), SpreadMethod = GradientSpreadMethod.Repeat, RelativeTransform = airFlow };
        stroke.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(70, color.R, color.G, color.B), 0));
        stroke.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(220, color.R, color.G, color.B), 0.5));
        stroke.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(70, color.R, color.G, color.B), 1));
        airDrawing.Stroke = stroke;
    }
    void Air(bool on)
    {
        if (on == airOn) return;
        airOn = on;
        airDrawing.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(400)));
        Flow();
    }
    void Flow()
    {
        bool run = airOn && Window.IsVisible;
        if (run == flowing) return;
        flowing = run;
        if (!run) { airFlow.BeginAnimation(TranslateTransform.YProperty, null); return; }
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(2.6)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(animation, 30);
        airFlow.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    void SetStatus(string text, string tone)
    {
        status.Text = text;
        statusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, tone);
    }

    static bool HasChoices(ComboBox box) { return box.Items.Cast<Choice>().Any(item => !String.IsNullOrEmpty(item.Value)); }

    void Choices(ComboBox box, IEnumerable<Choice> options, string selected)
    {
        box.Items.Clear();
        foreach (var option in options) box.Items.Add(option);
        box.SelectedItem = box.Items.Cast<Choice>().FirstOrDefault(item => item.Value == selected);
        if (box.SelectedItem == null) { box.Items.Add(new Choice { Value = "", Label = "—" }); box.SelectedIndex = box.Items.Count - 1; }
    }

    void Render(Dictionary<string, object> next)
    {
        state = next; live = true; updating = true;
        try
        {
            string suffix = Brisa.Text(Brisa.Get(state, "unit")) == "F" ? "°F" : "°C";
            double? target = Brisa.Number(Brisa.Get(state, "setpoint"));
            double? ambient = Brisa.Number(Brisa.Get(state, "room"));
            setpoint.Text = target.HasValue ? target.Value.ToString("0.#", CultureInfo.GetCultureInfo("pt-BR")) : "—";
            unit.Text = target.HasValue ? suffix : "";
            room.Text = ambient.HasValue ? ambient.Value.ToString("0.#", CultureInfo.GetCultureInfo("pt-BR")) + " " + suffix : "—";
            bool on = object.Equals(Brisa.Get(state, "power"), true);
            power.IsChecked = on;
            power.ToolTip = (on ? "Desligar " : "Ligar ") + catalog.Name;
            System.Windows.Automation.AutomationProperties.SetName(power, Brisa.Text(power.ToolTip));
            Air(on);
            double? lower = Brisa.Number(Brisa.Get(state, "min")), upper = Brisa.Number(Brisa.Get(state, "max"));
            if (lower.HasValue && upper.HasValue && upper > lower) { temperature.Maximum = upper.Value; temperature.Minimum = lower.Value; }
            if (target.HasValue) temperature.Value = target.Value;
            minimum.Text = lower.HasValue ? lower.Value.ToString("0.#") + "°" : "—";
            maximum.Text = upper.HasValue ? upper.Value.ToString("0.#") + "°" : "—";
            var modeValues = (Brisa.Get(state, "modes") as IEnumerable) ?? new object[0];
            Choices(modes, modeValues.Cast<object>().Select(item => new Choice { Value = Brisa.Text(item), Label = Label(Brisa.Text(item)) }), Brisa.Text(Brisa.Get(state, "mode")));
            var fanValues = (Brisa.Get(state, "fans") as IEnumerable) ?? new object[0];
            Choices(fans, fanValues.Cast<Dictionary<string, object>>().Select(item => new Choice { Value = Brisa.Text(Brisa.Get(item, "value")), Label = Label(Brisa.Text(Brisa.Get(item, "label"))) }), Brisa.Text(Brisa.Get(state, "fan")));
            bool wind = object.Equals(Brisa.Get(state, "windfree"), true);
            windfree.IsChecked = wind;
            windfree.ToolTip = object.Equals(Brisa.Get(state, "windfree_supported"), true) ? "WindFree " + (wind ? "ativado" : "desativado") + ": refrigeração sem vento direto" : "WindFree indisponível neste aparelho";
            bool sleeping = object.Equals(Brisa.Get(state, "sleep"), true);
            sleep.IsChecked = sleeping;
            sleep.ToolTip = object.Equals(Brisa.Get(state, "sleep_supported"), true) ? "Modo sono " + (sleeping ? "ativado" : "desativado") : "Modo sono indisponível neste aparelho";
            var airValues = (Brisa.Get(state, "airflows") as IEnumerable) ?? new object[0];
            Choices(airflow, airValues.Cast<object>().Select(item => new Choice { Value = Brisa.Text(item), Label = Label(Brisa.Text(item)) }), Brisa.Text(Brisa.Get(state, "airflow")));
            Glyphs();
            SetStatus("Conectado", "Success");
            status.ToolTip = catalog.Name + " · " + (on ? "ligado" : "desligado") + " · leitura às " + DateTime.Now.ToString("HH:mm:ss");
            Find<Border>("ErrorPanel").Visibility = Visibility.Collapsed;
            if(tray!=null) tray.Text = catalog.Name + " · " + (target.HasValue ? setpoint.Text + suffix : "Brisa") + " · " + (on ? "ligado" : "desligado");
        }
        finally { updating = false; }
    }

    async Task Run(string operation, string control, object value)
    {
        if (busy || offline) return;
        temperatureCommit.Stop();
        bool quiet = operation == "state" && live && state != null;
        busy = true;
        if(operation=="set" && state!=null)
        {
            var confirmed=state;
            var preview=Brisa.Json.Deserialize<Dictionary<string,object>>(Brisa.Json.Serialize(state));
            preview[control=="temperature"?"setpoint":control]=value;
            Render(preview); state=confirmed;
        }
        if (quiet) { scroll.IsHitTestVisible = false; status.Text = "Atualizando…"; }
        else { Enable(); SetStatus(operation == "set" ? "Confirmando alteração…" : "Conectando a " + catalog.Name + "…", "Caution"); }
        try
        {
            var next=await backend.Request(operation,control,value);
            string id=Brisa.Text(Brisa.Get(next,"device_id"));
            if(id.Length>0 && id!=catalog.Selected) throw new InvalidOperationException("A resposta pertence a outro aparelho. Selecione o cômodo novamente.");
            Render(next);
        }
        catch (Exception error)
        {
            if (state != null) Render(state);
            live = false;
            string message = error is InvalidOperationException ? error.Message : "Não foi possível conectar ao ar. Verifique o Wi-Fi e clique em Reconectar.";
            if (state != null) message += "\nOs valores exibidos são da última leitura.";
            ShowError(message);
            SetStatus("Sem conexão · Reconectar", "Critical");
            if(tray!=null) tray.Text = "Brisa — " + catalog.Name + " sem conexão";
        }
        finally { busy = false; scroll.IsHitTestVisible = true; Enable(); }
    }
    Task Update() { return Run("state", null, null); }
    Task Change(string control, object value) { return Run("set", control, value); }
    async Task Toggle(ToggleButton button, string control)
    {
        bool current = object.Equals(Brisa.Get(state, control), true);
        button.IsChecked = current;
        if (state != null) await Change(control, !current);
    }

    async Task CommitTemperature()
    {
        if (updating || busy || !live || offline || state == null || !Window.IsVisible) return;
        double? confirmed = Brisa.Number(Brisa.Get(state, "setpoint"));
        if (confirmed.HasValue && Math.Abs(temperature.Value - confirmed.Value) > 0.01) await Change("temperature", temperature.Value);
    }
    void ShowError(string message) { Find<TextBlock>("ErrorText").Text = message; Find<Border>("ErrorPanel").Visibility = Visibility.Visible; }

    void Configure()
    {
        if (busy || offline) return;
        temperatureCommit.Stop();
        var dialog=BuildSettings();
        bool saved;
        configuring = true;
        try { saved = dialog.ShowDialog() == true; }
        finally { configuring = false; }
        if (saved)
        {
            RoomChoices(); live = false; backend.Stop(); Enable();
            Window.Dispatcher.BeginInvoke(new Action(async delegate { await Update(); }));
        }
        Reveal();
    }

    internal Window BuildSettings()
    {
        Window dialog;
        using(var stream=File.OpenRead(Path.Combine(Brisa.Root,"src","Settings.xaml"))) dialog=(Window)XamlReader.Load(stream);
        dialog.Resources.MergedDictionaries.Add(Window.Resources);
        dialog.Owner=Window; dialog.WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var config=catalog.Current;
        dialog.Title="Brisa — " + catalog.Name;
        var name=(TextBox)dialog.FindName("NameField"); var ipField=(TextBox)dialog.FindName("IpField");
        name.Text=catalog.Name; ipField.Text=Brisa.Text(config["ip"]);
        ((TextBlock)dialog.FindName("IdentityHint")).Text=object.Equals(Brisa.Get(config,"identified"),false) ? "Este ar pode ser Quarto ou Gourmet. Identifique o aparelho e dê o nome do cômodo abaixo." : "Configurações de " + catalog.Name;
        dialog.SourceInitialized+=delegate { Theme.Frame(dialog); };
        dialog.ContentRendered+=delegate { name.Focus(); name.SelectAll(); };
        ((Grid)dialog.FindName("DialogHeader")).MouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e) { if(e.Source is TextBlock || e.Source is Grid) dialog.DragMove(); };
        ((Button)dialog.FindName("Dismiss")).Click+=delegate { dialog.DialogResult=false; };
        ((Button)dialog.FindName("Cancel")).Click+=delegate { dialog.DialogResult=false; };
        ((Button)dialog.FindName("Save")).Click+=delegate
        {
            var error=(TextBlock)dialog.FindName("Validation");
            string label=name.Text.Trim(); System.Net.IPAddress address;
            if(label.Length<1) { error.Text="Informe o nome do cômodo."; name.Focus(); return; }
            if(!System.Net.IPAddress.TryParse(ipField.Text.Trim(),out address) || address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork)
            { error.Text="Informe um IPv4 da sua rede Wi-Fi."; ipField.Focus(); return; }
            byte[] bytes=address.GetAddressBytes();
            if(!(bytes[0]==10 || bytes[0]==192 && bytes[1]==168 || bytes[0]==172 && bytes[1]>=16 && bytes[1]<=31))
            { error.Text="Use o endereço IPv4 local do aparelho, como 192.168.1.10."; ipField.Focus(); return; }
            object oldName=config["name"],oldIp=config["ip"],oldIdentified=Brisa.Get(config,"identified");
            config["name"]=label; config["ip"]=address.ToString();
            if(label!=Brisa.Text(oldName)) config["identified"]=true;
            try { catalog.Save(); dialog.DialogResult=true; }
            catch
            {
                config["name"]=oldName; config["ip"]=oldIp; config["identified"]=oldIdentified;
                error.Text="Não foi possível salvar. Verifique a permissão da pasta do Brisa.";
            }
        };
        return dialog;
    }

    internal static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var presentation=PresentationSource.FromVisual(window);
        double scale=presentation==null ? 1 : presentation.CompositionTarget.TransformToDevice.M11;
        var bounds=new Rect(0,0,window.ActualWidth,window.ActualHeight);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width*scale), (int)Math.Ceiling(bounds.Height*scale), 96*scale, 96*scale, PixelFormats.Pbgra32);
        var visual=new DrawingVisual();
        using(var drawing=visual.RenderOpen())
        {
            drawing.PushClip(new RectangleGeometry(bounds,8,8));
            drawing.DrawRectangle(new SolidColorBrush(Theme.Solid),null,bounds);
            drawing.DrawRectangle(new VisualBrush(window){Stretch=Stretch.None,AlignmentX=AlignmentX.Left,AlignmentY=AlignmentY.Top},null,bounds);
            drawing.Pop();
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
    }
    void Capture(string path) { Capture(Window, path); }
    public void Dispose() { timer.Stop(); temperatureCommit.Stop(); restyle.Stop(); if(tray!=null) tray.Dispose(); backend.Dispose(); }
}
