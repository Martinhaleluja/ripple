using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Ripple.Core;
using Ripple.UI;

namespace Ripple;

public partial class App : Application
{
    private const string InstanceName = @"Local\RippleWallpaper-SingleInstance";
    private const string OpenEventName = @"Local\RippleWallpaper-Open";
    private const string QuitEventName = @"Local\RippleWallpaper-Quit";

    private Mutex? _instance;
    private EventWaitHandle? _openEvent;
    private RegisteredWaitHandle? _openWait;
    private EventWaitHandle? _quitEvent;
    private RegisteredWaitHandle? _quitWait;
    private WaterEngine? _engine;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private RippleSettings _settings = new();
    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var arguments = e.Args;
        var assetsAt = Array.IndexOf(arguments, "--write-assets");
        if (assetsAt >= 0)
        {
            LogoArt.WriteAssets(arguments.ElementAtOrDefault(assetsAt + 1) ?? "Assets");
            Shutdown();
            return;
        }

        _instance = new Mutex(true, InstanceName, out var first);
        _openEvent = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEventName);
        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        if (!first)
        {
            // Already running: bring up its window, or ask it to quit.
            (arguments.Contains("--quit") ? _quitEvent : _openEvent).Set();
            Shutdown();
            return;
        }

        if (arguments.Contains("--quit"))
        {
            // Nothing is running, so there's nothing to quit.
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Info($"Unexpected error: {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Info($"Fatal error: {args.ExceptionObject}");

        _settings = RippleSettings.Load();
        _engine = new WaterEngine(_settings, arguments.Contains("--demo"));
        _engine.PauseChanged += reason => Dispatcher.BeginInvoke(() => ShowState(reason));
        _tray = new TrayIcon(OpenWindow, TogglePause, Quit);
        ShowState(_settings.Paused ? "Paused" : "");

        _openWait = ThreadPool.RegisterWaitForSingleObject(_openEvent, (_, _) => Dispatcher.BeginInvoke(OpenWindow), null, Timeout.Infinite, false);

        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitEvent, (_, _) => Dispatcher.BeginInvoke(Quit), null, Timeout.Infinite, false);
        SystemEvents.PowerModeChanged += OnPowerChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        if (!arguments.Contains("--background"))
        {
            OpenWindow();
        }
    }

    public RippleSettings Settings => _settings;

    public string PauseReason => _engine?.PauseReason ?? "";

    /// <summary>Applies a change straight away; saving waits a moment so a dragged slider isn't written 60 times.</summary>
    public void Change(Func<RippleSettings, RippleSettings> change)
    {
        _settings = change(_settings);
        _engine?.Apply(_settings);

        _saveTimer ??= CreateSaveTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void TogglePause()
    {
        Change(s => s with { Paused = !s.Paused });
        ShowState(_settings.Paused ? "Paused" : "");
    }

    public void Quit()
    {
        _saveTimer?.Stop();
        _settings.Save();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _openWait?.Unregister(null);
        _quitWait?.Unregister(null);
        _quitEvent?.Dispose();
        _tray?.Dispose();
        _engine?.Dispose();
        _openEvent?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private System.Windows.Threading.DispatcherTimer CreateSaveTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _settings.Save();
        };
        return timer;
    }

    private void OpenWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _window.ShowState(PauseReason.Length > 0 ? PauseReason : (_settings.Paused ? "Paused" : ""));
    }

    private void ShowState(string reason)
    {
        _tray?.ShowState(_settings.Paused, reason);
        _window?.ShowState(reason);
    }

    private void OnPowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _engine?.RequestRebuild();
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            _engine?.RequestRebuild();
        }
    }
}
