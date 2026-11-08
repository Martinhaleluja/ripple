using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ripple.Core;

/// <summary>
/// The wallpaper window never receives input, because the icons sit on top of it, so
/// the mouse is read with a low-level hook on its own quiet thread. The callback only
/// notes the position: a slow hook would make the whole PC's mouse feel laggy.
/// Only positions and button presses are read. Nothing is stored or blocked.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    private const uint ReinstallMessage = Native.WM_APP + 1;

    private readonly AutoResetEvent _wake;
    private readonly ConcurrentQueue<Native.POINT> _presses = new();
    private readonly Native.HookProc _callback;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private uint _threadId;
    private IntPtr _hook;
    private long _position;
    private int _wakePending;

    public MouseHook(AutoResetEvent wake)
    {
        _wake = wake;
        _callback = OnMouse;
        _thread = new Thread(Pump) { IsBackground = true, Name = "Ripple mouse", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _started.Wait();
    }

    /// <summary>The latest cursor position in physical pixels.</summary>
    public Native.POINT Position
    {
        get
        {
            var packed = Volatile.Read(ref _position);
            return new Native.POINT { X = (int)(packed >> 32), Y = (int)packed };
        }
    }

    public bool TryTakePress(out Native.POINT point) => _presses.TryDequeue(out point);

    /// <summary>Tells the hook another wake-up may be sent.</summary>
    public void MarkAwake() => Interlocked.Exchange(ref _wakePending, 0);

    /// <summary>Windows can silently drop a hook across sleep and resume, so put it back.</summary>
    public void Reinstall() => Native.PostThreadMessage(_threadId, ReinstallMessage, IntPtr.Zero, IntPtr.Zero);

    public void Dispose()
    {
        Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }

    private void Pump()
    {
        _threadId = Native.GetCurrentThreadId();
        Install();
        _started.Set();
        while (Native.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.message == ReinstallMessage)
            {
                Install();
            }
        }

        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
        }
    }

    private void Install()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
        }

        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _callback, Native.GetModuleHandle(null), 0);
        Log.Info(_hook != IntPtr.Zero ? "Mouse hook installed." : "Mouse hook failed; the water will not react to the mouse.");
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            var message = (uint)wParam;
            if (message == Native.WM_MOUSEMOVE)
            {
                Volatile.Write(ref _position, ((long)data.pt.X << 32) | (uint)data.pt.Y);
                Signal();
            }
            else if (message is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN)
            {
                Volatile.Write(ref _position, ((long)data.pt.X << 32) | (uint)data.pt.Y);
                _presses.Enqueue(data.pt);
                Signal();
            }
        }

        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void Signal()
    {
        if (Interlocked.Exchange(ref _wakePending, 1) == 0)
        {
            _wake.Set();
        }
    }
}
