using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace QuietPin;

internal static class Native
{
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}

public sealed class App : Application
{
    private Forms.NotifyIcon? tray;
    private Mutex? mutex;
    internal MainWindow Inbox = null!;
    internal Store Store = null!;
    private CaptureWindow? capture;
    private SettingsWindow? settings;
    private IntPtr previousWindow;
    private bool closingCapture;
    private bool reopenCapture;
    internal string? ShortcutError;
    internal bool Welcome;
    internal bool Quitting;
    internal static readonly string[] ShortcutLabels = { "Ctrl + Alt + Space", "Ctrl + Shift + Space", "Alt + Shift + Space" };

    [STAThread]
    public static void Main()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        mutex = new Mutex(true, "Local\\QuietPin.Desktop", out var first);
        if (!first) { MessageBox.Show("Aside 已在运行，请点击系统托盘中的图标。", "Aside"); Shutdown(); return; }
        Store = new Store(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietPin"));
        Inbox = new MainWindow(this);
        MainWindow = Inbox;
        Inbox.SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(Inbox).Handle)?.AddHook(WindowMessage);
            RegisterShortcut();
        };
        Inbox.Show();
        // Build the capture window up front, as macOS does, so the first hotkey press does not pay for window creation.
        capture = new CaptureWindow(this);
        new WindowInteropHelper(capture).EnsureHandle();
        System.Drawing.Icon trayIcon;
        try { trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application; }
        catch { trayIcon = System.Drawing.SystemIcons.Application; }
        tray = new Forms.NotifyIcon { Text = "Aside · Capture it. Stay in flow.", Icon = trayIcon, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 Inbox", null, (_, _) => Dispatcher.Invoke(ShowInbox));
        var captureMenu = menu.Items.Add("快速记录", null, (_, _) => Dispatcher.Invoke(ToggleCapture));
        var pinsMenu = menu.Items.Add("仅显示置顶事项", null, (_, _) => Dispatcher.Invoke(() => { Inbox.Show(); Inbox.ChangeMode(true, false); }));
        var stripMenu = menu.Items.Add("收为细条", null, (_, _) => Dispatcher.Invoke(() => { Inbox.Show(); Inbox.ChangeMode(true, true); }));
        var hideMenu = menu.Items.Add("最小化到系统托盘", null, (_, _) => Dispatcher.Invoke(Inbox.HideToTray));
        var topMenu = new Forms.ToolStripMenuItem("窗口置顶");
        topMenu.Click += (_, _) => Dispatcher.Invoke(() => Commit(b => b.Preferences.Topmost = !b.Preferences.Topmost));
        menu.Items.Add(topMenu);
        menu.Opening += (_, _) =>
        {
            var p = Store.Book.Preferences;
            captureMenu.Text = "快速记录    " + (p.CustomKeyLabel ?? ShortcutLabels[Math.Clamp(p.Shortcut, 0, 2)]);
            hideMenu.Enabled = Inbox.IsVisible || Inbox.EdgeHandleVisible;
            topMenu.Checked = Store.Book.Preferences.Topmost;
            ((Forms.ToolStripMenuItem)pinsMenu).Checked = Store.Book.Preferences.Collapsed && !Store.Book.Preferences.StripMode;
            ((Forms.ToolStripMenuItem)stripMenu).Checked = Store.Book.Preferences.StripMode;
        };
        menu.Items.Add("设置…", null, (_, _) => Dispatcher.Invoke(ShowSettings));
        menu.Items.Add("退出 Aside", null, (_, _) => Dispatcher.Invoke(() => { Quitting = true; Shutdown(); }));
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ShowInbox); };
        if (Store.LoadError != null) MessageBox.Show(Store.LoadError, "Aside 记录载入失败");
        // First launch: open Settings with a welcome note so the user knows Aside is running and sets a shortcut.
        else if (Store.FirstLaunch) { Welcome = true; ShowSettings(); }
        Perf.Log($"start: {(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds:F0} ms to ready; OS {Environment.OSVersion.Version}, " +
            $"{RuntimeInformation.OSArchitecture} OS / {RuntimeInformation.ProcessArchitecture} process, render tier {RenderCapability.Tier >> 16}, animations {SystemParameters.ClientAreaAnimation}");
    }

    internal bool Commit(Action<Notebook> action, bool render = true)
    {
        try { Store.Edit(action); if (render) { Inbox.Render(); capture?.ApplyAppearance(); settings?.UpdatePreviews(); } return true; }
        catch (Exception ex) { MessageBox.Show("未能保存，原记录未被覆盖。\n" + ex.Message, "Aside"); return false; }
    }
    internal void ShowInbox() { Inbox.Expand(); Inbox.Show(); Inbox.Activate(); }
    internal void ShowSettings()
    {
        if (settings == null) { settings = new SettingsWindow(this); settings.Closed += (_, _) => settings = null; }
        settings.Refresh();
        settings.Show(); settings.Activate();
    }
    internal void ToggleCapture()
    {
        if (closingCapture) { reopenCapture = true; return; }
        if (capture?.IsVisible == true) { CloseCapture(); return; }
        var started = Perf.Start();
        previousWindow = Native.GetForegroundWindow();
        capture ??= new CaptureWindow(this);
        capture.PositionAtCursor(); capture.PrepareEntrance(); capture.Show(); capture.Activate(); capture.FocusInput(); capture.AnimateIn();
        Perf.Log($"capture open: shown after {Perf.Ms(started)} ms");
        capture.LogPlacement();
        Perf.Frames("capture open", started, 200);
    }
    internal void CloseCapture(bool restoreFocus = true)
    {
        if (closingCapture || capture?.IsVisible != true) return;
        closingCapture = true;
        var started = Perf.Start();
        capture.AnimateOut(() =>
        {
            capture.Hide();
            Perf.Log($"capture close: hidden after {Perf.Ms(started)} ms");
            if (restoreFocus && previousWindow != IntPtr.Zero) Native.SetForegroundWindow(previousWindow);
            previousWindow = IntPtr.Zero;
            closingCapture = false;
            if (reopenCapture) { reopenCapture = false; ToggleCapture(); }
        });
    }
    internal void RegisterShortcut()
    {
        var hwnd = new WindowInteropHelper(Inbox).Handle;
        Native.UnregisterHotKey(hwnd, 1);
        uint[] modifiers = { 0x0003, 0x0006, 0x0005 };
        var index = Math.Clamp(Store.Book.Preferences.Shortcut, 0, 2);
        var p = Store.Book.Preferences;
        ShortcutError = Native.RegisterHotKey(hwnd, 1, (p.CustomModifiers ?? modifiers[index]) | 0x4000, p.CustomKey ?? 0x20) ? null : "快捷键已被占用，请在设置中更换。";
        if (ShortcutError != null) MessageBox.Show(ShortcutError, "Aside");
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == 1) { Perf.Log("hotkey received"); ToggleCapture(); handled = true; }
        return IntPtr.Zero;
    }
    internal Guid? ChooseReplacement(Window owner)
    {
        var dialog = UI.Dialog($"{Store.Book.Preferences.PinCapacity} 条 Pin 已满", 350, 300, owner);
        var stack = new StackPanel { Margin = new Thickness(22) };
        stack.Children.Add(UI.Text("选择一条替换", 18));
        stack.Children.Add(UI.Text("原事项会留在 Inbox。", 12));
        Guid? selected = null;
        foreach (var item in Store.Book.Pins)
            stack.Children.Add(UI.Button(item.Content, () => { selected = item.Id; dialog.DialogResult = true; }));
        stack.Children.Add(UI.Button("取消", () => dialog.DialogResult = false));
        dialog.Content = new ScrollViewer { Content = stack };
        dialog.ShowDialog();
        return selected;
    }
    internal bool Add(Window owner, string text, bool pin)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        Guid? replacement = null;
        if (pin && Store.Book.Pins.Length >= Store.Book.Preferences.PinCapacity)
        {
            replacement = ChooseReplacement(owner);
            if (!replacement.HasValue) return false;
        }
        return Commit(book =>
        {
            var id = book.Add(text);
            if (id.HasValue && pin && !book.Pin(id.Value, replacement)) throw new InvalidOperationException("无法置顶该事项");
        });
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (Inbox != null)
        {
            Inbox.SaveFrame();
            Native.UnregisterHotKey(new WindowInteropHelper(Inbox).Handle, 1);
        }
        tray?.Dispose(); mutex?.Dispose(); base.OnExit(e);
    }
}

// Spring curves matching the macOS build. Use with EasingMode.EaseIn so the curve applies as written.
internal sealed class SpringEase : EasingFunctionBase
{
    private readonly double response, damping, seconds;
    // The macOS edge reveal curve, 1 - e^(-9t)·cos(8t): ~70% of the distance in the first tenth of the
    // duration, which is what makes the note snap out instead of gliding.
    public SpringEase() { }
    // SwiftUI .spring(response:dampingFraction:) sampled over an animation lasting `seconds`.
    public SpringEase(double response, double damping, double seconds) { this.response = response; this.damping = damping; this.seconds = seconds; }
    protected override double EaseInCore(double t)
    {
        if (t >= 1) return 1;
        if (response <= 0) return 1 - Math.Exp(-9 * t) * Math.Cos(8 * t);
        var time = t * seconds; var omega = 2 * Math.PI / response;
        var decay = damping * omega; var ring = omega * Math.Sqrt(1 - damping * damping);
        return 1 - Math.Exp(-decay * time) * (Math.Cos(ring * time) + decay / ring * Math.Sin(ring * time));
    }
    protected override Freezable CreateInstanceCore() => new SpringEase(response, damping, seconds);
}

// Timing log for test builds only (built with -p:AsidePerf=true); calls compile away otherwise.
internal static class Perf
{
    [Conditional("PERF")]
    internal static void Log(string message)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "aside-perf.log");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { }
    }
    internal static long Start() => Stopwatch.GetTimestamp();
    internal static long Ms(long start) => (Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency;
    // Logs when the first frame is rendered and how many frames render during the next window of time.
    [Conditional("PERF")]
    internal static void Frames(string label, long start, int windowMs)
    {
        var frames = 0; long first = -1;
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            if (first < 0) first = Ms(start);
            frames++;
            if (Ms(start) >= windowMs) { CompositionTarget.Rendering -= tick; Log($"{label}: first frame {first} ms, {frames} frames in {Ms(start)} ms"); }
        };
        CompositionTarget.Rendering += tick;
    }
}

internal static class UI
{
    // Transparent windows get no system shadow, so they reserve this margin and draw one themselves.
    internal const double Shadow = 14;
    internal static DropShadowEffect ShadowEffect() => new() { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = .22, Color = Colors.Black };
    internal static TextBlock Text(string text, double size = 13) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5)
    };
    internal static Button Button(string title, Action action)
    {
        var button = new Button { Content = title, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(3), MinHeight = 28 };
        button.Click += (_, _) => action();
        System.Windows.Automation.AutomationProperties.SetName(button, title);
        return button;
    }
    // Segoe Fluent Icons ships with Windows 11; Segoe MDL2 Assets covers Windows 10 with the same code points.
    internal static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    internal static TextBlock Glyph(string glyph, double size = 12) => new()
    {
        Text = glyph, FontFamily = IconFont, FontSize = size,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    // Borderless button matching the macOS plain icon buttons; Foreground inherits from the window.
    internal static Button Flat(object content, string label, Action action, double width = 26, double height = 26)
    {
        var chrome = new FrameworkElementFactory(typeof(Border), "chrome");
        chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)), "chrome"));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .35));
        template.Triggers.Add(disabled);
        var button = new Button
        {
            Content = content, OverridesDefaultStyle = true, Template = template, Focusable = false,
            MinWidth = width, Height = height, Cursor = Cursors.Hand, ToolTip = label, VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) => action();
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        return button;
    }
    internal static Button Icon(string glyph, string label, Action action, double size = 12) => Flat(Glyph(glyph, size), label, action);
    // Light bordered button for the settings window, close to a macOS rounded push button.
    internal static Button Pill(string text, Action action)
    {
        var chrome = new FrameworkElementFactory(typeof(Border), "chrome");
        chrome.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(252, 252, 252)));
        chrome.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(212, 212, 212)));
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        chrome.SetValue(Border.PaddingProperty, new Thickness(11, 3, 11, 4));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(240, 240, 240)), "chrome"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(228, 228, 228)), "chrome"));
        template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .4));
        template.Triggers.Add(disabled);
        var button = new Button
        {
            Content = text, OverridesDefaultStyle = true, Template = template, Cursor = Cursors.Hand,
            FontSize = 12, MinHeight = 26, MinWidth = 28, VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) => action();
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        return button;
    }
    // Windows fades top-level windows in and out on show/hide. The macOS panels turn this off
    // (animationBehavior = .none); without it a docked note leaves a fading ghost after it hides.
    internal static void DisableSystemAnimations(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var disabled = 1;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 3, ref disabled, sizeof(int)); // DWMWA_TRANSITIONS_FORCEDISABLED
        };
    }
    internal static Brush Muted(Brush foreground) =>
        foreground is SolidColorBrush solid ? new SolidColorBrush(Color.FromArgb(150, solid.Color.R, solid.Color.G, solid.Color.B)) : Brushes.Gray;
    internal static Window Dialog(string title, double width, double height, Window? owner = null) => new()
    {
        Title = title, Width = width, Height = height, Owner = owner, Topmost = true,
        WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        FontFamily = new FontFamily("Microsoft YaHei UI"), Background = Brushes.WhiteSmoke,
        ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
    };
    internal static Color ParseColor(string value)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch { return Color.FromRgb(232, 230, 219); }
    }
}

internal sealed class MainWindow : Window
{
    private readonly App app;
    private const double Shadow = UI.Shadow;
    private readonly Border surface = new() { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Margin = new Thickness(Shadow) };
    // Separate layer so typing or hovering re-renders the content without re-blurring the shadow.
    private readonly Border shadow = new() { CornerRadius = new CornerRadius(16), Margin = new Thickness(Shadow), Effect = UI.ShadowEffect() };
    private readonly TextBox input = new() { FontSize = 14, MinWidth = 80, Padding = new Thickness(2, 4, 2, 4), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock inputPlaceholder = new() { Text = "记下一件事…", FontSize = 14, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly Grid inputArea = new();
    private Button? saveInput;
    private bool pinDraft;
    private readonly DispatcherTimer frameTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool resizingMode;
    private bool showDone;
    private StackPanel? compactControls;
    private Brush muted = Brushes.Gray;
    private readonly DispatcherTimer edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Window edgeHandle = new()
    {
        Title = "Aside 边缘唤回条", Width = 8, Height = 100,
        WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
        AllowsTransparency = true, ShowInTaskbar = false, ShowActivated = false, Focusable = false
    };
    private Rect? dockFrame;
    private bool dockHidden;
    private bool edgeTransitioning;
    private bool editingItem;
    private Action? finishInlineEdit;
    private int edgeMotionToken;
    private bool userDragging;
    // A note docked from a previous session starts visible and only hides after the pointer has
    // visited it once, so launching never leaves the user with nothing on screen.
    private bool edgeArmed = true;

    internal MainWindow(App app)
    {
        this.app = app;
        Title = "Aside Inbox"; FontFamily = new FontFamily("Microsoft YaHei UI");
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false; MinWidth = 290;
        // A borderless window has no resize frame; WindowChrome restores edge and corner resizing like macOS.
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(Shadow + 6), GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
        });
        var p = app.Store.Book.Preferences;
        SetSizeLimits();
        // Preferences store the visible note's frame; the window adds the shadow margin around it.
        Width = (p.StripMode ? p.StripWidth : (p.Collapsed ? p.CompactWidth : p.ExpandedWidth)) + 2 * Shadow;
        Height = (p.StripMode ? 30 : (p.Collapsed ? p.CompactHeight : p.ExpandedHeight)) + 2 * Shadow;
        Left = p.Left - Shadow; Top = p.Top - Shadow;
        var frame = new Grid();
        frame.Children.Add(shadow); frame.Children.Add(surface);
        Content = frame;
        inputArea.Children.Add(input); inputArea.Children.Add(inputPlaceholder);
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SaveInput(); e.Handled = true; } };
        input.TextChanged += (_, _) =>
        {
            inputPlaceholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
            if (saveInput != null) saveInput.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        };
        // Like isMovableByWindowBackground on macOS: any click that no control handled drags the window.
        surface.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 1 || Mouse.LeftButton != MouseButtonState.Pressed) return;
            e.Handled = true; DragWindow();
        };
        MouseEnter += (_, _) => { ShowCompactControls(true); Fade(); };
        MouseLeave += (_, _) => { finishInlineEdit?.Invoke(); ShowCompactControls(false); Fade(); };
        Activated += (_, _) => Fade(); Deactivated += (_, _) => Fade();
        LocationChanged += (_, _) => { if (!resizingMode && Mouse.LeftButton == MouseButtonState.Pressed) userDragging = true; ScheduleFrame(); };
        SizeChanged += (_, _) => ScheduleFrame();
        frameTimer.Tick += (_, _) => { frameTimer.Stop(); SaveFrame(); };
        Loaded += (_, _) =>
        {
            ClampToScreen();
            if (p.DockEdge != null) { dockFrame = new Rect(Left, Top, Width, Height); edgeArmed = false; }
            edgeTimer.Start();
        };
        edgeTimer.Tick += (_, _) => PollEdge();
        UI.DisableSystemAnimations(this); UI.DisableSystemAnimations(edgeHandle);
        Closing += (_, e) => { if (!app.Quitting) { e.Cancel = true; HideToTray(); } };
        Render();
    }

    internal void Render()
    {
        var p = app.Store.Book.Preferences;
        Topmost = p.Topmost;
        var color = UI.ParseColor(p.Background);
        edgeHandle.Topmost = p.Topmost;
        edgeHandle.Background = new SolidColorBrush(color);
        var dark = color.R * .2126 + color.G * .7152 + color.B * .0722 < 115;
        Foreground = dark ? Brushes.WhiteSmoke : new SolidColorBrush(Color.FromRgb(45, 49, 44));
        muted = UI.Muted(Foreground);
        surface.Background = new SolidColorBrush(color);
        shadow.Background = surface.Background;
        surface.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(26, 0, 0, 0));
        input.Foreground = Foreground; input.CaretBrush = Foreground; input.Background = Brushes.Transparent;
        inputPlaceholder.Foreground = muted;
        surface.Padding = p.StripMode ? new Thickness(10, 0, 6, 0) : new Thickness(0);
        if (p.StripMode)
        {
            var strip = new DockPanel { Background = Brushes.Transparent };
            var expand = UI.Icon("", "展开 Inbox", Expand, 10);
            DockPanel.SetDock(expand, Dock.Right); strip.Children.Add(expand);
            var grip = UI.Glyph("", 9); grip.Foreground = muted; grip.HorizontalAlignment = HorizontalAlignment.Left;
            strip.Children.Add(grip);
            strip.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Expand(); e.Handled = true; } };
            surface.Child = strip; Fade(); return;
        }
        var root = new DockPanel();
        if (!p.Collapsed)
        {
            var header = new DockPanel { Margin = new Thickness(18, 14, 12, 10), Background = Brushes.Transparent };
            var controls = HeaderControls(p);
            DockPanel.SetDock(controls, Dock.Right); header.Children.Add(controls);
            var pinCount = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            var pinGlyph = UI.Glyph("", 10); pinGlyph.Foreground = muted; pinCount.Children.Add(pinGlyph);
            pinCount.Children.Add(new TextBlock { Text = $" {app.Store.Book.Pins.Length}/{p.PinCapacity}", FontSize = 11, FontWeight = FontWeights.Medium, Foreground = muted, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(pinCount, Dock.Right); header.Children.Add(pinCount);
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new TextBlock { Text = "Inbox", FontSize = 19, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var open = app.Store.Book.Items.Count(i => !i.Completed);
            title.Children.Add(new TextBlock { Text = open.ToString(), FontSize = 11, FontFamily = new FontFamily("Consolas"), Foreground = muted, Margin = new Thickness(6, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(title);
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            var topDivider = Divider(); DockPanel.SetDock(topDivider, Dock.Top); root.Children.Add(topDivider);

            // Detach the retained input area before moving it to the new footer.
            if (inputArea.Parent is Panel oldInput) oldInput.Children.Remove(inputArea);
            var footer = new DockPanel { Margin = new Thickness(18, 9, 18, 11) };
            var plus = UI.Glyph("", 12); plus.Foreground = muted; plus.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(plus, Dock.Left); footer.Children.Add(plus);
            saveInput = UI.Icon("", "保存事项", SaveInput);
            saveInput.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
            DockPanel.SetDock(saveInput, Dock.Right); footer.Children.Add(saveInput);
            var pinToggle = UI.Icon(pinDraft ? "" : "", pinDraft ? "取消新事项置顶" : "新事项直接置顶", () => { pinDraft = !pinDraft; Render(); input.Focus(); });
            DockPanel.SetDock(pinToggle, Dock.Right); footer.Children.Add(pinToggle);
            footer.Children.Add(inputArea);
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            var bottomDivider = Divider(); DockPanel.SetDock(bottomDivider, Dock.Bottom); root.Children.Add(bottomDivider);
        }
        var rows = new StackPanel
        {
            Margin = p.Collapsed ? new Thickness(12, 25, 12, 12) : new Thickness(10, 10, 10, 12),
            Background = Brushes.Transparent
        };
        foreach (var item in app.Store.Book.Pins) rows.Children.Add(Row(item));
        if (!p.Collapsed)
        {
            var normal = app.Store.Book.Items.Where(i => !i.Pinned && !i.Completed).OrderByDescending(i => i.CreatedAt).ToArray();
            if (app.Store.Book.Pins.Length > 0 && normal.Length > 0) { var gap = Divider(); gap.Margin = new Thickness(8, 9, 8, 9); rows.Children.Add(gap); }
            foreach (var item in normal) rows.Children.Add(Row(item));
            if (normal.Length + app.Store.Book.Pins.Length == 0)
            {
                var empty = new StackPanel { Margin = new Thickness(10, 32, 10, 32) };
                empty.Children.Add(new TextBlock { Text = "想到就记，记完就走。", FontSize = 15, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 9) });
                empty.Children.Add(new TextBlock { Text = "随手记入 Inbox，把最想看见的事项 Pin 在这里。", FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
                rows.Children.Add(empty);
            }
            var done = app.Store.Book.Items.Where(i => i.Completed).OrderByDescending(i => i.CreatedAt).ToArray();
            if (done.Length > 0)
            {
                var doneRows = new StackPanel { Visibility = showDone ? Visibility.Visible : Visibility.Collapsed };
                foreach (var item in done) doneRows.Children.Add(Row(item));
                // A small chevron like the macOS DisclosureGroup, in place of WPF's circled Expander button.
                var chevron = UI.Glyph("\uE76C", 8);
                chevron.Foreground = muted; chevron.Margin = new Thickness(0, 1, 7, 0);
                chevron.RenderTransformOrigin = new Point(.5, .5); chevron.RenderTransform = new RotateTransform(showDone ? 90 : 0);
                var doneHeader = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(7, 15, 7, 4) };
                System.Windows.Automation.AutomationProperties.SetName(doneHeader, "展开或收起已完成事项");
                DockPanel.SetDock(chevron, Dock.Left); doneHeader.Children.Add(chevron);
                var clearDone = UI.Flat(new TextBlock { Text = "清空", FontSize = 11, Foreground = muted }, "清空已完成事项", RequestClearCompleted, 34, 22);
                DockPanel.SetDock(clearDone, Dock.Right); doneHeader.Children.Add(clearDone);
                doneHeader.Children.Add(new TextBlock { Text = $"已完成 · {done.Length}", FontSize = 11, Foreground = muted, VerticalAlignment = VerticalAlignment.Center });
                // Claim the press so the window does not start dragging, then toggle on release.
                doneHeader.MouseLeftButtonDown += (_, e) => e.Handled = true;
                doneHeader.MouseLeftButtonUp += (_, _) =>
                {
                    showDone = !showDone;
                    doneRows.Visibility = showDone ? Visibility.Visible : Visibility.Collapsed;
                    chevron.RenderTransform = new RotateTransform(showDone ? 90 : 0);
                };
                rows.Children.Add(doneHeader);
                rows.Children.Add(doneRows);
            }
        }
        else if (app.Store.Book.Pins.Length == 0)
            rows.Children.Add(new TextBlock { Text = "暂无置顶事项", FontSize = 13, Foreground = muted, Margin = new Thickness(10, 12, 10, 12) });
        // A focusable ScrollViewer swallows mouse-down, which would block background dragging.
        root.Children.Add(new ScrollViewer { Content = rows, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        compactControls = null;
        if (p.Collapsed)
        {
            var layers = new Grid();
            layers.Children.Add(root);
            compactControls = HeaderControls(p);
            compactControls.HorizontalAlignment = HorizontalAlignment.Right; compactControls.VerticalAlignment = VerticalAlignment.Top;
            compactControls.Margin = new Thickness(0, 1, 8, 0);
            layers.Children.Add(compactControls);
            ShowCompactControls(IsMouseOver);
            surface.Child = layers;
        }
        else surface.Child = root;
        Fade();
    }

    private StackPanel HeaderControls(Preferences p)
    {
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var size = p.Collapsed ? 10 : 12;
        var topmost = UI.Icon("", p.Topmost ? "取消 Inbox 窗口置顶" : "开启 Inbox 窗口置顶",
            () => app.Commit(b => b.Preferences.Topmost = !b.Preferences.Topmost), size);
        topmost.Opacity = p.Topmost ? 1 : .45;
        controls.Children.Add(topmost);
        controls.Children.Add(UI.Icon("", "最小化到系统托盘", HideToTray, size));
        controls.Children.Add(UI.Icon("", "设置", app.ShowSettings, size));
        if (p.Collapsed)
        {
            controls.Children.Add(UI.Icon("", "收为细条", ToggleMode, size));
            controls.Children.Add(UI.Icon("", "展开 Inbox", Expand, size));
        }
        else controls.Children.Add(UI.Icon("", "仅显示置顶事项", ToggleMode, size));
        return controls;
    }

    private Border Divider() => new()
    {
        Height = 1, Margin = new Thickness(18, 0, 18, 0),
        Background = Foreground is SolidColorBrush fg ? new SolidColorBrush(Color.FromArgb(30, fg.Color.R, fg.Color.G, fg.Color.B)) : Brushes.LightGray
    };

    private void ShowCompactControls(bool visible)
    {
        if (compactControls == null) return;
        compactControls.Opacity = visible ? 1 : 0;
        compactControls.IsHitTestVisible = visible;
    }

    private void RequestClearCompleted()
    {
        var count = app.Store.Book.Items.Count(i => i.Completed);
        if (count == 0) return;
        if (app.Store.Book.Preferences.SkipClearCompletedConfirmation)
        {
            app.Commit(b => b.ClearCompleted());
            return;
        }
        var dialog = UI.Dialog("清空已完成事项？", 410, 280, this);
        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(UI.Text($"将永久删除 {count} 条已完成事项，无法撤销。\n未完成和置顶事项不受影响。\n\n选择「以后不再提示」将清空本次事项，并在今后直接清空，不再确认。"));
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var skipFuture = false;
        var cancel = UI.Button("取消", () => dialog.DialogResult = false);
        cancel.IsCancel = true; cancel.IsDefault = true;
        actions.Children.Add(cancel);
        actions.Children.Add(UI.Button($"清空 {count} 条", () => dialog.DialogResult = true));
        actions.Children.Add(UI.Button("以后不再提示", () => { skipFuture = true; dialog.DialogResult = true; }));
        content.Children.Add(actions);
        dialog.Content = content;
        if (dialog.ShowDialog() == true) app.Commit(b => b.ClearCompleted(skipFuture));
    }

    private FrameworkElement Row(Item item)
    {
        var row = new DockPanel { Background = Brushes.Transparent };
        var shell = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 5, 6, 5), Background = Brushes.Transparent, Child = row };
        var complete = UI.Icon(item.Completed ? "" : "", item.Completed ? "恢复事项" : "标记完成", () => app.Commit(b => b.Complete(item.Id)), 14);
        complete.Foreground = muted; complete.MinWidth = 22; complete.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(complete, Dock.Left); row.Children.Add(complete);
        Button? pin = null;
        if (!item.Completed)
        {
            pin = UI.Icon(item.Pinned ? "" : "", item.Pinned ? "取消置顶" : "置顶", () => RequestPin(item), 11);
            pin.Foreground = muted; pin.VerticalAlignment = VerticalAlignment.Top;
            pin.Opacity = item.Pinned ? 1 : 0;
            DockPanel.SetDock(pin, Dock.Right); row.Children.Add(pin);
        }
        var text = new TextBlock
        {
            Text = item.Content, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 19,
            FontWeight = item.Pinned ? FontWeights.Medium : FontWeights.Normal,
            Margin = new Thickness(5, 3, 4, 3), VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand, ToolTip = "点击修改事项", Background = Brushes.Transparent
        };
        var dragging = false;
        // Clicking the text edits it instead of dragging the window.
        text.MouseLeftButtonDown += (_, e) => e.Handled = true;
        text.MouseLeftButtonUp += (_, e) => { if (e.ChangedButton == MouseButton.Left && !dragging) StartInlineEdit(item, row, text); };
        if (item.Completed) { text.TextDecorations = TextDecorations.Strikethrough; text.Foreground = muted; }
        row.Children.Add(text);
        var hoverFill = Foreground is SolidColorBrush fg ? new SolidColorBrush(Color.FromArgb(13, fg.Color.R, fg.Color.G, fg.Color.B)) : Brushes.Transparent;
        shell.MouseEnter += (_, _) => { shell.Background = hoverFill; if (pin != null) pin.Opacity = 1; };
        shell.MouseLeave += (_, _) => { shell.Background = Brushes.Transparent; if (pin != null && !item.Pinned) pin.Opacity = 0; };
        var context = new ContextMenu();
        if (!item.Completed)
        {
            var pinItem = new MenuItem { Header = item.Pinned ? "取消置顶" : "置顶" }; pinItem.Click += (_, _) => RequestPin(item);
            context.Items.Add(pinItem);
        }
        var completeItem = new MenuItem { Header = item.Completed ? "恢复事项" : "标记完成" }; completeItem.Click += (_, _) => app.Commit(b => b.Complete(item.Id));
        context.Items.Add(completeItem);
        context.Items.Add(new Separator());
        var remove = new MenuItem { Header = "删除" }; remove.Click += (_, _) => app.Commit(b => b.Delete(item.Id));
        context.Items.Add(remove); shell.ContextMenu = context;
        if (item.Pinned)
        {
            Point start = default;
            text.PreviewMouseLeftButtonDown += (_, e) => { start = e.GetPosition(text); dragging = false; };
            text.MouseMove += (_, e) =>
            {
                var point = e.GetPosition(text);
                if (e.LeftButton == MouseButtonState.Pressed && (Math.Abs(point.X - start.X) > 5 || Math.Abs(point.Y - start.Y) > 5))
                {
                    dragging = true;
                    DragDrop.DoDragDrop(text, new DataObject("QuietPin.Item", item.Id.ToString()), DragDropEffects.Move);
                }
            };
            shell.AllowDrop = true;
            shell.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("QuietPin.Item") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            shell.Drop += (_, e) => { if (Guid.TryParse(e.Data.GetData("QuietPin.Item") as string, out var id)) app.Commit(b => b.Move(id, item.Id)); };
        }
        return shell;
    }

    private void RequestPin(Item item)
    {
        if (item.Pinned) { app.Commit(b => b.Unpin(item.Id)); return; }
        var full = app.Store.Book.Pins.Length >= app.Store.Book.Preferences.PinCapacity;
        var replacement = full ? app.ChooseReplacement(this) : null;
        if (!full || replacement.HasValue) app.Commit(b => b.Pin(item.Id, replacement));
    }

    private void StartInlineEdit(Item item, DockPanel row, TextBlock label)
    {
        if (editingItem) return;
        editingItem = true;
        var editor = new TextBox { Text = item.Content, FontSize = 13, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = Foreground, Padding = new Thickness(0, 3, 0, 0) };
        row.Children.Remove(label);
        row.Children.Add(editor);
        var finished = false;
        void Finish(bool save)
        {
            if (finished) return;
            finished = true;
            editingItem = false;
            finishInlineEdit = null;
            if (save && !string.IsNullOrWhiteSpace(editor.Text) && editor.Text.Trim() != item.Content)
                app.Commit(b => b.Edit(item.Id, editor.Text));
            else Render();
        }
        editor.KeyDown += (_, e) => {
            if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        };
        editor.LostKeyboardFocus += (_, _) => Finish(true);
        finishInlineEdit = () => Finish(true);
        editor.Focus(); editor.SelectAll();
    }

    private void SaveInput()
    {
        if (app.Add(this, input.Text, pinDraft)) { input.Clear(); pinDraft = false; Render(); input.Focus(); }
    }
    internal void ToggleMode()
    {
        var p = app.Store.Book.Preferences;
        ChangeMode(!p.StripMode, p.Collapsed && !p.StripMode);
    }
    internal void Expand() { RevealFromEdge(false); ChangeMode(false, false); }
    private void SetSizeLimits()
    {
        var p = app.Store.Book.Preferences;
        MinHeight = 0;
        MaxHeight = p.StripMode ? 30 + 2 * Shadow : double.PositiveInfinity;
        MinHeight = (p.StripMode ? 30 : (p.Collapsed ? 95 : 280)) + 2 * Shadow;
        MinWidth = (p.StripMode ? 160 : 290) + 2 * Shadow;
        // The strip has a fixed height, so only its left and right edges resize.
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome != null) chrome.ResizeBorderThickness = p.StripMode ? new Thickness(Shadow + 6, 0, Shadow + 6, 0) : new Thickness(Shadow + 6);
    }
    internal void ChangeMode(bool collapsed, bool strip)
    {
        RevealFromEdge(false); frameTimer.Stop(); SaveFrame(); resizingMode = true;
        app.Commit(b => { b.Preferences.Collapsed = collapsed; b.Preferences.StripMode = strip; }, false);
        var p = app.Store.Book.Preferences;
        SetSizeLimits();
        Width = (p.StripMode ? p.StripWidth : (p.Collapsed ? p.CompactWidth : p.ExpandedWidth)) + 2 * Shadow;
        Height = (p.StripMode ? 30 : (p.Collapsed ? p.CompactHeight : p.ExpandedHeight)) + 2 * Shadow;
        resizingMode = false; ClampToScreen(); Render(); SaveFrame();
        if (p.DockEdge != null) dockFrame = new Rect(Left, Top, Width, Height);
    }
    private void ClampToScreen()
    {
        // Convert physical working-area pixels to WPF device-independent units.
        var handle = new WindowInteropHelper(this).Handle;
        var work = Forms.Screen.FromHandle(handle).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var origin = transform.Transform(new Point(work.Left, work.Top));
        var end = transform.Transform(new Point(work.Right, work.Bottom));
        // The shadow margin may run off screen; only the note itself must stay inside the work area.
        Width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, end.X - origin.X + 2 * Shadow));
        Height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, end.Y - origin.Y + 2 * Shadow));
        Left = Math.Clamp(Left, origin.X - Shadow, Math.Max(origin.X - Shadow, end.X - Width + Shadow));
        Top = Math.Clamp(Top, origin.Y - Shadow, Math.Max(origin.Y - Shadow, end.Y - Height + Shadow));
    }
    private void ScheduleFrame() { if (!IsLoaded || resizingMode) return; frameTimer.Stop(); frameTimer.Start(); }
    internal void SaveFrame()
    {
        if (!IsLoaded || resizingMode || app.Store.LoadError != null) return;
        app.Commit(b =>
        {
            var frame = dockHidden && dockFrame.HasValue ? dockFrame.Value : new Rect(Left, Top, Width, Height);
            var p = b.Preferences; p.Left = frame.Left + Shadow; p.Top = frame.Top + Shadow;
            var width = frame.Width - 2 * Shadow; var height = frame.Height - 2 * Shadow;
            if (p.StripMode) p.StripWidth = width;
            else if (p.Collapsed) { p.CompactWidth = width; p.CompactHeight = height; }
            else { p.ExpandedWidth = width; p.ExpandedHeight = height; }
        }, false);
    }
    internal void Fade()
    {
        var p = app.Store.Book.Preferences;
        // Invisible edge tab, as on macOS: an alpha-0 layered window shows nothing and lets clicks through,
        // while PollEdge still reveals the note when the cursor reaches its strip of the screen edge.
        edgeHandle.Opacity = 0;
        if (dockHidden) return;
        if (p.DockEdge != null)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = Math.Clamp(p.ActiveOpacity, .15, 1);
            return;
        }
        var opacity = p.Fade && !IsMouseOver && !IsActive ? p.IdleOpacity : p.ActiveOpacity;
        BeginAnimation(OpacityProperty, new DoubleAnimation(Math.Clamp(opacity, .15, 1), TimeSpan.FromMilliseconds(180)));
    }

    private Rect WorkArea()
    {
        var work = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var origin = transform.Transform(new Point(work.Left, work.Top));
        var end = transform.Transform(new Point(work.Right, work.Bottom));
        return new Rect(origin, end);
    }
    private void DragWindow()
    {
        userDragging = true;
        DragMove();
        // DragMove runs a native move loop; classify the final position on return.
        userDragging = true;
        PollEdge();
    }
    private void PollEdge()
    {
        if (edgeTransitioning && dockHidden) return;
        var pressing = Native.GetAsyncKeyState(0x01) < 0;
        if (userDragging && !pressing)
        {
            userDragging = false;
            var work = WorkArea();
            string? edge = Left + Shadow <= work.Left + 18 ? "left" : (Left + Width - Shadow >= work.Right - 18 ? "right" : null);
            app.Commit(b => b.Preferences.DockEdge = edge, false);
            if (edge != null)
            {
                resizingMode = true;
                Left = edge == "left" ? work.Left - Shadow : work.Right - Width + Shadow;
                Top = Math.Clamp(Top, work.Top - Shadow, Math.Max(work.Top - Shadow, work.Bottom - Height + Shadow));
                resizingMode = false;
            }
            dockFrame = edge == null ? null : new Rect(Left, Top, Width, Height);
            edgeArmed = true;
            Fade(); SaveFrame();
        }
        var visibleWindow = dockHidden ? edgeHandle : this;
        if (pressing || userDragging || editingItem || !visibleWindow.IsVisible || !IsEnabled || app.Store.Book.Preferences.DockEdge == null || !dockFrame.HasValue) return;
        Native.GetCursorPos(out var cursor);
        var local = visibleWindow.PointFromScreen(new Point(cursor.X, cursor.Y));
        if (edgeTransitioning && !dockHidden && dockFrame.HasValue)
            local = new Point(local.X + Left - dockFrame.Value.Left, local.Y + Top - dockFrame.Value.Top);
        var inset = visibleWindow == this ? Shadow : 0;
        if (new Rect(inset - 3, inset - 3, visibleWindow.ActualWidth - 2 * inset + 6, visibleWindow.ActualHeight - 2 * inset + 6).Contains(local))
        { edgeArmed = true; RevealFromEdge(); }
        else if (edgeArmed) HideAtEdge();
    }
    private void HideAtEdge(bool animated = true)
    {
        if (dockHidden || !dockFrame.HasValue) return;
        var rest = dockFrame.Value; var work = WorkArea();
        var currentLeft = Left;
        BeginAnimation(LeftProperty, null);
        Left = currentLeft;
        resizingMode = true; frameTimer.Stop(); dockFrame = rest; dockHidden = true;
        edgeTransitioning = true; var token = ++edgeMotionToken;
        var started = Perf.Start();
        // Swap windows instead of resizing the live transparent content to 8px.
        edgeHandle.Height = Math.Min(100, rest.Height - 2 * Shadow);
        edgeHandle.Left = app.Store.Book.Preferences.DockEdge == "left" ? work.Left : work.Right - 8;
        edgeHandle.Top = rest.Top + (rest.Height - edgeHandle.Height) / 2;
        void Finish()
        {
            if (token != edgeMotionToken || !dockHidden) return;
            BeginAnimation(LeftProperty, null);
            Hide(); Left = rest.Left; edgeHandle.Show();
            edgeTransitioning = false; resizingMode = false; Fade();
            Perf.Log($"edge hide: done after {Perf.Ms(started)} ms");
        }
        if (!animated || !SystemParameters.ClientAreaAnimation) { Finish(); return; }
        var parked = app.Store.Book.Preferences.DockEdge == "left" ? work.Left - Width + Shadow + 8 : work.Right - 8 - Shadow;
        var slide = new DoubleAnimation(Left, parked, TimeSpan.FromMilliseconds(120)) {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        slide.Completed += (_, _) => Finish();
        BeginAnimation(LeftProperty, slide);
    }
    private void RevealFromEdge(bool animated = true)
    {
        if (!dockHidden || !dockFrame.HasValue) return;
        var rest = dockFrame.Value; var work = WorkArea();
        resizingMode = true; dockHidden = false; edgeTransitioning = true;
        var token = ++edgeMotionToken;
        var started = Perf.Start();
        BeginAnimation(LeftProperty, null);
        var parked = app.Store.Book.Preferences.DockEdge == "left" ? work.Left - rest.Width + Shadow + 8 : work.Right - 8 - Shadow;
        Left = animated && SystemParameters.ClientAreaAnimation ? parked : rest.Left;
        Top = rest.Top; Width = rest.Width; Height = rest.Height;
        UpdateLayout(); Show(); edgeHandle.Hide();
        void Finish()
        {
            if (token != edgeMotionToken || dockHidden) return;
            BeginAnimation(LeftProperty, null);
            Left = rest.Left; edgeTransitioning = false; resizingMode = false; Fade();
            Perf.Log($"edge reveal: done after {Perf.Ms(started)} ms");
        }
        if (!animated || !SystemParameters.ClientAreaAnimation) { Finish(); return; }
        var slide = new DoubleAnimation(parked, rest.Left, TimeSpan.FromMilliseconds(300)) {
            EasingFunction = new SpringEase { EasingMode = EasingMode.EaseIn }
        };
        slide.Completed += (_, _) => Finish();
        Perf.Frames("edge reveal", started, 300);
        BeginAnimation(LeftProperty, slide);
    }
    internal void HideToTray() { Hide(); edgeHandle.Hide(); }
    internal bool EdgeHandleVisible => edgeHandle.IsVisible;
}

internal sealed class CaptureWindow : Window
{
    private readonly App app;
    // Alpha 1 instead of fully transparent: a layered window lets the mouse fall through alpha-0 pixels, which
    // ended the hover in the gap before the close button. macOS treats the whole bar as hover area.
    private readonly DockPanel captureRoot = new() { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), RenderTransformOrigin = new Point(.5, .5) };
    private int entrance;
    private readonly ScaleTransform captureScale = new(1, 1);
    private readonly ScaleTransform cancelScale = new(.72, .72);
    private readonly TextBox input = new() { FontSize = 20, BorderThickness = new Thickness(0), Padding = new Thickness(5), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border surface = new() { CornerRadius = new CornerRadius(32), Padding = new Thickness(22, 8, 22, 8), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    private readonly Border cancelSurface = new() { CornerRadius = new CornerRadius(32), Width = 64, Height = 64, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBlock icon = UI.Glyph("", 22);
    private readonly Button save;
    internal CaptureWindow(App app)
    {
        this.app = app;
        UI.DisableSystemAnimations(this);
        Title = "Aside 快速记录"; Width = 660 + 2 * UI.Shadow; Height = 64 + 2 * UI.Shadow; ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var panel = new DockPanel();
        icon.Margin = new Thickness(0, 0, 15, 0);
        DockPanel.SetDock(icon, Dock.Left); panel.Children.Add(icon);
        save = UI.Icon("", "保存到 Inbox", Save, 14); DockPanel.SetDock(save, Dock.Right); panel.Children.Add(save);
        var inputArea = new Grid();
        inputArea.Children.Add(input);
        var placeholder = new TextBlock { Text = "记下此刻的想法…", FontSize = 20, Foreground = Brushes.Gray, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        inputArea.Children.Add(placeholder);
        input.TextChanged += (_, _) => placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        panel.Children.Add(inputArea); surface.Child = panel;
        var cancel = new Button { Content = UI.Glyph("", 18), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "取消输入 · Esc", Cursor = Cursors.Hand, OverridesDefaultStyle = true };
        var cancelPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        cancelPresenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cancelPresenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var cancelRoot = new FrameworkElementFactory(typeof(Border));
        cancelRoot.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        cancelRoot.SetValue(Border.CornerRadiusProperty, new CornerRadius(32));
        cancelRoot.AppendChild(cancelPresenter);
        cancel.Template = new ControlTemplate(typeof(Button)) { VisualTree = cancelRoot };
        cancel.HorizontalContentAlignment = HorizontalAlignment.Center;
        cancel.VerticalContentAlignment = VerticalAlignment.Center;
        cancel.Click += (_, _) => app.CloseCapture();
        System.Windows.Automation.AutomationProperties.SetName(cancel, "取消输入");
        cancelSurface.Child = cancel;
        cancelSurface.Opacity = 0;
        cancelSurface.IsHitTestVisible = false;
        cancelSurface.RenderTransformOrigin = new Point(.5, .5);
        cancelSurface.RenderTransform = cancelScale;
        captureRoot.RenderTransform = captureScale;
        captureRoot.Margin = new Thickness(UI.Shadow);
        surface.Effect = UI.ShadowEffect(); cancelSurface.Effect = UI.ShadowEffect();
        DockPanel.SetDock(cancelSurface, Dock.Right); captureRoot.Children.Add(cancelSurface); captureRoot.Children.Add(surface); Content = captureRoot;
        captureRoot.MouseEnter += (_, _) => AnimateCancel(true);
        captureRoot.MouseLeave += (_, _) => AnimateCancel(false);
        input.ToolTip = "记下此刻的想法…";
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                if (!app.Store.Book.Preferences.ControlEnterToSave || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Save();
                e.Handled = true;
            }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { app.CloseCapture(); e.Handled = true; } };
        // Drag from anywhere on the bar, including the text box: a press that moves past the system drag
        // threshold moves the window, while a plain click still places the caret.
        Point? pressed = null;
        var caret = 0;
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressed = InsideButton(e.OriginalSource as DependencyObject) ? null : e.GetPosition(this);
            caret = input.CaretIndex;
        };
        PreviewMouseLeftButtonUp += (_, _) => pressed = null;
        PreviewMouseMove += (_, e) =>
        {
            if (pressed is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
            var moved = e.GetPosition(this) - start;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressed = null;
            Mouse.Capture(null);
            DragMove();
            // Undo any selection the text box began before the drag took over.
            input.Select(caret, 0);
            e.Handled = true;
        };
        Deactivated += (_, _) => { if (IsVisible) app.CloseCapture(false); };
        Closing += (_, e) => { if (!app.Quitting) { e.Cancel = true; app.CloseCapture(); } };
    }
    // Centre on the cursor's screen in physical pixels. The window may not have been shown yet, so WPF's
    // device transform is not available; ask the monitor for its DPI instead.
    internal void PositionAtCursor()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.GetCursorPos(out var point);
        var work = Forms.Screen.FromPoint(new System.Drawing.Point(point.X, point.Y)).WorkingArea;
        var monitor = Native.MonitorFromPoint(point, 2);
        var scale = Native.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
        Width = Math.Min(660, work.Width / scale - 40) + 2 * UI.Shadow;
        var width = (int)Math.Round(Width * scale); var height = (int)Math.Round(Height * scale);
        Native.SetWindowPos(hwnd, IntPtr.Zero, work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2, 0, 0, 0x0001 | 0x0004 | 0x0010);
        Perf.Log($"capture place: work {work}, dpi scale {scale:F2}, size {width}x{height}");
        ApplyAppearance();
    }
    [Conditional("PERF")]
    internal void LogPlacement()
    {
        Native.GetWindowRect(new WindowInteropHelper(this).Handle, out var rect);
        Perf.Log($"capture shown at {rect.Left},{rect.Top} to {rect.Right},{rect.Bottom}");
    }
    internal void ApplyAppearance()
    {
        var color = UI.ParseColor(app.Store.Book.Preferences.Background);
        surface.Background = new SolidColorBrush(color);
        cancelSurface.Background = surface.Background;
        var dark = color.R * .2126 + color.G * .7152 + color.B * .0722 < 115;
        Foreground = dark ? Brushes.WhiteSmoke : Brushes.Black;
        input.Foreground = Foreground; input.CaretBrush = Foreground;
        icon.Foreground = save.Foreground = UI.Muted(Foreground);
        surface.BorderBrush = cancelSurface.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(46, 255, 255, 255) : Color.FromArgb(36, 0, 0, 0));
        save.ToolTip = app.Store.Book.Preferences.ControlEnterToSave ? "保存到 Inbox · Ctrl + Enter" : "保存到 Inbox · Enter";
        Opacity = Math.Clamp(app.Store.Book.Preferences.CaptureOpacity, .15, 1);
    }
    internal void PrepareEntrance()
    {
        captureRoot.BeginAnimation(OpacityProperty, null);
        captureScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        captureScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        captureRoot.Opacity = SystemParameters.ClientAreaAnimation ? .75 : 1;
        captureScale.ScaleX = SystemParameters.ClientAreaAnimation ? .90 : 1;
        captureScale.ScaleY = SystemParameters.ClientAreaAnimation ? .98 : 1;
        AnimateCancel(false);
    }
    // Start the pop-in on the first rendered frame. Starting the clock before Show() let ~30 ms of the
    // animation elapse off screen, so the bar seemed to appear without popping.
    internal void AnimateIn()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var token = ++entrance;
        EventHandler? begin = null;
        begin = (_, _) =>
        {
            CompositionTarget.Rendering -= begin;
            if (token != entrance) return;
            captureRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(.75, 1, TimeSpan.FromMilliseconds(90)) {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            // SwiftUI .spring(response: 0.12, dampingFraction: 0.85) on the Mac.
            var spring = new SpringEase(.12, .85, .25) { EasingMode = EasingMode.EaseIn };
            captureScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.90, 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = spring });
            captureScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.98, 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = spring });
        };
        CompositionTarget.Rendering += begin;
    }
    // macOS fades the panel out (ease-in, 70 ms) without scaling it.
    internal void AnimateOut(Action completed)
    {
        entrance++;
        if (!SystemParameters.ClientAreaAnimation) { completed(); return; }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(70)) {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => completed();
        captureRoot.BeginAnimation(OpacityProperty, fade);
    }
    private void AnimateCancel(bool visible)
    {
        Perf.Log($"capture hover: close button {(visible ? "shown" : "hidden")}");
        cancelSurface.IsHitTestVisible = visible;
        var opacity = visible ? 1.0 : 0.0;
        var scale = visible ? 1.0 : .72;
        if (!SystemParameters.ClientAreaAnimation)
        {
            cancelSurface.Opacity = opacity; cancelScale.ScaleX = cancelScale.ScaleY = scale; return;
        }
        // SwiftUI .spring(response: 0.30, dampingFraction: 0.60) drives both opacity and scale on the Mac.
        var spring = new SpringEase(.30, .60, .45) { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(450);
        cancelSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, duration) { EasingFunction = spring });
        cancelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, duration) { EasingFunction = spring });
        cancelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, duration) { EasingFunction = spring });
    }
    private static bool InsideButton(DependencyObject? element)
    {
        for (; element != null; element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }
    internal void FocusInput() => Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) input.Focus(); }), DispatcherPriority.Input);
    private void Save()
    {
        if (app.Add(this, input.Text, false)) { input.Clear(); app.CloseCapture(); }
    }
}

internal sealed class SettingsWindow : Window
{
    private static readonly Brush Secondary = Frozen(Color.FromRgb(104, 104, 110));
    private static readonly Brush Hairline = Frozen(Color.FromRgb(230, 230, 230));
    private static readonly Brush Accent = Frozen(Color.FromRgb(77, 99, 84));
    private readonly App app;
    private readonly StackPanel stack = new() { Margin = new Thickness(20, 12, 20, 22) };
    private readonly StackPanel previews = new();
    private readonly WrapPanel savedColors = new() { VerticalAlignment = VerticalAlignment.Center };
    private Border? currentSwatch;
    private TextBox? hexInput;
    internal void Refresh() => Render();
    private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

    internal SettingsWindow(App app)
    {
        this.app = app;
        Title = "Aside 设置"; Width = 460; Height = 720; Topmost = true; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        Background = Frozen(Color.FromRgb(243, 243, 243)); Foreground = Frozen(Color.FromRgb(28, 28, 30));
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Render();
    }

    private void Render()
    {
        stack.Children.Clear(); var p = app.Store.Book.Preferences;

        if (app.Welcome)
        {
            var welcome = Card("欢迎使用 Aside");
            var body = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            body.Children.Add(new TextBlock { Text = "安装完成。Aside 已在后台运行，可在屏幕右下角的系统托盘中找到它（可能收在“^”里）。", TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
            var current = p.CustomKeyLabel ?? App.ShortcutLabels[Math.Clamp(p.Shortcut, 0, 2)];
            body.Children.Add(new TextBlock { Text = $"先在下方「全局快捷键」设置呼出快速记录的组合键（当前为 {current}）。之后在任何应用里按下它，就能随手记一句。", FontSize = 12, Foreground = Secondary, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 6, 0, 10) });
            var ok = UI.Pill("知道了", () => { app.Welcome = false; Render(); });
            ok.HorizontalAlignment = HorizontalAlignment.Right;
            body.Children.Add(ok); welcome.Children.Add(body);
        }

        var general = Card("通用");
        AddRow(general, "窗口始终置顶", null, Switch(p.Topmost, value => app.Commit(b => b.Preferences.Topmost = value)));
        AddRow(general, "最多置顶条数", "调低数量不会移除已有置顶；可手动取消多余置顶。",
            Stepper(p.PinCapacity, 1, 20, value => app.Commit(b => b.Preferences.PinLimit = value)));
        bool login;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            login = key?.GetValue("QuietPin") != null;
        AddRow(general, "登录时启动 Aside", null, Switch(login, SetLogin));
        AddRow(general, "全局快捷键", app.ShortcutError, ShortcutRecorder(), app.ShortcutError != null);
        AddRow(general, "快速记录保存键", "快捷输入居中出现，保存后自动消失，新记录默认不置顶。",
            Segmented(new[] { "Enter", "Ctrl + Enter" }, p.ControlEnterToSave ? 1 : 0,
                index => app.Commit(b => b.Preferences.ControlEnterToSave = index == 1)));

        var look = Card("外观");
        currentSwatch = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(6), BorderBrush = Frozen(Color.FromRgb(200, 200, 200)), BorderThickness = new Thickness(1) };
        AddRow(look, "背景颜色", null, Inline(currentSwatch, UI.Pill("调色盘…", OpenColorDialog), UI.Pill("屏幕取色…", StartPicker)));
        hexInput = new TextBox { Width = 112, Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center, BorderBrush = Frozen(Color.FromRgb(212, 212, 212)) };
        AddRow(look, "颜色值", "#RRGGBB 或 #AARRGGBB", Inline(hexInput, UI.Pill("应用", ApplyHex)));
        var shelf = new DockPanel();
        var saveColor = UI.Pill("保存颜色", () => app.Commit(b =>
        {
            var color = UI.ParseColor(b.Preferences.Background).ToString();
            if (!b.Preferences.SavedColors.Contains(color)) b.Preferences.SavedColors.Add(color);
        }));
        saveColor.VerticalAlignment = VerticalAlignment.Top; saveColor.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(saveColor, Dock.Left); shelf.Children.Add(saveColor);
        if (savedColors.Parent is Panel oldShelf) oldShelf.Children.Remove(savedColors);
        shelf.Children.Add(savedColors);
        AddBlock(look, "收藏色", shelf);
        var idle = OpacityRow(look, "鼠标离开", p.IdleOpacity, value => app.Commit(b => b.Preferences.IdleOpacity = value));
        idle.IsEnabled = p.Fade;
        OpacityRow(look, "交互时", p.ActiveOpacity, value => app.Commit(b => b.Preferences.ActiveOpacity = value));
        OpacityRow(look, "快速输入", p.CaptureOpacity, value => app.Commit(b => b.Preferences.CaptureOpacity = value));
        AddRow(look, "鼠标离开时淡化", "关闭后，窗口固定使用「交互时」透明度。",
            Switch(p.Fade, value => { idle.IsEnabled = value; app.Commit(b => b.Preferences.Fade = value); }));
        if (previews.Parent is Panel oldPreviews) oldPreviews.Children.Remove(previews);
        AddBlock(look, "实时预览", previews);
        AddRow(look, "恢复默认外观", null, UI.Pill("恢复", () =>
        {
            app.Commit(b => { b.Preferences.Background = "#FFE8E6DB"; b.Preferences.IdleOpacity = .4; b.Preferences.ActiveOpacity = .95; b.Preferences.CaptureOpacity = .95; b.Preferences.Fade = true; });
            Render();
        }));
        UpdatePreviews();

        var about = Card("关于");
        var info = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        info.Children.Add(new TextBlock { Text = "Aside · Capture it. Stay in flow.", FontWeight = FontWeights.SemiBold });
        info.Children.Add(new TextBlock
        {
            Text = "记录仅保存在这台电脑上。拖动窗口空白处可移动，拖动边缘可调整大小；拖动置顶事项可调整顺序；右键可置顶、完成或删除。移动 exe 后，请重新开关登录启动以更新路径。",
            FontSize = 11, Foreground = Secondary, TextWrapping = TextWrapping.Wrap, LineHeight = 17, Margin = new Thickness(0, 4, 0, 0)
        });
        about.Children.Add(info);
        var githubContent = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
        githubContent.Children.Add(new System.Windows.Controls.Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/assets/GitHub_Invertocat_Black.png")),
            Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center
        });
        githubContent.Children.Add(new TextBlock { Text = "GitHub 项目主页 ↗", FontSize = 12, Foreground = Brushes.RoyalBlue, TextDecorations = TextDecorations.Underline, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });
        githubContent.Children.Add(new TextBlock { Text = "喜欢的话来点个⭐️支持作者吧，谢谢大家～", FontSize = 11, Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        var github = UI.Flat(githubContent, "打开 Aside 的 GitHub 仓库", () =>
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/savannahliz/aside") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("无法打开 GitHub：" + ex.Message, "Aside"); }
        }, 0, double.NaN);
        github.HorizontalAlignment = HorizontalAlignment.Stretch; github.Margin = new Thickness(8, 4, 8, 8);
        Separate(about); about.Children.Add(github);
    }

    private StackPanel Card(string title)
    {
        stack.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Secondary, Margin = new Thickness(4, stack.Children.Count == 0 ? 6 : 20, 0, 7) });
        var rows = new StackPanel();
        stack.Children.Add(new Border { Background = Brushes.White, BorderBrush = Hairline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = rows });
        return rows;
    }
    private static void Separate(StackPanel card)
    {
        if (card.Children.Count > 0) card.Children.Add(new Border { Height = 1, Background = Hairline, Margin = new Thickness(14, 0, 0, 0) });
    }
    private static void AddRow(StackPanel card, string label, string? caption, FrameworkElement control, bool warn = false)
    {
        Separate(card);
        var row = new DockPanel { Margin = new Thickness(14, 10, 12, 10) };
        control.VerticalAlignment = VerticalAlignment.Center; control.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(control, Dock.Right); row.Children.Add(control);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (caption != null)
            text.Children.Add(new TextBlock { Text = caption, FontSize = 11, Foreground = warn ? Brushes.Firebrick : Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        row.Children.Add(text); card.Children.Add(row);
    }
    private static void AddBlock(StackPanel card, string label, UIElement content)
    {
        Separate(card);
        var block = new StackPanel { Margin = new Thickness(14, 10, 12, 12) };
        block.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) });
        block.Children.Add(content); card.Children.Add(block);
    }
    private static StackPanel Inline(params FrameworkElement[] items)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < items.Length; i++)
        {
            items[i].VerticalAlignment = VerticalAlignment.Center;
            if (i > 0) items[i].Margin = new Thickness(6, 0, 0, 0);
            panel.Children.Add(items[i]);
        }
        return panel;
    }
    // Pill-shaped on/off switch in place of the bare WPF check box.
    private static CheckBox Switch(bool value, Action<bool> change)
    {
        var track = new FrameworkElementFactory(typeof(Border), "track");
        track.SetValue(FrameworkElement.WidthProperty, 40.0);
        track.SetValue(FrameworkElement.HeightProperty, 22.0);
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        track.SetValue(Border.BackgroundProperty, Frozen(Color.FromRgb(200, 200, 200)));
        var knob = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse), "knob");
        knob.SetValue(FrameworkElement.WidthProperty, 16.0);
        knob.SetValue(FrameworkElement.HeightProperty, 16.0);
        knob.SetValue(System.Windows.Shapes.Shape.FillProperty, Brushes.White);
        knob.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        knob.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 0, 3, 0));
        track.AppendChild(knob);
        var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = track };
        var on = new Trigger { Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter(Border.BackgroundProperty, Accent, "track"));
        on.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "knob"));
        template.Triggers.Add(on);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .4));
        template.Triggers.Add(disabled);
        var toggle = new CheckBox { IsChecked = value, OverridesDefaultStyle = true, Template = template, Cursor = Cursors.Hand, Focusable = false };
        toggle.Click += (_, _) => change(toggle.IsChecked == true);
        return toggle;
    }
    private static FrameworkElement Stepper(int value, int min, int max, Action<int> change)
    {
        var current = value;
        var label = new TextBlock { Text = value.ToString(), MinWidth = 26, TextAlignment = TextAlignment.Center, FontWeight = FontWeights.SemiBold };
        Button minus = null!, plus = null!;
        void Set(int next)
        {
            current = Math.Clamp(next, min, max); label.Text = current.ToString();
            minus.IsEnabled = current > min; plus.IsEnabled = current < max;
            change(current);
        }
        minus = UI.Pill("−", () => Set(current - 1)); plus = UI.Pill("+", () => Set(current + 1));
        minus.IsEnabled = current > min; plus.IsEnabled = current < max;
        return Inline(minus, label, plus);
    }
    private static FrameworkElement Segmented(string[] options, int selected, Action<int> change)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var track = new Border { Background = Frozen(Color.FromRgb(232, 232, 232)), CornerRadius = new CornerRadius(7), Padding = new Thickness(2), Child = row };
        var segments = new Border[options.Length];
        void Paint() { for (var i = 0; i < segments.Length; i++) segments[i].Background = i == selected ? Brushes.White : Brushes.Transparent; }
        for (var i = 0; i < options.Length; i++)
        {
            var index = i;
            var segment = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 3, 10, 4), Cursor = Cursors.Hand, Child = new TextBlock { Text = options[i], FontSize = 12 } };
            segment.MouseLeftButtonUp += (_, _) => { if (selected == index) return; selected = index; Paint(); change(index); };
            System.Windows.Automation.AutomationProperties.SetName(segment, options[i]);
            segments[i] = segment; row.Children.Add(segment);
        }
        Paint();
        return track;
    }
    private Slider OpacityRow(StackPanel card, string title, double value, Action<double> change)
    {
        Separate(card);
        var row = new DockPanel { Margin = new Thickness(14, 8, 12, 8) };
        var percent = new TextBlock { Text = $"{value:P0}", Width = 44, TextAlignment = TextAlignment.Right, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(percent, Dock.Right); row.Children.Add(percent);
        var label = new TextBlock { Text = title, Width = 76, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(label, Dock.Left); row.Children.Add(label);
        var slider = new Slider { Minimum = .15, Maximum = 1, Value = Math.Clamp(value, .15, 1), VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
        slider.ValueChanged += (_, _) => { percent.Text = $"{slider.Value:P0}"; change(slider.Value); };
        row.Children.Add(slider); card.Children.Add(row);
        return slider;
    }
    private Button ShortcutRecorder()
    {
        string Current()
        {
            var q = app.Store.Book.Preferences;
            return q.CustomKeyLabel ?? App.ShortcutLabels[Math.Clamp(q.Shortcut, 0, 2)];
        }
        var shortcut = UI.Pill(Current(), () => { });
        shortcut.Focusable = true; shortcut.MinWidth = 150;
        var recording = false;
        void FinishRecording()
        {
            if (!recording) return;
            recording = false;
            shortcut.Content = Current();
            app.RegisterShortcut();
        }
        shortcut.Click += (_, _) =>
        {
            Native.UnregisterHotKey(new WindowInteropHelper(app.Inbox).Handle, 1);
            recording = true; shortcut.Content = "请按组合键 · Esc 取消"; shortcut.Focus();
        };
        shortcut.PreviewKeyDown += (_, e) =>
        {
            if (!recording) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { FinishRecording(); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            var flags = Keyboard.Modifiers;
            if ((flags & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0) { shortcut.Content = "请包含 Ctrl、Alt 或 Win"; return; }
            uint modifiers = 0;
            var label = "";
            if (flags.HasFlag(ModifierKeys.Control)) { modifiers |= 2; label += "Ctrl + "; }
            if (flags.HasFlag(ModifierKeys.Alt)) { modifiers |= 1; label += "Alt + "; }
            if (flags.HasFlag(ModifierKeys.Shift)) { modifiers |= 4; label += "Shift + "; }
            if (flags.HasFlag(ModifierKeys.Windows)) { modifiers |= 8; label += "Win + "; }
            label += key.ToString();
            app.Commit(b => { b.Preferences.CustomKey = (uint)KeyInterop.VirtualKeyFromKey(key); b.Preferences.CustomModifiers = modifiers; b.Preferences.CustomKeyLabel = label; });
            FinishRecording();
        };
        shortcut.LostKeyboardFocus += (_, _) => FinishRecording();
        Closed += (_, _) => FinishRecording();
        System.Windows.Automation.AutomationProperties.SetName(shortcut, "录制自定义快捷键");
        return shortcut;
    }
    private void OpenColorDialog()
    {
        var color = UI.ParseColor(app.Store.Book.Preferences.Background);
        using var dialog = new Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B) };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            app.Commit(b => b.Preferences.Background = $"#FF{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
    }
    private void ApplyHex()
    {
        try { var color = (Color)ColorConverter.ConvertFromString(hexInput?.Text ?? ""); app.Commit(b => b.Preferences.Background = color.ToString()); }
        catch { MessageBox.Show("请输入有效的颜色值，例如 #FFE8E6DB。", "Aside"); }
    }
    internal void UpdatePreviews()
    {
        var p = app.Store.Book.Preferences;
        var current = UI.ParseColor(p.Background);
        if (currentSwatch != null) currentSwatch.Background = new SolidColorBrush(current);
        if (hexInput != null && !hexInput.IsKeyboardFocusWithin) hexInput.Text = p.Background;
        savedColors.Children.Clear();
        foreach (var color in p.SavedColors)
        {
            var selected = UI.ParseColor(color) == current;
            var swatch = new Border
            {
                Width = 26, Height = 26, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(UI.ParseColor(color)), Cursor = Cursors.Hand,
                BorderBrush = selected ? Accent : Frozen(Color.FromRgb(190, 190, 190)), BorderThickness = new Thickness(selected ? 2 : 1),
                ToolTip = $"使用 {color} · 右键删除"
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, "收藏颜色 " + color);
            swatch.MouseLeftButtonUp += (_, _) => app.Commit(b => b.Preferences.Background = color);
            var menu = new ContextMenu();
            var remove = new MenuItem { Header = "删除保存的颜色" };
            remove.Click += (_, _) => app.Commit(b => b.Preferences.SavedColors.Remove(color));
            menu.Items.Add(remove); swatch.ContextMenu = menu;
            savedColors.Children.Add(swatch);
        }
        if (savedColors.Children.Count == 0)
            savedColors.Children.Add(new TextBlock { Text = "保存后显示在这里\n点击使用 · 右键删除", FontSize = 11, Foreground = Secondary, LineHeight = 16 });
        previews.Children.Clear();
        var notes = new Grid();
        notes.ColumnDefinitions.Add(new ColumnDefinition()); notes.ColumnDefinitions.Add(new ColumnDefinition());
        var idle = PreviewCard("便签 · 闲置", p.Fade ? p.IdleOpacity : p.ActiveOpacity, false);
        var active = PreviewCard("便签 · 交互", p.ActiveOpacity, false);
        Grid.SetColumn(active, 1); notes.Children.Add(idle); notes.Children.Add(active);
        previews.Children.Add(notes);
        previews.Children.Add(PreviewCard("快速输入", p.CaptureOpacity, true));
    }
    private FrameworkElement PreviewCard(string label, double opacity, bool capturePreview)
    {
        var column = new StackPanel { Margin = new Thickness(0, 0, capturePreview ? 0 : 6, 8) };
        column.Children.Add(new TextBlock { Text = $"{label}  {opacity:P0}", FontSize = 11, Foreground = Secondary, Margin = new Thickness(0, 0, 0, 5) });
        var tiles = new DrawingGroup();
        tiles.Children.Add(new GeometryDrawing(Brushes.Gainsboro, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        tiles.Children.Add(new GeometryDrawing(Brushes.WhiteSmoke, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        tiles.Children.Add(new GeometryDrawing(Brushes.WhiteSmoke, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var board = new Border
        {
            Height = capturePreview ? 56 : 70, CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Background = new DrawingBrush(tiles) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute }
        };
        var color = UI.ParseColor(app.Store.Book.Preferences.Background);
        var foreground = color.R * .2126 + color.G * .7152 + color.B * .0722 < 115 ? Brushes.WhiteSmoke : Brushes.Black;
        var body = new DockPanel { Margin = new Thickness(8), Opacity = opacity };
        if (capturePreview)
        {
            var cancel = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Margin = new Thickness(6, 0, 0, 0), Background = new SolidColorBrush(color) };
            var cancelGlyph = UI.Glyph("", 11); cancelGlyph.Foreground = foreground; cancel.Child = cancelGlyph;
            DockPanel.SetDock(cancel, Dock.Right); body.Children.Add(cancel);
        }
        var text = new TextBlock { Text = capturePreview ? "记下此刻的想法…" : "Inbox\n一件要记住的事", FontSize = 11, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
        body.Children.Add(new Border { CornerRadius = new CornerRadius(capturePreview ? 18 : 9), Background = new SolidColorBrush(color), Child = text });
        board.Child = body; column.Children.Add(board);
        return column;
    }
    private void SetLogin(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled) key.SetValue("QuietPin", "\"" + Environment.ProcessPath + "\"");
            else key.DeleteValue("QuietPin", false);
        }
        catch (Exception ex) { MessageBox.Show("无法更改登录启动：" + ex.Message, "Aside"); Render(); }
    }
    private void StartPicker()
    {
        Hide();
        var picker = UI.Dialog("屏幕取色", 310, 120);
        var label = UI.Text("移动鼠标查看颜色，单击取色\nEsc 取消", 13);
        label.Margin = new Thickness(15); picker.Content = label;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        var released = false;
        timer.Tick += (_, _) =>
        {
            if (Native.GetAsyncKeyState(0x1B) < 0) { picker.Close(); return; }
            var down = Native.GetAsyncKeyState(0x01) < 0;
            if (!down) released = true;
            Native.GetCursorPos(out var point);
            var dc = Native.GetDC(IntPtr.Zero);
            var pixel = Native.GetPixel(dc, point.X, point.Y); Native.ReleaseDC(IntPtr.Zero, dc);
            if (pixel == 0xFFFFFFFF) return;
            var hex = $"#FF{pixel & 255:X2}{(pixel >> 8) & 255:X2}{(pixel >> 16) & 255:X2}";
            label.Text = "单击取色 · Esc 取消\n" + hex;
            if (released && down) { app.Commit(b => b.Preferences.Background = hex); picker.Close(); }
        };
        picker.Closed += (_, _) => { timer.Stop(); Show(); Activate(); Render(); };
        picker.Show(); timer.Start();
    }
}
