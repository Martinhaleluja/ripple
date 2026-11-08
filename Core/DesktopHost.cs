using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;

namespace Ripple.Core;

/// <summary>One monitor as Ripple sees it, in physical pixels.</summary>
internal readonly record struct MonitorInfo(Native.RECT Bounds, double Scale, int RefreshRate);

/// <summary>
/// Owns the windows that sit behind the desktop icons.
///
/// Those windows become children of Explorer's desktop, which ties our message queue
/// to Explorer's: a hang here could freeze the desktop. So they live on this thread,
/// which does nothing but pump messages. All drawing happens on the engine's thread.
///
/// Windows has two desktop layouts:
///   Windows 11 24H2 and later: Progman holds the icons (SHELLDLL_DefView) and a WorkerW
///   as direct children. Our window goes between them, and must be layered to composite.
///   Before that: the icons live in a top-level WorkerW and the wallpaper in another
///   WorkerW just behind it, which becomes our parent.
///
/// If Ripple crashes, Windows destroys these windows with the process and the normal
/// wallpaper shows again, so a crash cannot leave the desktop broken.
/// </summary>
internal sealed class DesktopHost : IDisposable
{
    private const string ChildClass = "RippleWaterSurface";
    private const string ListenerClass = "RippleListener";

    private readonly Dispatcher _dispatcher;
    private readonly Native.WndProc _childProc;
    private readonly Native.WndProc _listenerProc;
    private readonly uint _taskbarCreated;
    private readonly Action _onDesktopChanged;
    private readonly Action _onWallpaperChanged;
    private readonly List<IntPtr> _windows = new();
    private IntPtr _listener;
    private IntPtr _parent;
    private IntPtr _defView;
    private IntPtr _workerBehind;

    public DesktopHost(Action onDesktopChanged, Action onWallpaperChanged)
    {
        _onDesktopChanged = onDesktopChanged;
        _onWallpaperChanged = onWallpaperChanged;
        _childProc = ChildProc;
        _listenerProc = ListenerProc;
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            Register();
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "Ripple desktop windows" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
    }

    public bool NewLayout { get; private set; }

    /// <summary>True while the windows are still attached to a living desktop.</summary>
    public bool IsAttached => _parent != IntPtr.Zero && Native.IsWindow(_parent);

    /// <summary>Finds the desktop and creates one hidden window per monitor.</summary>
    public IntPtr[] Attach(IReadOnlyList<MonitorInfo> monitors) => _dispatcher.Invoke(() =>
    {
        DetachOnThisThread();
        if (!FindDesktop())
        {
            return Array.Empty<IntPtr>();
        }

        var result = new IntPtr[monitors.Count];
        for (var i = 0; i < monitors.Count; i++)
        {
            result[i] = CreateSurfaceWindow(monitors[i].Bounds);
            _windows.Add(result[i]);
        }

        return result;
    });

    /// <summary>Shows windows once they already hold a picture, so nothing flashes.</summary>
    public void Show(IEnumerable<IntPtr> windows) => _dispatcher.Invoke(() =>
    {
        foreach (var window in windows)
        {
            Native.ShowWindow(window, Native.SW_SHOWNOACTIVATE);
        }

        KeepBehindIcons();
    });

    public void Detach() => _dispatcher.Invoke(DetachOnThisThread);

    public void Dispose()
    {
        _dispatcher.Invoke(() =>
        {
            DetachOnThisThread();
            if (_listener != IntPtr.Zero)
            {
                Native.DestroyWindow(_listener);
            }
        });
        _dispatcher.InvokeShutdown();
    }

    private void Register()
    {
        var instance = Native.GetModuleHandle(null);
        var child = new Native.WNDCLASSEX
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_childProc),
            hInstance = instance,
            lpszClassName = ChildClass,
        };
        Native.RegisterClassEx(ref child);

        var listener = child;
        listener.lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_listenerProc);
        listener.lpszClassName = ListenerClass;
        Native.RegisterClassEx(ref listener);

        // A hidden top-level window: only top-level windows hear when Explorer restarts,
        // when displays change or when the Windows wallpaper changes.
        _listener = Native.CreateWindowEx(Native.WS_EX_TOOLWINDOW, ListenerClass, "Ripple", Native.WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
    }

    private bool FindDesktop()
    {
        var progman = Native.FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            Log.Info("Desktop not found: there is no Progman window.");
            return false;
        }

        // Asks Explorer to split the wallpaper out from the icons.
        Native.SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), IntPtr.Zero, Native.SMTO_NORMAL, 1000, out _);
        Native.SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), Native.SMTO_NORMAL, 1000, out _);

        _defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (_defView != IntPtr.Zero)
        {
            NewLayout = true;
            _parent = progman;
            _workerBehind = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            Log.Info($"Desktop layout: new (24H2). Progman {progman:X}, icons {_defView:X}, WorkerW {_workerBehind:X}.");
            return true;
        }

        NewLayout = false;
        var worker = IntPtr.Zero;
        Native.EnumWindows((top, _) =>
        {
            var view = Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view == IntPtr.Zero)
            {
                return true;
            }

            _defView = view;
            worker = Native.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            return false;
        }, IntPtr.Zero);

        _parent = worker != IntPtr.Zero ? worker : progman;
        _workerBehind = IntPtr.Zero;
        Log.Info($"Desktop layout: classic. Parent {_parent:X}, icons {_defView:X}.");
        return _defView != IntPtr.Zero;
    }

    private IntPtr CreateSurfaceWindow(Native.RECT bounds)
    {
        // Disabled and hit-test transparent: clicks can never land on the water. Started
        // as a hidden popup so it has no moment as a child of the wrong DPI context.
        var exStyle = Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
        if (NewLayout)
        {
            exStyle |= Native.WS_EX_LAYERED;
        }

        var window = Native.CreateWindowEx(exStyle, ChildClass, "Ripple water", Native.WS_POPUP | Native.WS_DISABLED,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            throw new InvalidOperationException("The water window could not be created.");
        }

        if (NewLayout)
        {
            Native.SetLayeredWindowAttributes(window, 0, 255, Native.LWA_ALPHA);
        }

        var style = (uint)(long)Native.GetWindowLongPtr(window, Native.GWL_STYLE);
        style = (style & ~Native.WS_POPUP) | Native.WS_CHILD | Native.WS_CLIPSIBLINGS;
        Native.SetWindowLongPtr(window, Native.GWL_STYLE, new IntPtr(style));
        if (Native.SetParent(window, _parent) == IntPtr.Zero)
        {
            Log.Info($"SetParent reported error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}.");
        }

        var origin = new Native.POINT { X = bounds.Left, Y = bounds.Top };
        Native.ScreenToClient(_parent, ref origin);
        Native.SetWindowPos(window, IntPtr.Zero, origin.X, origin.Y, bounds.Width, bounds.Height, Native.SWP_NOACTIVATE);
        return window;
    }

    private void KeepBehindIcons()
    {
        if (!NewLayout)
        {
            return;
        }

        // Icons on top, then the water, then Explorer's own wallpaper layer.
        var above = _defView;
        foreach (var window in _windows)
        {
            Native.SetWindowPos(window, above, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            above = window;
        }

        if (_workerBehind != IntPtr.Zero)
        {
            Native.SetWindowPos(_workerBehind, above, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    private void DetachOnThisThread()
    {
        var hadWindows = _windows.Count > 0;
        foreach (var window in _windows)
        {
            Native.DestroyWindow(window);
        }

        _windows.Clear();

        // Ask the desktop to repaint so nothing of the water lingers.
        if (hadWindows && _parent != IntPtr.Zero && Native.IsWindow(_parent))
        {
            Native.RedrawWindow(_parent, IntPtr.Zero, IntPtr.Zero, Native.RDW_INVALIDATE | Native.RDW_ERASE | Native.RDW_ALLCHILDREN | Native.RDW_UPDATENOW);
        }

        _parent = IntPtr.Zero;
    }

    private IntPtr ChildProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_NCHITTEST:
                return new IntPtr(Native.HTTRANSPARENT);
            case Native.WM_ERASEBKGND:
                return new IntPtr(1);
            case Native.WM_PAINT:
                Native.ValidateRect(hwnd, IntPtr.Zero);
                return IntPtr.Zero;
            default:
                return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private IntPtr ListenerProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreated || msg == Native.WM_DISPLAYCHANGE)
        {
            _onDesktopChanged();
        }
        else if (msg == Native.WM_SETTINGCHANGE && (uint)wParam == Native.SPI_SETDESKWALLPAPER)
        {
            _onWallpaperChanged();
        }

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
