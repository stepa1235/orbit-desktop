using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace OrbitDesktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? fixture = args.SkipWhile(a => a != "--fixture").Skip(1).FirstOrDefault();
        using var mutex = new Mutex(true, fixture is null ? @"Local\OrbitDesktop.Production" : @"Local\OrbitDesktop.Fixture", out bool owner);
        if (!owner)
        {
            MessageBox.Show("Orbit уже запущен. Настройки и выход доступны через значок Orbit рядом с часами.", "Orbit");
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string? capture = args.SkipWhile(a => a != "--capture").Skip(1).FirstOrDefault();
        app.DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbit Desktop");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "error.log"), DateTimeOffset.Now + Environment.NewLine + e.Exception + Environment.NewLine);
            }
            catch (Exception) { }
            MessageBox.Show("Произошла ошибка. Рабочий стол Windows будет восстановлен.", "Orbit");
            e.Handled = true;
            app.Shutdown(1);
        };
        using var controller = new Controller(fixture);
        if (capture is not null)
        {
            var captureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            captureTimer.Tick += (_, _) =>
            {
                captureTimer.Stop();
                controller.Capture(capture);
                app.Shutdown();
            };
            captureTimer.Start();
        }
        app.Run();
        return 0;
    }
}

internal sealed class Controller : IDisposable
{
    private readonly Catalog catalog;
    private readonly Forms.NotifyIcon tray;
    private readonly DispatcherTimer check;
    private readonly DesktopShell.DesktopEvents desktopEvents;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly System.Collections.Generic.List<Garden> views = [];
    private readonly string? fixture;
    private IntPtr host;
    private string geometry = "";
    private bool paused;
    private bool disposed;
    private bool desktopChangePending;
    private Window? settings;
    private Window? search;

    private sealed record Garden(Forms.Screen Monitor, HwndSource Source, Galaxy Galaxy);

    internal Controller(string? fixture)
    {
        this.fixture = fixture;
        catalog = new Catalog(fixture);
        catalog.Changed += Refresh;
        tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Orbit Desktop",
            Visible = true
        };
        tray.ContextMenuStrip = new Forms.ContextMenuStrip();
        tray.ContextMenuStrip.Items.Add("Поиск", null, (_, _) => Search(Primary()));
        tray.ContextMenuStrip.Items.Add("Настройки", null, (_, _) => Settings(Primary()));
        tray.ContextMenuStrip.Items.Add("Показать или скрыть Orbit", null, (_, _) => Toggle());
        tray.ContextMenuStrip.Items.Add("Выход и возврат рабочего стола", null, (_, _) => Application.Current.Shutdown());
        tray.DoubleClick += (_, _) => Settings(Primary());

        check = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        check.Tick += (_, _) => Reconcile();
        check.Start();
        desktopEvents = new DesktopShell.DesktopEvents(QueueDesktopChange, QueueDesktopChange);
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        Reconcile();
    }

    private void DisplayChanged(object? sender, EventArgs args) => dispatcher.BeginInvoke(Reconcile);

    private void QueueDesktopChange()
    {
        if (disposed || paused || desktopChangePending) return;
        desktopChangePending = true;
        dispatcher.BeginInvoke(DispatcherPriority.Send, DesktopChanged);
    }

    private void DesktopChanged()
    {
        desktopChangePending = false;
        if (disposed || paused) return;
        foreach (var view in views)
        {
            view.Galaxy.UpdateCoverage();
            if (host != IntPtr.Zero)
                DesktopShell.Position(view.Source.Handle, host, view.Monitor);
        }
    }

    private Galaxy? Primary() => views.FirstOrDefault(view => view.Monitor.Primary)?.Galaxy ??
                                 views.FirstOrDefault()?.Galaxy;

    internal void Capture(string path)
    {
        var view = Primary() ?? throw new InvalidOperationException("Desktop was not located.");
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private void Reconcile()
    {
        if (disposed || paused) return;
        var desktop = DesktopShell.Locate();
        var screens = Forms.Screen.AllScreens;
        string layout = string.Join("|", screens.Select(s => $"{s.DeviceName}:{s.Bounds}:{s.WorkingArea}").Order());
        if (desktop == host && geometry == layout && views.Count == screens.Length &&
            views.All(view => DesktopShell.IsWindow(view.Source.Handle)))
        {
            foreach (var view in views)
                DesktopShell.Position(view.Source.Handle, desktop, view.Monitor);
            return;
        }

        TearDown();
        host = desktop;
        desktopEvents.WatchDesktop(host);
        geometry = layout;
        if (host == IntPtr.Zero)
        {
            tray.Text = "Orbit · ожидание Проводника";
            return;
        }

        foreach (var monitor in screens)
        {
            var area = monitor.WorkingArea;
            if (area.Width < 1 || area.Height < 1) continue;
            var parameters = new HwndSourceParameters("Orbit Desktop — " + monitor.DeviceName, area.Width, area.Height)
            {
                // WS_POPUP makes this HWND owned by the desktop rather than a child of it.
                ParentWindow = host,
                WindowStyle = DesktopShell.WsPopup,
                ExtendedWindowStyle = DesktopShell.ExToolWindow | DesktopShell.ExNoActivate,
                AcquireHwndFocusInMenuMode = true
            };
            var source = new HwndSource(parameters);
            Galaxy? galaxy = null;
            galaxy = new Galaxy(catalog, () => DesktopShell.HasForegroundApp(host, source.Handle, monitor),
                () => Search(galaxy), () => Settings(galaxy), NotifyError);
            galaxy.SlotsChanged += Refresh;
            source.RootVisual = galaxy;
            DesktopShell.Position(source.Handle, host, monitor);
            galaxy.Start();
            views.Add(new Garden(monitor, source, galaxy));
        }
        tray.Text = "Orbit Desktop";
    }

    private void Toggle()
    {
        paused = !paused;
        if (paused) TearDown();
        else Reconcile();
    }

    private void Refresh()
    {
        if (disposed) return;
        foreach (var view in views) view.Galaxy.Reload();
    }

    private void Save()
    {
        try { catalog.Save(); }
        catch (IOException) { NotifyError(); }
        catch (UnauthorizedAccessException) { NotifyError(); }
        Refresh();
    }

    private void NotifyError()
    {
        tray.ShowBalloonTip(3500, "Orbit Desktop", "Windows не смогла выполнить действие. Проверьте файл или права доступа.", Forms.ToolTipIcon.Info);
    }

    private void Search(Galaxy? origin)
    {
        if (search is not null) { search.Activate(); return; }
        var target = origin ?? Primary();
        search = Ui.Search(catalog, item => target?.FocusItem(item), NotifyError);
        search.Closed += (_, _) => search = null;
        Place(search, target);
        search.Show();
        search.Activate();
    }

    private void Settings(Galaxy? origin)
    {
        if (settings is not null) { settings.Activate(); return; }
        var target = origin ?? Primary();
        settings = Ui.Settings(catalog, Save, Toggle, () => Application.Current.Shutdown(), () =>
        {
            foreach (var view in views) view.Galaxy.ResetCamera();
        }, () => ChangeStartup(!StartupEnabled), StartupEnabled, paused, fixture is null, RenderCapability.Tier >> 16);
        settings.Closed += (_, _) => settings = null;
        Place(settings, target);
        settings.Show();
        settings.Activate();
    }

    private void Place(Window popup, Galaxy? target)
    {
        if (target is null) return;
        var view = views.FirstOrDefault(view => ReferenceEquals(view.Galaxy, target));
        if (view is null) return;
        var area = view.Monitor.WorkingArea;
        popup.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(popup).Handle;
            var dpi = VisualTreeHelper.GetDpi(target);
            int x = area.Left + (int)((area.Width - popup.Width * dpi.DpiScaleX) / 2);
            int y = area.Top + (int)((area.Height - popup.Height * dpi.DpiScaleY) / 2);
            DesktopShell.MovePopup(hwnd, Math.Max(x, area.Left), Math.Max(y, area.Top));
        };
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupName = "OrbitDesktop";
    private static bool StartupEnabled => Registry.CurrentUser.OpenSubKey(RunKey) is RegistryKey key && ReadStartup(key);
    private static bool ReadStartup(RegistryKey key)
    {
        using (key) return key.GetValue(StartupName) is string;
    }

    private void ChangeStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(StartupName, "\"" + Environment.ProcessPath + "\"");
            else key.DeleteValue(StartupName, false);
        }
        catch (UnauthorizedAccessException) { NotifyError(); }
        catch (IOException) { NotifyError(); }
    }

    private void TearDown()
    {
        foreach (var view in views)
        {
            view.Galaxy.Stop();
            view.Source.Dispose();
        }
        views.Clear();
        host = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        desktopEvents.Dispose();
        check.Stop();
        TearDown();
        tray.Visible = false;
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
        catalog.Dispose();
    }
}
