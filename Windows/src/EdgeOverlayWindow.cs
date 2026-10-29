using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DrawingPath = System.Windows.Shapes.Path;

namespace LanDrop.Windows;

public sealed class EdgeOverlayWindow : Window
{
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer _foregroundTimer;
    private readonly DispatcherTimer _topmostTimer;
    private EdgeOverlaySettings _settings = new();
    private readonly List<(NativeRect Bounds, double Dpi)> _monitors = [];
    private NativeRect _desktop;
    private NativeRect? _foreground;
    private nint _foregroundHandle;
    private HwndSource? _source;
    private nint _handle;
    private bool _closed;
    private bool _displayUpdateQueued;

    public EdgeOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = true;
        Content = _canvas;
        _foregroundTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _foregroundTimer.Tick += ForegroundTimer_Tick;
        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _topmostTimer.Tick += TopmostTimer_Tick;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_handle);
            _source?.AddHook(WindowMessageHook);
            var style = GetWindowLongPtr(_handle, -20).ToInt64();
            SetWindowLongPtr(_handle, -20, new nint(style | 0x20 | 0x80 | 0x08000000));
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _foregroundTimer.Stop();
            _foregroundTimer.Tick -= ForegroundTimer_Tick;
            _topmostTimer.Stop();
            _topmostTimer.Tick -= TopmostTimer_Tick;
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            _source?.RemoveHook(WindowMessageHook);
        };
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
    }

    public void ApplySettings(EdgeOverlaySettings settings)
    {
        if (_closed) return;
        _settings = settings;
        if (!settings.Enabled)
        {
            _foregroundTimer.Stop();
            _topmostTimer.Stop();
            _canvas.Children.Clear();
            Hide();
            return;
        }

        if (!IsVisible) Show();
        UpdateDisplayLayout();
        _topmostTimer.Start();
        if (settings.DisplayMode == EdgeOverlayDisplayMode.Window)
        {
            UpdateForeground();
            _foregroundTimer.Start();
        }
        else
        {
            _foregroundTimer.Stop();
        }
        RenderOverlay();
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e) => QueueDisplayUpdate();

    private void QueueDisplayUpdate()
    {
        if (_closed || _displayUpdateQueued) return;
        _displayUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _displayUpdateQueued = false;
            if (_closed || !_settings.Enabled) return;
            UpdateDisplayLayout();
            RenderOverlay();
        }));
    }

    private void UpdateDisplayLayout()
    {
        _monitors.Clear();
        EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref NativeRect bounds, nint state) =>
        {
            var dpi = GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
            _monitors.Add((bounds, dpi));
            return true;
        }, 0);
        if (_monitors.Count == 0) return;
        _desktop = new NativeRect
        {
            Left = _monitors.Min(m => m.Bounds.Left),
            Top = _monitors.Min(m => m.Bounds.Top),
            Right = _monitors.Max(m => m.Bounds.Right),
            Bottom = _monitors.Max(m => m.Bounds.Bottom)
        };
        SetWindowPos(_handle, new nint(-1), _desktop.Left, _desktop.Top,
            _desktop.Right - _desktop.Left, _desktop.Bottom - _desktop.Top, 0x0010);
    }

    private void ForegroundTimer_Tick(object? sender, EventArgs e)
    {
        if (UpdateForeground()) RenderOverlay();
    }

    private void TopmostTimer_Tick(object? sender, EventArgs e)
    {
        SetWindowPos(_handle, new nint(-1), 0, 0, 0, 0, 0x0213);
    }

    private bool UpdateForeground()
    {
        var window = GetForegroundWindow();
        if (window == 0 || IsIconic(window)) return false;
        GetWindowThreadProcessId(window, out var processId);
        // Keep the last external target while the user adjusts LanDrop settings.
        if (processId == Environment.ProcessId)
        {
            if (_foreground is not null && (!IsWindow(_foregroundHandle) || IsIconic(_foregroundHandle)))
            {
                _foreground = null;
                _foregroundHandle = 0;
                return true;
            }
            return false;
        }
        if (!GetWindowRect(window, out var bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;
        _foregroundHandle = window;
        if (_foreground is NativeRect old && old.Left == bounds.Left && old.Top == bounds.Top &&
            old.Right == bounds.Right && old.Bottom == bounds.Bottom) return false;
        _foreground = bounds;
        return true;
    }

    private void RenderOverlay()
    {
        _canvas.Children.Clear();
        if (!_settings.Enabled || _handle == 0) return;
        var dpi = GetDpiForWindow(_handle) / 96.0;
        if (dpi <= 0) dpi = 1;
        foreach (var monitor in _monitors)
        {
            var bounds = new Rect((monitor.Bounds.Left - _desktop.Left) / dpi, (monitor.Bounds.Top - _desktop.Top) / dpi,
                (monitor.Bounds.Right - monitor.Bounds.Left) / dpi, (monitor.Bounds.Bottom - monitor.Bounds.Top) / dpi);
            var useAspect = _settings.DisplayMode != EdgeOverlayDisplayMode.Window;
            if (!useAspect)
            {
                if (_foreground is not NativeRect target) continue;
                var centerX = ((double)target.Left + target.Right) / 2;
                var centerY = ((double)target.Top + target.Bottom) / 2;
                if (centerX < monitor.Bounds.Left || centerX >= monitor.Bounds.Right ||
                    centerY < monitor.Bounds.Top || centerY >= monitor.Bounds.Bottom) continue;
                bounds = new Rect((target.Left - _desktop.Left) / dpi, (target.Top - _desktop.Top) / dpi,
                    (target.Right - target.Left) / dpi, (target.Bottom - target.Top) / dpi);
            }
            foreach (var shape in EdgeOverlayGeometry.CreateShapes(_settings, bounds, monitor.Dpi / dpi, useAspect))
                _canvas.Children.Add(shape);
        }
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0084) { handled = true; return new nint(-1); }
        if (message == 0x0021) { handled = true; return new nint(3); }
        if (message is 0x007E or 0x02E0) QueueDisplayUpdate();
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect bounds, nint state);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint state);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
}

internal static class EdgeOverlayGeometry
{
    internal static Rect GetSafeArea(double width, double height, EdgeOverlayAspectRatio aspect)
    {
        var ratio = aspect switch
        {
            EdgeOverlayAspectRatio.UltraWide21x9 => 21.0 / 9,
            EdgeOverlayAspectRatio.Standard4x3 => 4.0 / 3,
            EdgeOverlayAspectRatio.Standard5x4 => 5.0 / 4,
            _ => 0
        };
        if (ratio == 0) return new Rect(0, 0, width, height);
        var safeWidth = Math.Min(width, height * ratio);
        var safeHeight = Math.Min(height, width / ratio);
        return new Rect((width - safeWidth) / 2, (height - safeHeight) / 2, safeWidth, safeHeight);
    }

    internal static IReadOnlyList<Shape> CreateShapes(EdgeOverlaySettings settings, Rect bounds, double scale = 1, bool applyAspectRatio = true)
    {
        var shapes = new List<Shape>();
        if (bounds.Width <= 0 || bounds.Height <= 0) return shapes;
        if (settings.Shape is not (EdgeOverlayShape.Rectangle or EdgeOverlayShape.Semicircle or EdgeOverlayShape.Triangle)) return shapes;
        var safe = applyAspectRatio ? GetSafeArea(bounds.Width, bounds.Height, settings.AspectRatio) : new Rect(0, 0, bounds.Width, bounds.Height);
        safe.Offset(bounds.X, bounds.Y);
        var areas = settings.Split switch
        {
            EdgeOverlaySplit.SideBySide => new[] { new Rect(safe.X, safe.Y, safe.Width / 2, safe.Height), new Rect(safe.X + safe.Width / 2, safe.Y, safe.Width / 2, safe.Height) },
            EdgeOverlaySplit.TopBottom => new[] { new Rect(safe.X, safe.Y, safe.Width, safe.Height / 2), new Rect(safe.X, safe.Y + safe.Height / 2, safe.Width, safe.Height / 2) },
            _ => new[] { safe }
        };
        double[] widths = [4, 8, 15, 38, 55, 75, 100];
        double[] lengths = [0, 2, 4, 7, 10, 14, 20];
        var bar = (settings.Size is >= 0 and <= 6 ? widths[settings.Size] : 38) * scale;
        var length = (settings.Length is >= 0 and <= 6 ? lengths[settings.Length] : 0) * scale;
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(settings.ColorHex) ? "#00FF00" : settings.ColorHex); }
        catch (FormatException) { color = Colors.Lime; }
        catch (NotSupportedException) { color = Colors.Lime; }
        foreach (var area in areas)
        {
            bool[] visible = [settings.TopVisible, settings.BottomVisible, settings.LeftVisible, settings.RightVisible];
            int[] opacity = [settings.TopOpacity, settings.BottomOpacity, settings.LeftOpacity, settings.RightOpacity];
            for (var side = 0; side < 4; side++)
            {
                if (!visible[side]) continue;
                var alpha = Math.Clamp(settings.UsePerEdgeOpacity ? opacity[side] : settings.Opacity, 0, 100) * 255 / 100;
                var brush = new SolidColorBrush(Color.FromArgb((byte)alpha, color.R, color.G, color.B));
                brush.Freeze();
                var center = side switch
                {
                    0 => new Point(area.X + area.Width / 2, area.Y),
                    1 => new Point(area.X + area.Width / 2, area.Bottom),
                    2 => new Point(area.X, area.Y + area.Height / 2),
                    _ => new Point(area.Right, area.Y + area.Height / 2)
                };
                var along = side < 2 ? new Vector(1, 0) : new Vector(0, 1);
                var inward = side switch { 0 => new Vector(0, 1), 1 => new Vector(0, -1), 2 => new Vector(1, 0), _ => new Vector(-1, 0) };
                Shape shape;
                if (settings.Shape == EdgeOverlayShape.Rectangle)
                {
                    var thickness = Math.Max(bar * 1.5, 2);
                    var span = Math.Max(bar * 3 + length * 12, thickness * 2);
                    shape = new Rectangle { Width = side < 2 ? span : thickness, Height = side < 2 ? thickness : span, Fill = brush };
                    Canvas.SetLeft(shape, center.X - (side < 2 ? span / 2 : side == 3 ? thickness : 0));
                    Canvas.SetTop(shape, center.Y - (side >= 2 ? span / 2 : side == 1 ? thickness : 0));
                }
                else
                {
                    var figure = new PathFigure { IsClosed = true, IsFilled = true };
                    if (settings.Shape == EdgeOverlayShape.Semicircle)
                    {
                        var depth = Math.Max(bar * 1.5, 8);
                        var radius = Math.Max(bar * 1.5 + length * 7.5, depth);
                        figure.StartPoint = center - along * radius;
                        figure.Segments.Add(new ArcSegment(center + along * radius,
                            side < 2 ? new Size(radius, depth) : new Size(depth, radius), 0, false,
                            side is 0 or 3 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise, true));
                    }
                    else
                    {
                        var depth = Math.Max(bar * 4, 60);
                        var span = Math.Max(bar * 2.5 + length * 8, 40);
                        figure.StartPoint = center - along * (span / 2);
                        figure.Segments.Add(new LineSegment(center + along * (span / 2), true));
                        figure.Segments.Add(new LineSegment(center + inward * depth, true));
                    }
                    shape = new DrawingPath { Fill = brush, Data = new PathGeometry([figure]) };
                }
                var left = Canvas.GetLeft(shape);
                var top = Canvas.GetTop(shape);
                shape.Clip = new RectangleGeometry(new Rect(area.X - (double.IsNaN(left) ? 0 : left),
                    area.Y - (double.IsNaN(top) ? 0 : top), area.Width, area.Height));
                shapes.Add(shape);
            }
        }
        return shapes;
    }
}
