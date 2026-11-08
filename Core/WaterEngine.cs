using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Rectangle = System.Drawing.Rectangle;
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;

namespace Ripple.Core;

/// <summary>
/// Runs the water on its own thread with Direct3D 11. Direct3D rather than WPF because
/// the water has to live inside Explorer's desktop window behind the icons, where WPF
/// cannot draw, and because the simulation fits the graphics card: it is cheap to run
/// and costs nothing on the CPU.
///
/// When the water is calm this thread sleeps. Moving the mouse over a window that
/// covers the desktop does not wake the water at all.
/// </summary>
public sealed class WaterEngine : IDisposable
{
    private const int StepsPerSecond = 180;
    private const int MaxStepsPerFrame = 6;
    private const int MaxStamps = 16;
    private const float CalmSlopePixels = 0.2f;

    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Surface> _surfaces = new();
    private readonly List<Droplet> _droplets = new();
    private readonly Random _random = new();
    private readonly Stamp[] _stampScratch = new Stamp[MaxStamps];
    private readonly bool _demo;

    private DesktopHost? _host;
    private MouseHook? _mouse;
    private volatile RippleSettings _settings;
    private volatile bool _quit;
    private volatile bool _rebuildRequested = true;
    private volatile bool _wallpaperRequested;
    private volatile bool _hookRequested;
    private string _pauseReason = "";

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIFactory2? _factory;
    private ID3D11VertexShader? _vertex;
    private ID3D11PixelShader? _inject;
    private ID3D11PixelShader? _step;
    private ID3D11PixelShader? _surfacePass;
    private ID3D11PixelShader? _draw;
    private ID3D11Buffer? _stepConstants;
    private ID3D11Buffer? _injectConstants;
    private ID3D11Buffer? _drawConstants;
    private ID3D11SamplerState? _surfaceSampler;
    private ID3D11SamplerState? _wallpaperSampler;

    private Native.POINT _lastCursor;
    private bool _haveCursor;
    private double _stepDebt;
    private double _lastFrameTime;
    private double _nextDemoSplash;
    private double _demoEnds;
    private double _statsSince;
    private int _statsFrames;

    /// <summary>Raised on the engine's thread when the water starts or stops being paused.</summary>
    public event Action<string>? PauseChanged;

    public WaterEngine(RippleSettings settings, bool demo)
    {
        _settings = settings;
        _demo = demo;
        _thread = new Thread(Run) { IsBackground = true, Name = "Ripple water" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Why the water is paused right now, or empty if it isn't.</summary>
    public string PauseReason => Volatile.Read(ref _pauseReason);

    public void Apply(RippleSettings settings)
    {
        var old = _settings;
        _settings = settings;
        if (old.WallpaperPath != settings.WallpaperPath || old.Fit != settings.Fit)
        {
            _wallpaperRequested = true;
        }

        if (old.Detail != settings.Detail)
        {
            _rebuildRequested = true;
        }

        _wake.Set();
    }

    public void RequestRebuild()
    {
        _rebuildRequested = true;
        _hookRequested = true;
        _wake.Set();
    }

    public void Dispose()
    {
        _quit = true;
        _wake.Set();
        _thread.Join(4000);
    }

    private void Run()
    {
        try
        {
            _mouse = new MouseHook(_wake);
            _host = new DesktopHost(RequestRebuild, () =>
            {
                if (_settings.WallpaperPath is null)
                {
                    _wallpaperRequested = true;
                    _wake.Set();
                }
            });

            if (_demo)
            {
                _demoEnds = _clock.Elapsed.TotalSeconds + 25;
                Log.Info("Demo mode: dropping splashes for 25 seconds.");
            }

            var nextRuleCheck = 0d;
            while (!_quit)
            {
                try
                {
                    if (_rebuildRequested)
                    {
                        Rebuild();
                    }

                    if (_wallpaperRequested)
                    {
                        _wallpaperRequested = false;
                        LoadWallpapers();
                    }

                    if (_hookRequested)
                    {
                        _hookRequested = false;
                        _mouse.Reinstall();
                    }

                    var now = _clock.Elapsed.TotalSeconds;
                    if (now >= nextRuleCheck)
                    {
                        nextRuleCheck = now + 2;
                        CheckRules();
                    }

                    var active = Frame(now);
                    if (!active)
                    {
                        _lastFrameTime = 0;
                        _stepDebt = 0;

                        // Asleep. Rules and the desktop's health are looked at every two
                        // seconds; everything else wakes us. The short nap first caps
                        // wake-ups while a mouse moves over some other window.
                        Thread.Sleep(15);
                        _mouse.MarkAwake();
                        _wake.WaitOne(_demo && now < _demoEnds ? 50 : 2000);
                    }
                }
                catch (Exception exception) when (IsGraphicsLoss(exception))
                {
                    Log.Info($"Graphics reset, rebuilding: {exception.Message}");
                    _rebuildRequested = true;
                    ReleaseEverything();
                    Thread.Sleep(1000);
                }
            }
        }
        catch (Exception exception)
        {
            Log.Info($"The water stopped: {exception}");
        }
        finally
        {
            ReleaseEverything();
            _host?.Dispose();
            _mouse?.Dispose();
            Log.Info("Water stopped and desktop handed back.");
        }
    }

    private static bool IsGraphicsLoss(Exception exception) =>
        exception is SharpGen.Runtime.SharpGenException or COMException or InvalidOperationException;

    // ---- The desktop and the graphics card ----

    private void Rebuild()
    {
        _rebuildRequested = false;
        ReleaseEverything();

        var monitors = FindMonitors();
        Log.Info($"Monitors: {string.Join(", ", monitors.Select(m => $"{m.Bounds.Width}x{m.Bounds.Height}@{m.Bounds.Left},{m.Bounds.Top} scale {m.Scale:0.##} {m.RefreshRate} Hz"))}");

        var windows = _host!.Attach(monitors);
        if (windows.Length == 0)
        {
            // Explorer may still be starting. Try again shortly.
            Thread.Sleep(1500);
            _rebuildRequested = true;
            return;
        }

        CreateDevice();
        for (var i = 0; i < monitors.Count; i++)
        {
            _surfaces.Add(new Surface(monitors[i], windows[i]));
        }

        foreach (var surface in _surfaces)
        {
            CreateSurfaceResources(surface);
        }

        LoadWallpapers(showWindows: true);
    }

    private static List<MonitorInfo> FindMonitors()
    {
        var found = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(monitor, ref info))
            {
                var scale = Native.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96d : 1d;
                found.Add(new MonitorInfo(info.rcMonitor, scale, RefreshRateOf(info.szDevice)));
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static int RefreshRateOf(string device)
    {
        var mode = new Native.DEVMODE { dmSize = (short)Marshal.SizeOf<Native.DEVMODE>() };
        return Native.EnumDisplaySettings(device, -1, ref mode) && mode.dmDisplayFrequency > 1 ? mode.dmDisplayFrequency : 60;
    }

    private void CreateDevice()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out _device, out var level, out _context).CheckError();

        using (var dxgiDevice = _device!.QueryInterface<IDXGIDevice1>())
        {
            dxgiDevice.SetMaximumFrameLatency(1);
            using var adapter = dxgiDevice.GetAdapter();
            _factory = adapter.GetParent<IDXGIFactory2>();
            Log.Info($"Graphics: {adapter.Description.Description}, feature level {level}.");
        }

        var source = ReadShaderSource();
        var vertexCode = ShaderCompiler.Compile(source, "VS", "vs_4_0");
        _vertex = _device.CreateVertexShader(vertexCode);
        _inject = _device.CreatePixelShader(ShaderCompiler.Compile(source, "PS_Inject", "ps_4_0"));
        _step = _device.CreatePixelShader(ShaderCompiler.Compile(source, "PS_Step", "ps_4_0"));
        _surfacePass = _device.CreatePixelShader(ShaderCompiler.Compile(source, "PS_Surface", "ps_4_0"));
        _draw = _device.CreatePixelShader(ShaderCompiler.Compile(source, "PS_Draw", "ps_4_0"));

        _stepConstants = CreateConstants(Unsafe.SizeOf<StepConstants>());
        _injectConstants = CreateConstants(Unsafe.SizeOf<InjectConstants>());
        _drawConstants = CreateConstants(Unsafe.SizeOf<DrawConstants>());

        _surfaceSampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        _wallpaperSampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Mirror));
    }

    private ID3D11Buffer CreateConstants(int size) =>
        _device!.CreateBuffer(new BufferDescription((uint)((size + 15) & ~15), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

    private static string ReadShaderSource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Water.hlsl")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void CreateSurfaceResources(Surface surface)
    {
        var device = _device!;
        var width = surface.Monitor.Bounds.Width;
        var height = surface.Monitor.Bounds.Height;
        var scale = surface.Monitor.Scale;

        var cell = (_settings.Detail switch { WaterDetail.Low => 4.4, WaterDetail.High => 2.3, _ => 3.2 }) * scale;
        cell = Math.Max(cell, Math.Max(width, height) / 1280d);
        surface.CellSize = (float)cell;
        surface.SimWidth = Math.Max(16, (int)Math.Ceiling(width / cell));
        surface.SimHeight = Math.Max(16, (int)Math.Ceiling(height / cell));

        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Scaling = Scaling.None,
        };
        surface.SwapChain = _factory!.CreateSwapChainForHwnd(device, surface.Window, description);
        _factory.MakeWindowAssociation(surface.Window, WindowAssociationFlags.IgnoreAll);
        using (var back = surface.SwapChain.GetBuffer<ID3D11Texture2D>(0))
        {
            surface.BackBuffer = device.CreateRenderTargetView(back);
        }

        var stateDescription = new Texture2DDescription(Format.R32G32_Float, (uint)surface.SimWidth, (uint)surface.SimHeight, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource);
        for (var i = 0; i < 2; i++)
        {
            surface.State[i] = device.CreateTexture2D(stateDescription);
            surface.StateTarget[i] = device.CreateRenderTargetView(surface.State[i]);
            surface.StateView[i] = device.CreateShaderResourceView(surface.State[i]);
            _context!.ClearRenderTargetView(surface.StateTarget[i], new Color4(0, 0, 0, 0));
        }

        var surfaceDescription = new Texture2DDescription(Format.R16G16B16A16_Float, (uint)surface.SimWidth, (uint)surface.SimHeight, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource);
        surface.Slopes = device.CreateTexture2D(surfaceDescription);
        surface.SlopesTarget = device.CreateRenderTargetView(surface.Slopes);
        surface.SlopesView = device.CreateShaderResourceView(surface.Slopes);
        _context!.ClearRenderTargetView(surface.SlopesTarget, new Color4(0, 0, 0, 0));

        surface.Staging = device.CreateTexture2D(new Texture2DDescription(Format.R32G32_Float, (uint)surface.SimWidth, (uint)surface.SimHeight, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
    }

    private void LoadWallpapers(bool showWindows = false)
    {
        var settings = _settings;
        var decoded = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew();
        var every = new Native.RECT
        {
            Left = _surfaces.Min(s => s.Monitor.Bounds.Left),
            Top = _surfaces.Min(s => s.Monitor.Bounds.Top),
            Right = _surfaces.Max(s => s.Monitor.Bounds.Right),
            Bottom = _surfaces.Max(s => s.Monitor.Bounds.Bottom),
        };

        try
        {
            foreach (var surface in _surfaces)
            {
                string? path;
                WallpaperFit fit;
                var span = false;
                Color background;
                if (settings.WallpaperPath is { } chosen && File.Exists(chosen))
                {
                    path = chosen;
                    fit = settings.Fit;
                    background = Color.Black;
                }
                else
                {
                    var windows = WallpaperSource.ReadWindows(surface.Monitor.Bounds);
                    path = windows.Path;
                    fit = windows.Fit;
                    span = windows.Span;
                    background = windows.Background;
                }

                using var picture = WallpaperSource.Compose(path, fit, span, background, surface.Monitor.Bounds, every, decoded);
                UploadWallpaper(surface, picture);
                surface.Dirty = true;
            }
        }
        finally
        {
            foreach (var bitmap in decoded.Values)
            {
                bitmap.Dispose();
            }
        }

        Log.Info($"Wallpaper ready in {watch.ElapsedMilliseconds} ms ({settings.WallpaperPath ?? "the Windows wallpaper"}).");
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);

        if (showWindows)
        {
            foreach (var surface in _surfaces)
            {
                RenderCalm(surface);
            }

            _context!.Flush();
            _host!.Show(_surfaces.Select(s => s.Window));
            foreach (var surface in _surfaces)
            {
                RenderCalm(surface);
            }
        }
        else
        {
            foreach (var surface in _surfaces)
            {
                DrawToScreen(surface, surface == _surfaces[0]);
                surface.Dirty = false;
            }
        }
    }

    private void UploadWallpaper(Surface surface, Bitmap picture)
    {
        surface.WallpaperView?.Dispose();
        surface.Wallpaper?.Dispose();

        var data = picture.LockBits(new Rectangle(0, 0, picture.Width, picture.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var description = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)picture.Width, (uint)picture.Height, 1, 1,
                BindFlags.ShaderResource, ResourceUsage.Immutable);
            surface.Wallpaper = _device!.CreateTexture2D(description, new[] { new SubresourceData(data.Scan0, (uint)data.Stride) });
            surface.WallpaperView = _device.CreateShaderResourceView(surface.Wallpaper);
        }
        finally
        {
            picture.UnlockBits(data);
        }
    }

    private void ReleaseEverything()
    {
        foreach (var surface in _surfaces)
        {
            surface.Dispose();
        }

        _surfaces.Clear();
        _droplets.Clear();
        _context?.ClearState();
        _context?.Flush();
        _surfaceSampler?.Dispose();
        _wallpaperSampler?.Dispose();
        _stepConstants?.Dispose();
        _injectConstants?.Dispose();
        _drawConstants?.Dispose();
        _vertex?.Dispose();
        _inject?.Dispose();
        _step?.Dispose();
        _surfacePass?.Dispose();
        _draw?.Dispose();
        _factory?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _surfaceSampler = _wallpaperSampler = null;
        _stepConstants = _injectConstants = _drawConstants = null;
        _vertex = null;
        _inject = _step = _surfacePass = _draw = null;
        _factory = null;
        _context = null;
        _device = null;
        _host?.Detach();
    }

    // ---- Pausing ----

    private void CheckRules()
    {
        if (_host is { IsAttached: false } && _surfaces.Count > 0)
        {
            Log.Info("The desktop went away (Explorer restarted?). Reattaching.");
            _rebuildRequested = true;
            _hookRequested = true;
        }

        var settings = _settings;
        string reason;
        if (settings.Paused)
        {
            reason = "Paused";
        }
        else if (settings.PauseOnBattery && System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline)
        {
            reason = "Paused on battery";
        }
        else if (settings.PauseOnFullScreen && IsFullScreenAppRunning())
        {
            reason = "Paused for a full-screen app";
        }
        else
        {
            reason = "";
        }

        if (reason != _pauseReason)
        {
            _pauseReason = reason;
            Log.Info(reason.Length == 0 ? "Water running." : reason + ".");
            if (reason.Length > 0 && reason != "Paused")
            {
                // Nobody can see it, so don't spend a single frame settling it.
                foreach (var surface in _surfaces)
                {
                    Flatten(surface);
                }
            }

            PauseChanged?.Invoke(reason);
        }
    }

    private bool IsFullScreenAppRunning()
    {
        if (Native.SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4)
        {
            return true;
        }

        var foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero || Native.ClassOf(foreground) is "Progman" or "WorkerW" or "Shell_TrayWnd")
        {
            return false;
        }

        if (!Native.GetWindowRect(foreground, out var rect))
        {
            return false;
        }

        foreach (var surface in _surfaces)
        {
            var monitor = surface.Monitor.Bounds;
            if (rect.Left <= monitor.Left && rect.Top <= monitor.Top && rect.Right >= monitor.Right && rect.Bottom >= monitor.Bottom)
            {
                // A maximised window stops short of the taskbar; only borderless full
                // screen covers the monitor exactly.
                var style = (uint)(long)Native.GetWindowLongPtr(foreground, Native.GWL_STYLE);
                if ((style & 0x00C00000) == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ---- One frame ----

    /// <summary>Returns false when there's nothing to do and the thread may sleep.</summary>
    private bool Frame(double now)
    {
        if (_surfaces.Count == 0 || _device is null)
        {
            return false;
        }

        var settings = _settings;
        var pausedForAll = _pauseReason.Length > 0;
        ReadInput(settings, now, pausedForAll);

        var anyActive = false;
        foreach (var surface in _surfaces)
        {
            anyActive |= surface.Active || surface.Dirty || surface.PendingStamps.Count > 0;
        }

        if (!anyActive && _droplets.Count == 0)
        {
            return false;
        }

        if (_lastFrameTime == 0)
        {
            _lastFrameTime = now - (1 / 60d);
        }

        var elapsed = Math.Min(now - _lastFrameTime, 0.1);
        _lastFrameTime = now;
        _stepDebt += elapsed * StepsPerSecond;
        var steps = Math.Min((int)_stepDebt, MaxStepsPerFrame);
        _stepDebt = Math.Min(_stepDebt - steps, 3);

        var first = true;
        foreach (var surface in _surfaces)
        {
            if (surface.PendingStamps.Count > 0)
            {
                if (!surface.Active)
                {
                    var stamp = surface.PendingStamps[0];
                    Log.Info($"Water woke: {(stamp.From.Equals(stamp.To) ? "splash" : "cursor")} at {stamp.To.X},{stamp.To.Y}.");
                }

                surface.Active = true;
                surface.CalmChecks = 0;
                Inject(surface);
            }

            if (surface.Active)
            {
                Step(surface, steps, settings);
                WriteSlopes(surface);
                DrawToScreen(surface, first);
                first = false;
                CheckForCalm(surface, now);
            }
            else if (surface.Dirty)
            {
                DrawToScreen(surface, first);
                first = false;
                surface.Dirty = false;
            }
        }

        _context!.ClearState();
        Report(now);

        // The screen's own refresh paces the water. If nothing held us back (a screen
        // that isn't presenting, say), don't spin: the frame took almost no time at all.
        if (_clock.Elapsed.TotalSeconds - now < 0.004)
        {
            Thread.Sleep(8);
        }

        return true;
    }

    private void Report(double now)
    {
        _statsFrames++;
        if (now - _statsSince >= 5)
        {
            if (_statsSince > 0)
            {
                Log.Info($"Water: {_statsFrames / (now - _statsSince):0.0} frames a second.");
            }

            _statsSince = now;
            _statsFrames = 0;
        }
    }

    private void ReadInput(RippleSettings settings, double now, bool paused)
    {
        var mouse = _mouse!;
        mouse.MarkAwake();

        var strength = (float)(0.3 + (settings.Strength * 1.7));
        var sensitivity = (float)(0.15 + (settings.Sensitivity * 1.7));

        while (mouse.TryTakePress(out var press))
        {
            if (!paused && settings.ClickSplash && IsDesktopAt(press) && SurfaceAt(press) is { } surface)
            {
                Splash(surface, press, strength, now);
            }
        }

        var cursor = mouse.Position;
        if (_haveCursor && (cursor.X != _lastCursor.X || cursor.Y != _lastCursor.Y))
        {
            var from = _lastCursor;
            if (!paused && SurfaceAt(cursor) is { } surface && SurfaceAt(from) == surface && IsDesktopAt(cursor))
            {
                var scale = (float)surface.Monitor.Scale;
                var dx = (float)(cursor.X - from.X);
                var dy = (float)(cursor.Y - from.Y);
                var distance = MathF.Sqrt((dx * dx) + (dy * dy));
                var pace = Math.Clamp(distance / (46f * scale), 0.06f, 1f);
                surface.AddStamp(from, cursor, 12f * scale, 0.8f * pace * sensitivity * strength, 0);
            }
        }

        _lastCursor = cursor;
        _haveCursor = cursor.X != 0 || cursor.Y != 0 || _haveCursor;

        for (var i = _droplets.Count - 1; i >= 0; i--)
        {
            var droplet = _droplets[i];
            if (now >= droplet.When)
            {
                _droplets.RemoveAt(i);
                if (!paused)
                {
                    droplet.Surface.AddStamp(droplet.At, droplet.At, droplet.Radius, droplet.Amount, 0);
                }
            }
        }

        if (_demo && now < _demoEnds && now >= _nextDemoSplash && !paused && _surfaces.Count > 0)
        {
            _nextDemoSplash = now + 1.6;
            var surface = _surfaces[0];
            var bounds = surface.Monitor.Bounds;
            var at = new Native.POINT
            {
                X = bounds.Left + _random.Next(bounds.Width / 6, bounds.Width * 5 / 6),
                Y = bounds.Top + _random.Next(bounds.Height / 6, bounds.Height * 5 / 6),
            };
            Splash(surface, at, strength, now);
        }
    }

    /// <summary>A real splash: a heavy dent, then a few droplets thrown out that land a moment later.</summary>
    private void Splash(Surface surface, Native.POINT at, float strength, double now)
    {
        var scale = (float)surface.Monitor.Scale;
        surface.AddStamp(at, at, 25f * scale, 2.9f * strength, 0);

        // A tighter, faster kick at the very centre seeds the train of fine wavelets.
        surface.AddStamp(at, at, 10f * scale, 0, -0.34f * strength);

        var count = _random.Next(4, 8);
        for (var i = 0; i < count; i++)
        {
            var angle = _random.NextDouble() * Math.PI * 2;
            var reach = (40 + (_random.NextDouble() * 110)) * scale;
            _droplets.Add(new Droplet(
                surface,
                new Native.POINT { X = at.X + (int)(Math.Cos(angle) * reach), Y = at.Y + (int)(Math.Sin(angle) * reach) },
                now + 0.12 + (_random.NextDouble() * 0.35),
                (float)((7 + (_random.NextDouble() * 7)) * scale),
                (float)((0.45 + (_random.NextDouble() * 0.5)) * strength)));
        }
    }

    private Surface? SurfaceAt(Native.POINT p)
    {
        foreach (var surface in _surfaces)
        {
            var bounds = surface.Monitor.Bounds;
            if (p.X >= bounds.Left && p.X < bounds.Right && p.Y >= bounds.Top && p.Y < bounds.Bottom)
            {
                return surface;
            }
        }

        return null;
    }

    /// <summary>True when the pointer is over the desktop, not an app window or the taskbar.</summary>
    private static bool IsDesktopAt(Native.POINT p)
    {
        var window = Native.WindowFromPoint(p);
        if (window == IntPtr.Zero)
        {
            return false;
        }

        var root = Native.GetAncestor(window, Native.GA_ROOT);
        return Native.ClassOf(root) is "Progman" or "WorkerW";
    }

    private void FullScreenPass(ID3D11PixelShader shader, ID3D11RenderTargetView target, int width, int height)
    {
        var context = _context!;
        context.OMSetRenderTargets(target);
        context.RSSetViewport(new Viewport(width, height));
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertex);
        context.PSSetShader(shader);
        context.Draw(3, 0);
    }

    private void Inject(Surface surface)
    {
        var context = _context!;
        var count = Math.Min(surface.PendingStamps.Count, MaxStamps);
        var constants = new InjectConstants { StampCount = count };
        var bounds = surface.Monitor.Bounds;
        unsafe
        {
            for (var i = 0; i < count; i++)
            {
                var stamp = surface.PendingStamps[i];
                constants.Stamps[(i * 8) + 0] = (stamp.From.X - bounds.Left) / surface.CellSize;
                constants.Stamps[(i * 8) + 1] = (stamp.From.Y - bounds.Top) / surface.CellSize;
                constants.Stamps[(i * 8) + 2] = (stamp.To.X - bounds.Left) / surface.CellSize;
                constants.Stamps[(i * 8) + 3] = (stamp.To.Y - bounds.Top) / surface.CellSize;
                constants.Stamps[(i * 8) + 4] = Math.Max(1.2f, stamp.Radius / surface.CellSize);
                constants.Stamps[(i * 8) + 5] = -stamp.Height;
                constants.Stamps[(i * 8) + 6] = stamp.Speed;
                constants.Stamps[(i * 8) + 7] = 0;
            }
        }

        surface.PendingStamps.RemoveRange(0, count);
        Write(_injectConstants!, constants);

        var next = 1 - surface.Current;
        context.OMSetRenderTargets((ID3D11RenderTargetView?)null!);
        context.PSSetConstantBuffer(0, _injectConstants);
        context.PSSetShaderResource(0, surface.StateView[surface.Current]);
        FullScreenPass(_inject!, surface.StateTarget[next], surface.SimWidth, surface.SimHeight);
        context.PSSetShaderResource(0, null!);
        surface.Current = next;
    }

    private void Step(Surface surface, int steps, RippleSettings settings)
    {
        if (steps <= 0)
        {
            return;
        }

        var context = _context!;

        // Keep real-world wave speed the same at any display scale and detail level.
        var waveSpeed = Math.Min(0.70f, (float)(360 * surface.Monitor.Scale) / (surface.CellSize * StepsPerSecond));
        var keep = (float)(0.9997 + ((0.9900 - 0.9997) * settings.Damping));
        var constants = new StepConstants
        {
            WaveSpeedSquared = waveSpeed * waveSpeed,
            VelocityKeep = keep,
            Viscosity = 0.016f,
            HeightKeep = 0.9997f,
            Dispersion = 0.42f,
            MaxX = surface.SimWidth - 1,
            MaxY = surface.SimHeight - 1,
        };
        Write(_stepConstants!, constants);
        context.PSSetConstantBuffer(0, _stepConstants);

        for (var i = 0; i < steps; i++)
        {
            var next = 1 - surface.Current;
            context.OMSetRenderTargets((ID3D11RenderTargetView?)null!);
            context.PSSetShaderResource(0, surface.StateView[surface.Current]);
            FullScreenPass(_step!, surface.StateTarget[next], surface.SimWidth, surface.SimHeight);
            context.PSSetShaderResource(0, null!);
            surface.Current = next;
        }
    }

    private void WriteSlopes(Surface surface)
    {
        var context = _context!;

        // PS_Surface reads the same constants as PS_Step: the grid size is what it needs.
        context.PSSetConstantBuffer(0, _stepConstants);
        context.OMSetRenderTargets((ID3D11RenderTargetView?)null!);
        context.PSSetShaderResource(0, surface.StateView[surface.Current]);
        FullScreenPass(_surfacePass!, surface.SlopesTarget!, surface.SimWidth, surface.SimHeight);
        context.PSSetShaderResource(0, null!);
    }

    private void DrawToScreen(Surface surface, bool waitForScreen)
    {
        var context = _context!;
        var scale = (float)surface.Monitor.Scale;
        var bounds = surface.Monitor.Bounds;
        var constants = new DrawConstants
        {
            ScreenWidth = bounds.Width,
            ScreenHeight = bounds.Height,
            SimWidth = surface.SimWidth,
            SimHeight = surface.SimHeight,
            Depth = surface.DepthPixels(scale),
            ColourSplit = 0.075f,
            Light = 1f,
            CausticStrength = 1.15f,
        };
        Write(_drawConstants!, constants);

        // Set the target first: the slopes texture must not be a target and a resource at once.
        context.OMSetRenderTargets(surface.BackBuffer!);
        context.PSSetConstantBuffer(0, _drawConstants);
        context.PSSetShaderResource(0, surface.SlopesView);
        context.PSSetShaderResource(1, surface.WallpaperView);
        context.PSSetSampler(0, _surfaceSampler);
        context.PSSetSampler(1, _wallpaperSampler);
        FullScreenPass(_draw!, surface.BackBuffer!, bounds.Width, bounds.Height);
        context.PSSetShaderResource(0, null!);
        context.PSSetShaderResource(1, null!);

        var result = surface.SwapChain!.Present(waitForScreen ? surface.SyncInterval : 0u, PresentFlags.None);
        if (result.Failure)
        {
            throw new COMException($"Present failed with {result}", result.Code);
        }
    }

    private void RenderCalm(Surface surface)
    {
        DrawToScreen(surface, false);
        surface.Dirty = false;
    }

    /// <summary>
    /// A few times a second, look at the actual water. Once nothing would move a pixel
    /// any more, snap it to perfectly flat and let the thread sleep.
    /// </summary>
    private void CheckForCalm(Surface surface, double now)
    {
        var context = _context!;
        if (surface.StagingPending)
        {
            var result = context.Map(surface.Staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out var mapped);
            if (result.Failure)
            {
                return; // The card is still busy with it. Look again next frame.
            }

            float largest;
            try
            {
                largest = LargestMovement(mapped.DataPointer, (int)mapped.RowPitch, surface.SimWidth, surface.SimHeight);
            }
            finally
            {
                context.Unmap(surface.Staging!, 0);
            }

            surface.StagingPending = false;
            var pixels = largest * surface.DepthPixels((float)surface.Monitor.Scale);
            if (pixels < CalmSlopePixels && surface.PendingStamps.Count == 0)
            {
                Flatten(surface);
                Log.Info("Water calm; going to sleep.");
            }

            return;
        }

        if (now >= surface.NextCalmCheck)
        {
            surface.NextCalmCheck = now + 0.4;
            context.CopyResource(surface.Staging!, surface.State[surface.Current]!);
            surface.StagingPending = true;
        }
    }

    private static unsafe float LargestMovement(IntPtr data, int pitch, int width, int height)
    {
        var largest = 0f;
        for (var y = 0; y < height; y++)
        {
            var row = (float*)((byte*)data + (y * pitch));
            var previous = row[0];
            for (var x = 1; x < width; x++)
            {
                var value = row[x * 2];
                var slope = MathF.Abs(value - previous) + MathF.Abs(row[(x * 2) + 1]);
                previous = value;
                if (slope > largest)
                {
                    largest = slope;
                }
            }
        }

        return largest;
    }

    private void Flatten(Surface surface)
    {
        var context = _context!;
        surface.Active = false;
        surface.StagingPending = false;
        context.ClearRenderTargetView(surface.StateTarget[0]!, new Color4(0, 0, 0, 0));
        context.ClearRenderTargetView(surface.StateTarget[1]!, new Color4(0, 0, 0, 0));
        context.ClearRenderTargetView(surface.SlopesTarget!, new Color4(0, 0, 0, 0));
        surface.Dirty = true;
    }

    private unsafe void Write<T>(ID3D11Buffer buffer, T value) where T : unmanaged
    {
        var mapped = _context!.Map(buffer, MapMode.WriteDiscard);
        Unsafe.Write(mapped.DataPointer.ToPointer(), value);
        _context.Unmap(buffer, 0);
    }

    // ---- Small types ----

    private readonly record struct Stamp(Native.POINT From, Native.POINT To, float Radius, float Height, float Speed);

    private readonly record struct Droplet(Surface Surface, Native.POINT At, double When, float Radius, float Amount);

    [StructLayout(LayoutKind.Sequential)]
    private struct StepConstants
    {
        public float WaveSpeedSquared, VelocityKeep, Viscosity, HeightKeep, Dispersion;
        public int MaxX, MaxY;
        public float Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct InjectConstants
    {
        public int StampCount;
        public float PadA, PadB, PadC;
        public fixed float Stamps[MaxStamps * 8];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrawConstants
    {
        public float ScreenWidth, ScreenHeight, SimWidth, SimHeight;
        public float Depth, ColourSplit, Light, CausticStrength;
    }

    private sealed class Surface : IDisposable
    {
        public Surface(MonitorInfo monitor, IntPtr window)
        {
            Monitor = monitor;
            Window = window;
        }

        public MonitorInfo Monitor { get; }
        public IntPtr Window { get; }
        public IDXGISwapChain1? SwapChain;
        public ID3D11RenderTargetView? BackBuffer;
        public readonly ID3D11Texture2D?[] State = new ID3D11Texture2D?[2];
        public readonly ID3D11RenderTargetView?[] StateTarget = new ID3D11RenderTargetView?[2];
        public readonly ID3D11ShaderResourceView?[] StateView = new ID3D11ShaderResourceView?[2];
        public ID3D11Texture2D? Slopes;
        public ID3D11RenderTargetView? SlopesTarget;
        public ID3D11ShaderResourceView? SlopesView;
        public ID3D11Texture2D? Staging;
        public ID3D11Texture2D? Wallpaper;
        public ID3D11ShaderResourceView? WallpaperView;
        public readonly List<Stamp> PendingStamps = new(MaxStamps);
        public int Current;
        public int SimWidth, SimHeight;
        public float CellSize;
        public bool Active, Dirty, StagingPending;
        public int CalmChecks;

        /// <summary>Screen refreshes per frame: about 60 frames a second is plenty for water.</summary>
        public uint SyncInterval => (uint)Math.Max(1, (int)Math.Round(Monitor.RefreshRate / 60d));
        public double NextCalmCheck;

        public float DepthPixels(float scale) => 240f * scale;

        public void AddStamp(Native.POINT from, Native.POINT to, float radius, float height, float speed)
        {
            if (PendingStamps.Count < MaxStamps * 3)
            {
                PendingStamps.Add(new Stamp(from, to, radius, height, speed));
            }
        }

        public void Dispose()
        {
            BackBuffer?.Dispose();
            SwapChain?.Dispose();
            for (var i = 0; i < 2; i++)
            {
                StateView[i]?.Dispose();
                StateTarget[i]?.Dispose();
                State[i]?.Dispose();
            }

            SlopesView?.Dispose();
            SlopesTarget?.Dispose();
            Slopes?.Dispose();
            Staging?.Dispose();
            WallpaperView?.Dispose();
            Wallpaper?.Dispose();
        }
    }
}

/// <summary>Compiles the shader through the compiler that ships with Windows.</summary>
internal static class ShaderCompiler
{
    public static byte[] Compile(string source, string entry, string profile)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var result = D3DCompile(bytes, (nuint)bytes.Length, "Water.hlsl", IntPtr.Zero, IntPtr.Zero, entry, profile, 0x0800 /* optimise fully */, 0, out var code, out var errors);
        try
        {
            if (result < 0)
            {
                var message = errors != IntPtr.Zero ? Marshal.PtrToStringAnsi(BlobPointer(errors)) : "no details";
                throw new InvalidOperationException($"Shader {entry} failed: {message}");
            }

            var output = new byte[(int)BlobSize(code)];
            Marshal.Copy(BlobPointer(code), output, 0, output.Length);
            return output;
        }
        finally
        {
            if (code != IntPtr.Zero) Marshal.Release(code);
            if (errors != IntPtr.Zero) Marshal.Release(errors);
        }
    }

    private static unsafe IntPtr BlobPointer(IntPtr blob) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)(*(*(void***)blob + 3)))(blob);

    private static unsafe nuint BlobSize(IntPtr blob) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, nuint>)(*(*(void***)blob + 4)))(blob);

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] source, nuint size, string name, IntPtr defines, IntPtr include, string entry, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);
}
