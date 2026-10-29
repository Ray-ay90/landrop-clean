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

public sealed record CrosshairDisplay(string DeviceName, string Name);

public sealed class CrosshairWindow : Window
{
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer _topmostTimer;
    private readonly List<(NativeRect Bounds, double Dpi, string DeviceName)> _monitors = [];
    private CrosshairSettings _settings = new();
    private NativeRect _desktop;
    private HwndSource? _source;
    private nint _handle;
    private bool _closed;
    private bool _displayUpdateQueued;

    public CrosshairWindow()
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
            SetWindowLongPtr(_handle, -20, new nint(style | 0x08 | 0x20 | 0x80 | 0x08000000));
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _topmostTimer.Stop();
            _topmostTimer.Tick -= TopmostTimer_Tick;
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            _source?.RemoveHook(WindowMessageHook);
        };
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
    }

    public static IReadOnlyList<CrosshairDisplay> GetDisplays()
    {
        var displays = new List<CrosshairDisplay>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref NativeRect bounds, nint state) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), DeviceName = "" };
            if (GetMonitorInfo(monitor, ref info))
            {
                var name = $"显示器 {displays.Count + 1}" + ((info.Flags & 1) != 0 ? "（主屏）" : "");
                displays.Add(new CrosshairDisplay(info.DeviceName, name));
            }
            return true;
        }, 0);
        return displays;
    }

    public void ApplySettings(CrosshairSettings settings)
    {
        if (_closed) return;
        _settings = settings;
        if (!settings.Enabled)
        {
            _topmostTimer.Stop();
            _canvas.Children.Clear();
            Hide();
            return;
        }

        if (!IsVisible) Show();
        UpdateDisplayLayout();
        _topmostTimer.Start();
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
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), DeviceName = "" };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            var dpi = GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
            _monitors.Add((bounds, dpi, info.DeviceName));
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

    private void TopmostTimer_Tick(object? sender, EventArgs e) =>
        SetWindowPos(_handle, new nint(-1), 0, 0, 0, 0, 0x0213);

    private void RenderOverlay()
    {
        _canvas.Children.Clear();
        if (!_settings.Enabled || _handle == 0) return;
        var dpi = GetDpiForWindow(_handle) / 96.0;
        if (dpi <= 0) dpi = 1;
        var selectedMonitorAvailable = _monitors.Any(monitor =>
            string.Equals(_settings.MonitorDeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase));
        foreach (var monitor in _monitors)
        {
            if (selectedMonitorAvailable &&
                !string.Equals(_settings.MonitorDeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase)) continue;
            var bounds = new Rect((monitor.Bounds.Left - _desktop.Left) / dpi, (monitor.Bounds.Top - _desktop.Top) / dpi,
                (monitor.Bounds.Right - monitor.Bounds.Left) / dpi, (monitor.Bounds.Bottom - monitor.Bounds.Top) / dpi);
            foreach (var shape in CrosshairGeometry.CreateShapes(_settings, bounds, monitor.Dpi / dpi))
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
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect bounds, nint state);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}

internal static class CrosshairGeometry
{
    internal static IReadOnlyList<Shape> CreateShapes(CrosshairSettings settings, Rect bounds, double scale = 1)
    {
        var shapes = new List<Shape>();
        if (bounds.Width <= 0 || bounds.Height <= 0) return shapes;
        if (settings.Shape is not (CrosshairShape.Circle or CrosshairShape.Cross or CrosshairShape.Diamond)) return shapes;
        if (scale <= 0) scale = 1;
        var safe = EdgeOverlayGeometry.GetSafeArea(bounds.Width, bounds.Height, settings.AspectRatio);
        safe.Offset(bounds.X, bounds.Y);
        var areas = settings.Split switch
        {
            EdgeOverlaySplit.SideBySide => new[] { new Rect(safe.X, safe.Y, safe.Width / 2, safe.Height), new Rect(safe.X + safe.Width / 2, safe.Y, safe.Width / 2, safe.Height) },
            EdgeOverlaySplit.TopBottom => new[] { new Rect(safe.X, safe.Y, safe.Width, safe.Height / 2), new Rect(safe.X, safe.Y + safe.Height / 2, safe.Width, safe.Height / 2) },
            _ => new[] { safe }
        };
        double[] sizes = [6, 10, 16, 24, 34, 48, 68];
        double[] thicknesses = [1, 2, 3, 4, 5, 7, 10];
        var size = (settings.Size is >= 0 and <= 6 ? sizes[settings.Size] : 24) * scale;
        var thickness = (settings.Thickness is >= 0 and <= 6 ? thicknesses[settings.Thickness] : 4) * scale;
        var half = size / 2;
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(settings.ColorHex) ? "#FF0000" : settings.ColorHex); }
        catch (FormatException) { color = Colors.Red; }
        catch (NotSupportedException) { color = Colors.Red; }
        var brush = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(settings.Opacity, 0, 100) * 255 / 100), color.R, color.G, color.B));
        brush.Freeze();
        foreach (var area in areas)
        {
            var center = new Point(area.X + area.Width / 2 + settings.PositionX, area.Y + area.Height / 2 + settings.PositionY);
            if (settings.Shape == CrosshairShape.Circle)
            {
                var ring = new Ellipse { Width = size, Height = size, Stroke = brush, StrokeThickness = thickness };
                Canvas.SetLeft(ring, center.X - half);
                Canvas.SetTop(ring, center.Y - half);
                shapes.Add(ring);
            }
            else if (settings.Shape == CrosshairShape.Cross)
            {
                shapes.Add(new Line { X1 = center.X - half, Y1 = center.Y, X2 = center.X + half, Y2 = center.Y, Stroke = brush, StrokeThickness = thickness });
                shapes.Add(new Line { X1 = center.X, Y1 = center.Y - half, X2 = center.X, Y2 = center.Y + half, Stroke = brush, StrokeThickness = thickness });
            }
            else
            {
                var figure = new PathFigure { StartPoint = new Point(center.X, center.Y - half), IsClosed = true, IsFilled = false };
                figure.Segments.Add(new LineSegment(new Point(center.X + half, center.Y), true));
                figure.Segments.Add(new LineSegment(new Point(center.X, center.Y + half), true));
                figure.Segments.Add(new LineSegment(new Point(center.X - half, center.Y), true));
                shapes.Add(new DrawingPath { Data = new PathGeometry([figure]), Stroke = brush, StrokeThickness = thickness });
                continue;
            }
            var diameter = Math.Max(settings.Shape == CrosshairShape.Circle ? 1 : 2, thickness);
            var dot = new Ellipse { Width = diameter, Height = diameter, Fill = brush };
            Canvas.SetLeft(dot, center.X - thickness / 2);
            Canvas.SetTop(dot, center.Y - thickness / 2);
            shapes.Add(dot);
        }
        return shapes;
    }
}
