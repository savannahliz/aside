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
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern uint GetPixel(IntPtr dc, int x, int y);
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X; public int Y; }
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
        tray = new Forms.NotifyIcon { Text = "Aside · Capture it. Stay in flow.", Icon = System.Drawing.SystemIcons.Information, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 Inbox", null, (_, _) => Dispatcher.Invoke(ShowInbox));
        menu.Items.Add("快速记录", null, (_, _) => Dispatcher.Invoke(ToggleCapture));
        var pinsMenu = menu.Items.Add("仅显示置顶事项", null, (_, _) => Dispatcher.Invoke(() => { Inbox.Show(); Inbox.ChangeMode(true, false); }));
        var stripMenu = menu.Items.Add("收为细条", null, (_, _) => Dispatcher.Invoke(() => { Inbox.Show(); Inbox.ChangeMode(true, true); }));
        menu.Items.Add("隐藏窗口", null, (_, _) => Dispatcher.Invoke(Inbox.HideToTray));
        var topMenu = new Forms.ToolStripMenuItem("窗口置顶");
        topMenu.Click += (_, _) => Dispatcher.Invoke(() => Commit(b => b.Preferences.Topmost = !b.Preferences.Topmost));
        menu.Items.Add(topMenu);
        menu.Opening += (_, _) =>
        {
            topMenu.Checked = Store.Book.Preferences.Topmost;
            ((Forms.ToolStripMenuItem)pinsMenu).Checked = Store.Book.Preferences.Collapsed && !Store.Book.Preferences.StripMode;
            ((Forms.ToolStripMenuItem)stripMenu).Checked = Store.Book.Preferences.StripMode;
        };
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(ShowSettings));
        menu.Items.Add("退出 Aside", null, (_, _) => Dispatcher.Invoke(() => { Quitting = true; Shutdown(); }));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowInbox);
        if (Store.LoadError != null) MessageBox.Show(Store.LoadError, "Aside 记录载入失败");
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
        previousWindow = Native.GetForegroundWindow();
        capture ??= new CaptureWindow(this);
        capture.PositionAtCursor(); capture.PrepareEntrance(); capture.Show(); capture.Activate(); capture.FocusInput(); capture.AnimateIn();
    }
    internal void CloseCapture(bool restoreFocus = true)
    {
        if (closingCapture || capture?.IsVisible != true) return;
        closingCapture = true;
        capture.AnimateOut(() =>
        {
            capture.Hide();
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
        if (message == 0x0312 && wParam.ToInt32() == 1) { ToggleCapture(); handled = true; }
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

internal static class UI
{
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
    private readonly Border surface = new() { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(12) };
    private readonly TextBox input = new() { FontSize = 14, MinWidth = 80, Padding = new Thickness(7), BorderThickness = new Thickness(0) };
    private readonly CheckBox pinInput = new() { Content = "Pin", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5) };
    private readonly DispatcherTimer frameTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool resizingMode;
    private bool showDone;
    private StackPanel? compactControls;
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

    internal MainWindow(App app)
    {
        this.app = app;
        Title = "Aside Inbox"; FontFamily = new FontFamily("Microsoft YaHei UI");
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip; ShowInTaskbar = false; MinWidth = 290;
        var p = app.Store.Book.Preferences;
        SetSizeLimits();
        Width = p.StripMode ? p.StripWidth : (p.Collapsed ? p.CompactWidth : p.ExpandedWidth);
        Height = p.StripMode ? 30 : (p.Collapsed ? p.CompactHeight : p.ExpandedHeight);
        Left = p.Left; Top = p.Top;
        Content = surface;
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SaveInput(); e.Handled = true; } };
        MouseEnter += (_, _) => { if (compactControls != null) compactControls.Opacity = 1; Fade(); };
        MouseLeave += (_, _) => { finishInlineEdit?.Invoke(); if (compactControls != null) compactControls.Opacity = 0; Fade(); };
        Activated += (_, _) => Fade(); Deactivated += (_, _) => Fade();
        LocationChanged += (_, _) => { if (!resizingMode && Mouse.LeftButton == MouseButtonState.Pressed) userDragging = true; ScheduleFrame(); };
        SizeChanged += (_, _) => ScheduleFrame();
        frameTimer.Tick += (_, _) => { frameTimer.Stop(); SaveFrame(); };
        Loaded += (_, _) =>
        {
            ClampToScreen();
            if (p.DockEdge != null) { dockFrame = new Rect(Left, Top, Width, Height); HideAtEdge(false); }
            edgeTimer.Start();
        };
        edgeTimer.Tick += (_, _) => PollEdge();
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
        surface.Background = new SolidColorBrush(color);
        surface.BorderBrush = new SolidColorBrush(Color.FromArgb(35, 100, 100, 100));
        input.Foreground = Foreground; input.Background = Brushes.Transparent;
        surface.Padding = p.StripMode ? new Thickness(8, 0, 8, 0) : new Thickness(12);
        if (p.StripMode)
        {
            var strip = new DockPanel { Background = Brushes.Transparent };
            var expand = new Button { Content = "↗", Width = 24, Height = 22, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Foreground, ToolTip = "展开 Inbox" };
            expand.Click += (_, _) => Expand(); DockPanel.SetDock(expand, Dock.Right); strip.Children.Add(expand);
            var grip = UI.Text("≡", 12); grip.VerticalAlignment = VerticalAlignment.Center; strip.Children.Add(grip);
            strip.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Expand(); else if (e.Source == strip || e.Source == grip) DragWindow(); };
            surface.Child = strip; Fade(); return;
        }
        var root = new DockPanel();
        var header = new DockPanel { LastChildFill = true, Background = Brushes.Transparent, MinHeight = 26 };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var topmost = UI.Button(p.Topmost ? "▣" : "▢", () => app.Commit(b => b.Preferences.Topmost = !b.Preferences.Topmost));
        topmost.ToolTip = p.Topmost ? "取消 Inbox 窗口置顶" : "开启 Inbox 窗口置顶";
        System.Windows.Automation.AutomationProperties.SetName(topmost, (string)topmost.ToolTip);
        controls.Children.Add(topmost);
        var minimize = UI.Button("−", HideToTray);
        minimize.ToolTip = "最小化到系统托盘";
        System.Windows.Automation.AutomationProperties.SetName(minimize, (string)minimize.ToolTip);
        controls.Children.Add(minimize);
        controls.Children.Add(UI.Button("⚙", app.ShowSettings));
        controls.Children.Add(UI.Button(p.Collapsed ? "━" : "↙", ToggleMode));
        if (p.Collapsed) controls.Children.Add(UI.Button("↗", Expand));
        DockPanel.SetDock(controls, Dock.Right); header.Children.Add(controls);
        if (!p.Collapsed) header.Children.Add(UI.Text($"Inbox  ·  {app.Store.Book.Pins.Length}/{p.PinCapacity}", 17));
        compactControls = p.Collapsed ? controls : null;
        if (compactControls != null) compactControls.Opacity = IsMouseOver ? 1 : 0;
        header.MouseLeftButtonDown += (_, e) => { if (e.Source == header || e.Source is TextBlock) DragWindow(); };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        if (!p.Collapsed)
        {
            // Detach retained input controls before moving them to the new footer.
            if (input.Parent is Panel oldInput) oldInput.Children.Remove(input);
            if (pinInput.Parent is Panel oldPin) oldPin.Children.Remove(pinInput);
            var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 3) };
            var save = UI.Button("↵", SaveInput);
            DockPanel.SetDock(save, Dock.Right); footer.Children.Add(save);
            DockPanel.SetDock(pinInput, Dock.Right); footer.Children.Add(pinInput);
            footer.Children.Add(input); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        }
        var rows = new StackPanel();
        foreach (var item in app.Store.Book.Pins) rows.Children.Add(Row(item));
        if (!p.Collapsed)
        {
            var normal = app.Store.Book.Items.Where(i => !i.Pinned && !i.Completed).OrderByDescending(i => i.CreatedAt).ToArray();
            if (app.Store.Book.Pins.Length > 0 && normal.Length > 0) rows.Children.Add(new Separator { Margin = new Thickness(5, 8, 5, 8) });
            foreach (var item in normal) rows.Children.Add(Row(item));
            if (normal.Length + app.Store.Book.Pins.Length == 0)
                rows.Children.Add(UI.Text("想到就记，记完就走。\n\n随手记入 Inbox，把最想看见的事项 Pin 在这里。", 14));
            var done = app.Store.Book.Items.Where(i => i.Completed).ToArray();
            if (done.Length > 0)
            {
                var doneRows = new StackPanel(); foreach (var item in done) doneRows.Children.Add(Row(item));
                var doneHeader = new StackPanel { Orientation = Orientation.Horizontal };
                doneHeader.Children.Add(UI.Text($"已完成 · {done.Length}", 11));
                var clearDone = UI.Button("清空", RequestClearCompleted);
                System.Windows.Automation.AutomationProperties.SetName(clearDone, "清空已完成事项");
                doneHeader.Children.Add(clearDone);
                var expander = new Expander { Header = doneHeader, Content = doneRows, IsExpanded = showDone, Margin = new Thickness(4, 10, 4, 4) };
                expander.Expanded += (_, _) => showDone = true; expander.Collapsed += (_, _) => showDone = false;
                rows.Children.Add(expander);
            }
        }
        else if (app.Store.Book.Pins.Length == 0) rows.Children.Add(UI.Text("暂无置顶事项"));
        root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        surface.Child = root; Fade();
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
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), Background = Brushes.Transparent };
        var complete = UI.Button(item.Completed ? "✓" : "○", () => app.Commit(b => b.Complete(item.Id)));
        complete.ToolTip = item.Completed ? "恢复事项" : "标记完成";
        DockPanel.SetDock(complete, Dock.Left); row.Children.Add(complete);
        if (!item.Completed)
        {
            var pin = UI.Button(item.Pinned ? "●" : "Pin", () =>
            {
                if (item.Pinned) app.Commit(b => b.Unpin(item.Id));
                else
                {
                    var replacement = app.Store.Book.Pins.Length >= app.Store.Book.Preferences.PinCapacity ? app.ChooseReplacement(this) : null;
                    if (app.Store.Book.Pins.Length < app.Store.Book.Preferences.PinCapacity || replacement.HasValue) app.Commit(b => b.Pin(item.Id, replacement));
                }
            });
            pin.ToolTip = item.Pinned ? "取消置顶" : "置顶";
            DockPanel.SetDock(pin, Dock.Right); row.Children.Add(pin);
        }
        var text = UI.Text(item.Content); text.VerticalAlignment = VerticalAlignment.Center;
        text.Cursor = Cursors.Hand;
        text.ToolTip = "点击修改事项";
        var dragging = false;
        text.MouseLeftButtonUp += (_, e) => { if (e.ChangedButton == MouseButton.Left && !dragging) StartInlineEdit(item, row, text); };
        if (item.Completed) { text.TextDecorations = TextDecorations.Strikethrough; text.Opacity = .55; }
        row.Children.Add(text);
        var context = new ContextMenu();
        var remove = new MenuItem { Header = "删除" }; remove.Click += (_, _) => app.Commit(b => b.Delete(item.Id));
        context.Items.Add(remove); row.ContextMenu = context;
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
            row.AllowDrop = true;
            row.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("QuietPin.Item") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            row.Drop += (_, e) => { if (Guid.TryParse(e.Data.GetData("QuietPin.Item") as string, out var id)) app.Commit(b => b.Move(id, item.Id)); };
        }
        return row;
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
        if (app.Add(this, input.Text, pinInput.IsChecked == true)) { input.Clear(); pinInput.IsChecked = false; input.Focus(); }
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
        MaxHeight = p.StripMode ? 30 : double.PositiveInfinity;
        MinHeight = p.StripMode ? 30 : (p.Collapsed ? 95 : 280);
        MinWidth = p.StripMode ? 160 : 290;
        ResizeMode = p.StripMode ? ResizeMode.CanResize : ResizeMode.CanResizeWithGrip;
    }
    internal void ChangeMode(bool collapsed, bool strip)
    {
        RevealFromEdge(false); frameTimer.Stop(); SaveFrame(); resizingMode = true;
        app.Commit(b => { b.Preferences.Collapsed = collapsed; b.Preferences.StripMode = strip; }, false);
        var p = app.Store.Book.Preferences;
        SetSizeLimits();
        Width = p.StripMode ? p.StripWidth : (p.Collapsed ? p.CompactWidth : p.ExpandedWidth);
        Height = p.StripMode ? 30 : (p.Collapsed ? p.CompactHeight : p.ExpandedHeight);
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
        Width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, end.X - origin.X));
        Height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, end.Y - origin.Y));
        Left = Math.Clamp(Left, origin.X, Math.Max(origin.X, end.X - Width));
        Top = Math.Clamp(Top, origin.Y, Math.Max(origin.Y, end.Y - Height));
    }
    private void ScheduleFrame() { if (!IsLoaded || resizingMode) return; frameTimer.Stop(); frameTimer.Start(); }
    internal void SaveFrame()
    {
        if (!IsLoaded || resizingMode || app.Store.LoadError != null) return;
        app.Commit(b =>
        {
            var frame = dockHidden && dockFrame.HasValue ? dockFrame.Value : new Rect(Left, Top, Width, Height);
            var p = b.Preferences; p.Left = frame.Left; p.Top = frame.Top;
            if (p.StripMode) p.StripWidth = frame.Width;
            else if (p.Collapsed) { p.CompactWidth = frame.Width; p.CompactHeight = frame.Height; }
            else { p.ExpandedWidth = frame.Width; p.ExpandedHeight = frame.Height; }
        }, false);
    }
    internal void Fade()
    {
        var p = app.Store.Book.Preferences;
        edgeHandle.Opacity = Math.Clamp(p.IdleOpacity, .65, 1);
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
            string? edge = Left <= work.Left + 18 ? "left" : (Left + Width >= work.Right - 18 ? "right" : null);
            app.Commit(b => b.Preferences.DockEdge = edge, false);
            if (edge != null)
            {
                resizingMode = true;
                Left = edge == "left" ? work.Left : work.Right - Width;
                Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
                resizingMode = false;
            }
            dockFrame = edge == null ? null : new Rect(Left, Top, Width, Height);
            Fade(); SaveFrame();
        }
        var visibleWindow = dockHidden ? edgeHandle : this;
        if (pressing || userDragging || editingItem || !visibleWindow.IsVisible || !IsEnabled || app.Store.Book.Preferences.DockEdge == null || !dockFrame.HasValue) return;
        Native.GetCursorPos(out var cursor);
        var local = visibleWindow.PointFromScreen(new Point(cursor.X, cursor.Y));
        if (edgeTransitioning && !dockHidden && dockFrame.HasValue)
            local = new Point(local.X + Left - dockFrame.Value.Left, local.Y + Top - dockFrame.Value.Top);
        if (new Rect(-3, -3, visibleWindow.ActualWidth + 6, visibleWindow.ActualHeight + 6).Contains(local))
        { RevealFromEdge(); }
        else
        {
            HideAtEdge();
        }
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
        // Swap windows instead of resizing the live transparent content to 8px.
        edgeHandle.Height = Math.Min(100, rest.Height);
        edgeHandle.Left = app.Store.Book.Preferences.DockEdge == "left" ? work.Left : work.Right - 8;
        edgeHandle.Top = rest.Top + (rest.Height - edgeHandle.Height) / 2;
        void Finish()
        {
            if (token != edgeMotionToken || !dockHidden) return;
            BeginAnimation(LeftProperty, null);
            Hide(); Left = rest.Left; edgeHandle.Show();
            edgeTransitioning = false; resizingMode = false; Fade();
        }
        if (!animated || !SystemParameters.ClientAreaAnimation) { Finish(); return; }
        var parked = app.Store.Book.Preferences.DockEdge == "left" ? work.Left - Width + 8 : work.Right - 8;
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
        BeginAnimation(LeftProperty, null);
        var parked = app.Store.Book.Preferences.DockEdge == "left" ? work.Left - rest.Width + 8 : work.Right - 8;
        Left = animated && SystemParameters.ClientAreaAnimation ? parked : rest.Left;
        Top = rest.Top; Width = rest.Width; Height = rest.Height;
        UpdateLayout(); Show(); edgeHandle.Hide();
        void Finish()
        {
            if (token != edgeMotionToken || dockHidden) return;
            BeginAnimation(LeftProperty, null);
            Left = rest.Left; edgeTransitioning = false; resizingMode = false; Fade();
        }
        if (!animated || !SystemParameters.ClientAreaAnimation) { Finish(); return; }
        var slide = new DoubleAnimation(parked, rest.Left, TimeSpan.FromMilliseconds(280)) {
            EasingFunction = new BackEase { Amplitude = .24, EasingMode = EasingMode.EaseOut }
        };
        slide.Completed += (_, _) => Finish();
        BeginAnimation(LeftProperty, slide);
    }
    internal void HideToTray() { Hide(); edgeHandle.Hide(); }
}

internal sealed class CaptureWindow : Window
{
    private readonly App app;
    private readonly DockPanel captureRoot = new() { Background = Brushes.Transparent, RenderTransformOrigin = new Point(.5, .5) };
    private readonly ScaleTransform captureScale = new(1, 1);
    private readonly ScaleTransform cancelScale = new(.72, .72);
    private readonly TextBox input = new() { FontSize = 20, BorderThickness = new Thickness(0), Padding = new Thickness(5), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border surface = new() { CornerRadius = new CornerRadius(32), Padding = new Thickness(22, 8, 22, 8), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    private readonly Border cancelSurface = new() { CornerRadius = new CornerRadius(32), Width = 64, Height = 64, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(12, 0, 0, 0) };
    internal CaptureWindow(App app)
    {
        this.app = app;
        Title = "Aside 快速记录"; Width = 660; Height = 64; ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var panel = new DockPanel();
        var icon = UI.Text("✎", 25); icon.Margin = new Thickness(0, 0, 14, 0); icon.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(icon, Dock.Left); panel.Children.Add(icon);
        var save = UI.Button("↵", Save); DockPanel.SetDock(save, Dock.Right); panel.Children.Add(save);
        var inputArea = new Grid();
        inputArea.Children.Add(input);
        var placeholder = new TextBlock { Text = "记下此刻的想法…", FontSize = 20, Foreground = Brushes.Gray, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        inputArea.Children.Add(placeholder);
        input.TextChanged += (_, _) => placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        panel.Children.Add(inputArea); surface.Child = panel;
        var cancel = new Button { Content = "×", FontSize = 27, Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "取消输入 · Esc" };
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
        Deactivated += (_, _) => { if (IsVisible) app.CloseCapture(false); };
        Closing += (_, e) => { if (!app.Quitting) { e.Cancel = true; app.CloseCapture(); } };
    }
    internal void PositionAtCursor()
    {
        new WindowInteropHelper(this).EnsureHandle();
        Native.GetCursorPos(out var point);
        var work = Forms.Screen.FromPoint(new System.Drawing.Point(point.X, point.Y)).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var center = transform.Transform(new Point(work.Left + work.Width / 2.0, work.Top + work.Height / 2.0));
        Left = center.X - Width / 2; Top = center.Y - Height / 2;
        ApplyAppearance();
    }
    internal void ApplyAppearance()
    {
        var color = UI.ParseColor(app.Store.Book.Preferences.Background);
        surface.Background = new SolidColorBrush(color);
        cancelSurface.Background = surface.Background;
        Foreground = color.R * .2126 + color.G * .7152 + color.B * .0722 < 115 ? Brushes.WhiteSmoke : Brushes.Black;
        input.Foreground = Foreground;
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
    internal void AnimateIn()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        captureRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(.75, 1, TimeSpan.FromMilliseconds(90)) {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        var spring = new BackEase { Amplitude = .15, EasingMode = EasingMode.EaseOut };
        captureScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.90, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = spring });
        captureScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.98, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = spring });
    }
    internal void AnimateOut(Action completed)
    {
        if (!SystemParameters.ClientAreaAnimation) { completed(); return; }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(70)) {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => completed();
        captureRoot.BeginAnimation(OpacityProperty, fade);
        captureScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.98, TimeSpan.FromMilliseconds(70)));
        captureScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.98, TimeSpan.FromMilliseconds(70)));
    }
    private void AnimateCancel(bool visible)
    {
        cancelSurface.IsHitTestVisible = visible;
        var opacity = visible ? 1.0 : 0.0;
        var scale = visible ? 1.0 : .72;
        if (!SystemParameters.ClientAreaAnimation)
        {
            cancelSurface.Opacity = opacity; cancelScale.ScaleX = cancelScale.ScaleY = scale; return;
        }
        cancelSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(160)) {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        var spring = new BackEase { Amplitude = .48, EasingMode = EasingMode.EaseOut };
        cancelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, TimeSpan.FromMilliseconds(250)) { EasingFunction = spring });
        cancelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, TimeSpan.FromMilliseconds(250)) { EasingFunction = spring });
    }
    internal void FocusInput() => Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) input.Focus(); }), DispatcherPriority.Input);
    private void Save()
    {
        if (app.Add(this, input.Text, false)) { input.Clear(); app.CloseCapture(); }
    }
}

internal sealed class SettingsWindow : Window
{
    private readonly App app;
    private readonly StackPanel stack = new() { Margin = new Thickness(22) };
    private readonly StackPanel previews = new();
    private readonly WrapPanel savedColors = new();
    internal void Refresh() => Render();
    internal SettingsWindow(App app)
    {
        this.app = app;
        Title = "Aside 设置"; Width = 450; Height = 720; Topmost = true; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Microsoft YaHei UI"); Background = Brushes.WhiteSmoke;
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Render();
    }
    private void Render()
    {
        stack.Children.Clear(); var p = app.Store.Book.Preferences;
        stack.Children.Add(UI.Text("通用", 18));
        Toggle("窗口始终置顶", p.Topmost, value => app.Commit(b => b.Preferences.Topmost = value));
        var pinCount = new DockPanel { Margin = new Thickness(3, 7, 3, 7) };
        var count = new ComboBox { ItemsSource = Enumerable.Range(1, 20).ToArray(), SelectedItem = p.PinCapacity, Width = 65 };
        count.SelectionChanged += (_, _) => app.Commit(b => b.Preferences.PinLimit = (int)count.SelectedItem);
        DockPanel.SetDock(count, Dock.Right); pinCount.Children.Add(count);
        pinCount.Children.Add(UI.Text("最多置顶条数")); stack.Children.Add(pinCount);
        stack.Children.Add(UI.Text("调低数量不会移除已有置顶；可手动取消多余置顶。", 11));
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            Toggle("登录时启动 Aside", key?.GetValue("QuietPin") != null, SetLogin);
        stack.Children.Add(UI.Text("全局快捷键"));
        var shortcut = new Button { Content = p.CustomKeyLabel ?? App.ShortcutLabels[Math.Clamp(p.Shortcut, 0, 2)], Margin = new Thickness(3), Padding = new Thickness(9, 6, 9, 6) };
        var recording = false;
        void FinishRecording()
        {
            if (!recording) return;
            recording = false;
            shortcut.Content = app.Store.Book.Preferences.CustomKeyLabel ?? App.ShortcutLabels[Math.Clamp(app.Store.Book.Preferences.Shortcut, 0, 2)];
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
        stack.Children.Add(shortcut);
        stack.Children.Add(UI.Text("快速记录保存键"));
        var submit = new ComboBox { ItemsSource = new[] { "Enter", "Ctrl + Enter" }, SelectedIndex = p.ControlEnterToSave ? 1 : 0, Margin = new Thickness(3, 3, 3, 12) };
        submit.SelectionChanged += (_, _) => app.Commit(b => b.Preferences.ControlEnterToSave = submit.SelectedIndex == 1);
        stack.Children.Add(submit);
        stack.Children.Add(UI.Text("外观", 18));
        stack.Children.Add(UI.Button("背景颜色 · 系统调色盘", () =>
        {
            var color = UI.ParseColor(app.Store.Book.Preferences.Background);
            using var dialog = new Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B) };
            if (dialog.ShowDialog() == Forms.DialogResult.OK)
                app.Commit(b => b.Preferences.Background = $"#FF{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
        }));
        stack.Children.Add(UI.Button("从屏幕取色…", StartPicker));
        stack.Children.Add(UI.Text("也可输入颜色值（#RRGGBB 或 #AARRGGBB）：", 11));
        var hex = new TextBox { Text = p.Background, Margin = new Thickness(3), Padding = new Thickness(5) };
        stack.Children.Add(hex);
        stack.Children.Add(UI.Button("应用颜色值", () =>
        {
            try { var color = (Color)ColorConverter.ConvertFromString(hex.Text); app.Commit(b => b.Preferences.Background = color.ToString()); }
            catch { MessageBox.Show("请输入有效的颜色值，例如 #FFE8E6DB。", "Aside"); }
        }));
        var palette = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
        var saveColor = UI.Button("保存颜色", () => app.Commit(b =>
        {
            var color = UI.ParseColor(b.Preferences.Background).ToString();
            if (!b.Preferences.SavedColors.Contains(color)) b.Preferences.SavedColors.Add(color);
        }));
        DockPanel.SetDock(saveColor, Dock.Left);
        palette.Children.Add(saveColor);
        if (savedColors.Parent is ScrollViewer previousPalette) previousPalette.Content = null;
        palette.Children.Add(new ScrollViewer { Content = savedColors, Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        stack.Children.Add(palette);
        Slider("鼠标离开", p.IdleOpacity, value => app.Commit(b => b.Preferences.IdleOpacity = value));
        Slider("交互时", p.ActiveOpacity, value => app.Commit(b => b.Preferences.ActiveOpacity = value));
        Slider("快速输入", p.CaptureOpacity, value => app.Commit(b => b.Preferences.CaptureOpacity = value));
        Toggle("鼠标离开时淡化", p.Fade, value => app.Commit(b => b.Preferences.Fade = value));
        UpdatePreviews();
        stack.Children.Add(previews);
        stack.Children.Add(UI.Button("恢复默认外观", () =>
        {
            app.Commit(b => { b.Preferences.Background = "#FFE8E6DB"; b.Preferences.IdleOpacity = .4; b.Preferences.ActiveOpacity = .95; b.Preferences.CaptureOpacity = .95; b.Preferences.Fade = true; }); Render();
        }));
        stack.Children.Add(UI.Text("数据仅保存在这台电脑。移动 exe 后，请重新开关登录启动以更新路径。", 11));
        var github = new Button { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Margin = new Thickness(3, 16, 3, 4) };
        github.Content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var githubContent = (StackPanel)github.Content;
        githubContent.Children.Add(new System.Windows.Controls.Image {
            Source = new BitmapImage(new Uri("pack://application:,,,/assets/GitHub_Invertocat_Black.png")),
            Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center
        });
        var githubLabel = UI.Text("GitHub 项目主页 ↗", 12);
        githubLabel.Foreground = Brushes.RoyalBlue;
        githubLabel.TextDecorations = TextDecorations.Underline;
        githubLabel.HorizontalAlignment = HorizontalAlignment.Center;
        githubLabel.Margin = new Thickness(0, 5, 0, 0);
        githubContent.Children.Add(githubLabel);
        var support = UI.Text("喜欢的话来点个⭐️支持作者吧，谢谢大家～", 11);
        support.HorizontalAlignment = HorizontalAlignment.Center;
        support.Margin = new Thickness(0, 5, 0, 0);
        githubContent.Children.Add(support);
        github.Click += (_, _) => {
            try { Process.Start(new ProcessStartInfo("https://github.com/savannahliz/aside") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("无法打开 GitHub：" + ex.Message, "Aside"); }
        };
        System.Windows.Automation.AutomationProperties.SetName(github, "打开 Aside 的 GitHub 仓库");
        stack.Children.Add(github);
    }
    private void Toggle(string title, bool value, Action<bool> change)
    {
        var toggle = new CheckBox { Content = title, IsChecked = value, Margin = new Thickness(3, 7, 3, 7) };
        toggle.Click += (_, _) => change(toggle.IsChecked == true); stack.Children.Add(toggle);
    }
    internal void UpdatePreviews()
    {
        savedColors.Children.Clear();
        foreach (var color in app.Store.Book.Preferences.SavedColors)
        {
            var swatch = new Button { Width = 28, Height = 28, Margin = new Thickness(3), Background = new SolidColorBrush(UI.ParseColor(color)), ToolTip = color + " · 右键删除" };
            System.Windows.Automation.AutomationProperties.SetName(swatch, "收藏颜色 " + color);
            swatch.Click += (_, _) => app.Commit(b => b.Preferences.Background = color);
            var menu = new ContextMenu();
            var remove = new MenuItem { Header = "删除保存的颜色" };
            remove.Click += (_, _) => app.Commit(b => b.Preferences.SavedColors.Remove(color));
            menu.Items.Add(remove); swatch.ContextMenu = menu;
            savedColors.Children.Add(swatch);
        }
        if (savedColors.Children.Count == 0) savedColors.Children.Add(UI.Text("保存后显示在这里\n点击使用 · 右键删除", 11));
        previews.Children.Clear();
        var p = app.Store.Book.Preferences;
        previews.Children.Add(UI.Text("实时预览", 13));
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
        var column = new StackPanel { Margin = new Thickness(3) };
        column.Children.Add(UI.Text($"{label}  {opacity:P0}", 11));
        var tiles = new DrawingGroup();
        tiles.Children.Add(new GeometryDrawing(Brushes.Gainsboro, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        tiles.Children.Add(new GeometryDrawing(Brushes.WhiteSmoke, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        tiles.Children.Add(new GeometryDrawing(Brushes.WhiteSmoke, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var board = new Grid { Height = capturePreview ? 58 : 76, Background = new DrawingBrush(tiles) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute } };
        var color = UI.ParseColor(app.Store.Book.Preferences.Background);
        var foreground = color.R * .2126 + color.G * .7152 + color.B * .0722 < 115 ? Brushes.WhiteSmoke : Brushes.Black;
        var body = new DockPanel { Margin = new Thickness(8), Opacity = opacity };
        if (capturePreview)
        {
            var cancel = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Margin = new Thickness(6, 0, 0, 0), Background = new SolidColorBrush(color), Child = new TextBlock { Text = "×", FontSize = 18, Foreground = foreground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            DockPanel.SetDock(cancel, Dock.Right); body.Children.Add(cancel);
        }
        var text = new TextBlock { Text = capturePreview ? "✎  记下此刻的想法…" : "Inbox\n○ 一件要记住的事", FontSize = 11, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        body.Children.Add(new Border { CornerRadius = new CornerRadius(capturePreview ? 20 : 8), Background = new SolidColorBrush(color), Child = text });
        board.Children.Add(body); column.Children.Add(board);
        return column;
    }
    private void Slider(string title, double value, Action<double> change)
    {
        var label = UI.Text($"{title}  {value:P0}"); stack.Children.Add(label);
        var slider = new Slider { Minimum = .15, Maximum = 1, Value = Math.Clamp(value, .15, 1), Margin = new Thickness(3), TickFrequency = .05 };
        slider.ValueChanged += (_, _) => { label.Text = $"{title}  {slider.Value:P0}"; change(slider.Value); };
        stack.Children.Add(slider);
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
