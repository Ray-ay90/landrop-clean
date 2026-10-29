using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LanDrop.Windows;
using Path = System.IO.Path;
using Shape = System.Windows.Shapes.Shape;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    private static readonly List<string> Passed = [];
    private static readonly string EvidenceDirectory = Path.Combine(AppContext.BaseDirectory, "evidence");
    private static readonly Type GeometryType = typeof(EdgeOverlayWindow).Assembly.GetType("LanDrop.Windows.EdgeOverlayGeometry", true)!;
    private static readonly Type WindowType = typeof(MainWindow);

    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(EvidenceDirectory);
        Application? app = null;
        try
        {
            RunGeometry();
            app = new App();
            ((App)app).InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (!args.Contains("--renderer-only")) RunSettingsAndUi();
            RunWindowLifecycle();
            File.WriteAllText(Path.Combine(EvidenceDirectory, "report.json"), JsonSerializer.Serialize(new { result = "passed", checks = Passed }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: {Passed.Count} isolated edge overlay checks; evidence: {EvidenceDirectory}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            File.WriteAllText(Path.Combine(EvidenceDirectory, "report.json"), JsonSerializer.Serialize(new { result = "failed", checks = Passed, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        finally { app?.Shutdown(); }
    }

    private static void RunGeometry()
    {
        var cases = new (EdgeOverlayShape Shape, int Size, int Length, double Along, double Depth)[]
        {
            (EdgeOverlayShape.Rectangle, 0, 0, 12, 6),
            (EdgeOverlayShape.Rectangle, 1, 0, 24, 12),
            (EdgeOverlayShape.Rectangle, 2, 0, 45, 22.5),
            (EdgeOverlayShape.Rectangle, 3, 0, 114, 57),
            (EdgeOverlayShape.Rectangle, 4, 0, 165, 82.5),
            (EdgeOverlayShape.Rectangle, 5, 0, 225, 112.5),
            (EdgeOverlayShape.Rectangle, 6, 0, 300, 150),
            (EdgeOverlayShape.Rectangle, 0, 1, 36, 6),
            (EdgeOverlayShape.Rectangle, 0, 2, 60, 6),
            (EdgeOverlayShape.Rectangle, 0, 3, 96, 6),
            (EdgeOverlayShape.Rectangle, 0, 4, 132, 6),
            (EdgeOverlayShape.Rectangle, 0, 5, 180, 6),
            (EdgeOverlayShape.Rectangle, 6, 6, 540, 150),
            (EdgeOverlayShape.Semicircle, 0, 0, 16, 8),
            (EdgeOverlayShape.Semicircle, 3, 0, 114, 57),
            (EdgeOverlayShape.Semicircle, 0, 2, 72, 8),
            (EdgeOverlayShape.Semicircle, 6, 6, 600, 150),
            (EdgeOverlayShape.Triangle, 0, 0, 40, 60),
            (EdgeOverlayShape.Triangle, 3, 0, 95, 152),
            (EdgeOverlayShape.Triangle, 0, 2, 42, 60),
            (EdgeOverlayShape.Triangle, 6, 6, 410, 400)
        };
        foreach (var item in cases)
        {
            var settings = Settings(item.Shape);
            settings.Size = item.Size;
            settings.Length = item.Length;
            var shapes = Shapes(settings, new Rect(0, 0, 1000, 800));
            var canvas = Layout(shapes, 1000, 800);
            Require(shapes.Count == 4, $"{item.Shape} Size={item.Size} Length={item.Length}: four edges");
            var expected = new[]
            {
                new Rect(500 - item.Along / 2, 0, item.Along, item.Depth),
                new Rect(500 - item.Along / 2, 800 - item.Depth, item.Along, item.Depth),
                new Rect(0, 400 - item.Along / 2, item.Depth, item.Along),
                new Rect(1000 - item.Depth, 400 - item.Along / 2, item.Depth, item.Along)
            };
            RequireBounds(shapes, canvas, expected, $"{item.Shape} original CIL geometry Size={item.Size}, Length={item.Length}");
        }

        var arcSettings = Settings(EdgeOverlayShape.Semicircle);
        arcSettings.Length = 2;
        var arcBitmap = Render(Layout(Shapes(arcSettings, new Rect(0, 0, 1000, 800)), 1000, 800), 1000, 800);
        Require(Alpha(arcBitmap, 500, 6) > 240 && Alpha(arcBitmap, 465, 7) == 0 && Alpha(arcBitmap, 500, 11) == 0, "Semicircle is an inward half ellipse with empty outer corners");
        var triangleSettings = Settings(EdgeOverlayShape.Triangle);
        var triangleBitmap = Render(Layout(Shapes(triangleSettings, new Rect(0, 0, 1000, 800)), 1000, 800), 1000, 800);
        Require(Alpha(triangleBitmap, 499, 50) > 240 && Alpha(triangleBitmap, 482, 50) == 0 && Alpha(triangleBitmap, 499, 65) == 0, "Triangle tapers inward, not outward or to a filled box");
        Require(Alpha(triangleBitmap, 500, 400) == 0, "Screen center remains transparent");

        var alphaSettings = Settings(EdgeOverlayShape.Rectangle);
        alphaSettings.ColorHex = "#12ABEF";
        alphaSettings.Opacity = 60;
        var common = Shapes(alphaSettings, new Rect(0, 0, 1000, 800));
        Require(common.All(shape => ((SolidColorBrush)shape.Fill).Color == Color.FromArgb(153, 0x12, 0xAB, 0xEF)), "Uniform 60% alpha is floor(percent*255/100) and custom RGB is retained");
        alphaSettings.UsePerEdgeOpacity = true;
        alphaSettings.TopOpacity = 25;
        alphaSettings.BottomOpacity = 75;
        alphaSettings.LeftOpacity = 50;
        alphaSettings.RightOpacity = 100;
        var pixels = Render(Layout(Shapes(alphaSettings, new Rect(0, 0, 1000, 800)), 1000, 800), 1000, 800);
        Require(Alpha(pixels, 500, 2) == 63 && Alpha(pixels, 500, 797) == 191 && Alpha(pixels, 2, 400) == 127 && Alpha(pixels, 997, 400) == 255, "Per-edge opacity is applied independently to actual pixels");
        alphaSettings.TopVisible = false;
        var noTop = Shapes(alphaSettings, new Rect(0, 0, 1000, 800));
        var hiddenPixels = Render(Layout(noTop, 1000, 800), 1000, 800);
        Require(noTop.Count == 3 && Alpha(hiddenPixels, 500, 2) == 0 && Alpha(hiddenPixels, 500, 797) == 191, "Hiding one edge leaves the other three unchanged");

        RequireRect(SafeArea(1920, 1080, EdgeOverlayAspectRatio.FullScreen), new Rect(0, 0, 1920, 1080), "Full-screen safe area");
        RequireRect(SafeArea(1920, 1080, EdgeOverlayAspectRatio.UltraWide21x9), new Rect(0, 128.57142857142856, 1920, 822.8571428571429), "21:9 is the largest centered inset");
        RequireRect(SafeArea(1920, 1080, EdgeOverlayAspectRatio.Standard4x3), new Rect(240, 0, 1440, 1080), "4:3 is the largest centered inset");
        RequireRect(SafeArea(1920, 1080, EdgeOverlayAspectRatio.Standard5x4), new Rect(285, 0, 1350, 1080), "5:4 is the largest centered inset");
        RequireRect(SafeArea(800, 1200, EdgeOverlayAspectRatio.Standard4x3), new Rect(0, 300, 800, 600), "Portrait area letterboxes instead of overflowing");

        var splitSettings = Settings(EdgeOverlayShape.Rectangle);
        splitSettings.AspectRatio = EdgeOverlayAspectRatio.Standard4x3;
        splitSettings.Split = EdgeOverlaySplit.SideBySide;
        var sideBySide = Shapes(splitSettings, new Rect(0, 0, 1000, 800));
        var sideCanvas = Layout(sideBySide, 1000, 800);
        Require(sideBySide.Count == 8, "Side-by-side draws all four edges in each half");
        RequireBounds(sideBySide, sideCanvas,
        [new Rect(244, 25, 12, 6), new Rect(244, 769, 12, 6), new Rect(0, 394, 6, 12), new Rect(494, 394, 6, 12),
         new Rect(744, 25, 12, 6), new Rect(744, 769, 12, 6), new Rect(500, 394, 6, 12), new Rect(994, 394, 6, 12)],
            "Aspect ratio is inset before side-by-side split; each half has an internal edge");
        splitSettings.Split = EdgeOverlaySplit.TopBottom;
        var topBottom = Shapes(splitSettings, new Rect(0, 0, 1000, 800));
        RequireBounds(topBottom, Layout(topBottom, 1000, 800),
        [new Rect(494, 25, 12, 6), new Rect(494, 394, 12, 6), new Rect(0, 206.5, 6, 12), new Rect(994, 206.5, 6, 12),
         new Rect(494, 400, 12, 6), new Rect(494, 769, 12, 6), new Rect(0, 581.5, 6, 12), new Rect(994, 581.5, 6, 12)],
            "Top-bottom split preserves inset bounds and draws both internal edges");

        var offset = Shapes(Settings(EdgeOverlayShape.Rectangle), new Rect(100, 50, 1000, 800));
        RequireBounds(offset, Layout(offset, 1200, 950),
        [new Rect(594, 50, 12, 6), new Rect(594, 844, 12, 6), new Rect(100, 444, 6, 12), new Rect(1094, 444, 6, 12)],
            "Non-origin monitor bounds retain their position");
        var windowSettings = Settings(EdgeOverlayShape.Rectangle);
        windowSettings.AspectRatio = EdgeOverlayAspectRatio.Standard4x3;
        var windowShapes = Shapes(windowSettings, new Rect(100, 50, 1000, 800), applyAspectRatio: false);
        RequireBounds(windowShapes, Layout(windowShapes, 1200, 950),
        [new Rect(594, 50, 12, 6), new Rect(594, 844, 12, 6), new Rect(100, 444, 6, 12), new Rect(1094, 444, 6, 12)],
            "Window mode uses the target window rectangle without aspect ratio insets");
        var scaled = Shapes(Settings(EdgeOverlayShape.Rectangle), new Rect(0, 0, 1000, 800), scale: 2);
        RequireBounds(scaled, Layout(scaled, 1000, 800),
        [new Rect(488, 0, 24, 12), new Rect(488, 788, 24, 12), new Rect(0, 388, 12, 24), new Rect(988, 388, 12, 24)],
            "A 2:1 monitor/window DPI ratio doubles marker size without moving its edge center");
        var clippedSettings = Settings(EdgeOverlayShape.Triangle);
        clippedSettings.Size = 6;
        clippedSettings.AspectRatio = EdgeOverlayAspectRatio.UltraWide21x9;
        clippedSettings.Split = EdgeOverlaySplit.TopBottom;
        var clippedShapes = Shapes(clippedSettings, new Rect(0, 0, 1000, 800));
        var clippedBitmap = Render(Layout([clippedShapes[0]], 1000, 800), 1000, 800);
        Require(Alpha(clippedBitmap, 500, 250) > 240 && Alpha(clippedBitmap, 500, 500) == 0 && Alpha(clippedBitmap, 500, 175) == 0, "Oversized triangle is clipped to its own half and safe area");
        SaveBitmap(arcBitmap, "overlay-semicircle-original.png");
        SaveBitmap(triangleBitmap, "overlay-triangle-original.png");
        SaveBitmap(pixels, "overlay-per-edge-alpha.png");
    }

    private static void RunSettingsAndUi()
    {
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        var receiveDirectory = Path.Combine(EvidenceDirectory, "synthetic-received");
        var settingsPath = Path.Combine(dataDirectory, "settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new
        {
            receiveDirectory,
            clickSoundEnabled = false,
            clickSoundVolume = 0.21,
            completionSoundEnabled = false,
            completionSoundVolume = 0.34,
            clickSoundChoice = 2,
            completionSoundChoice = 3,
            themeChoice = "Silver",
            deviceId = "windows-overlay-regression-synthetic",
            trustedDeviceKeys = new Dictionary<string, string> { ["android-regression-only"] = "synthetic-key-not-a-real-pairing-secret" }
        }));
        var window = new MainWindow();
        var closed = false;
        try
        {
            var field = WindowType.GetFields(Private).Single(item => item.FieldType == typeof(EdgeOverlaySettings));
            var settings = (EdgeOverlaySettings)field.GetValue(window)!;
            Require(!settings.Enabled && settings.Size == 3 && settings.Length == 0 && settings.ColorHex == "#00FF00" && settings.Opacity == 60, "Legacy settings without edge overlay load with a disabled default");
            settings.Shape = EdgeOverlayShape.Triangle;
            settings.Size = 2;
            settings.Length = 5;
            settings.Opacity = 38;
            settings.ColorHex = "#234567";
            settings.UsePerEdgeOpacity = true;
            settings.LeftVisible = false;
            settings.RightOpacity = 83;
            settings.TopOpacity = 7;
            settings.BottomOpacity = 11;
            settings.LeftOpacity = 23;
            settings.AspectRatio = EdgeOverlayAspectRatio.Standard5x4;
            settings.Split = EdgeOverlaySplit.SideBySide;
            settings.DisplayMode = EdgeOverlayDisplayMode.Window;
            Call(window, "SaveSettings");
            using (var json = JsonDocument.Parse(File.ReadAllText(settingsPath)))
            {
                var root = json.RootElement;
                Require(root.GetProperty("deviceId").GetString() == "windows-overlay-regression-synthetic" && root.GetProperty("trustedDeviceKeys").GetProperty("android-regression-only").GetString() == "synthetic-key-not-a-real-pairing-secret", "Saving overlay settings preserves old device identity and pairing fields");
                Require(root.GetProperty("receiveDirectory").GetString() == receiveDirectory && root.GetProperty("themeChoice").GetString() == "Silver", "Saving overlay settings preserves receive and theme settings from legacy JSON");
                Require(new[] { "clickSoundEnabled", "clickSoundVolume", "completionSoundEnabled", "completionSoundVolume", "clickSoundChoice", "completionSoundChoice" }.All(key => !root.TryGetProperty(key, out _)), "Saving overlay settings loaded from legacy JSON drops only the retired sound fields");
            }
            field.SetValue(window, new EdgeOverlaySettings());
            Call(window, "LoadSettings");
            settings = (EdgeOverlaySettings)field.GetValue(window)!;
            Require(settings.Shape == EdgeOverlayShape.Triangle && settings.Size == 2 && settings.Length == 5 && settings.Opacity == 38 && settings.ColorHex == "#234567" && settings.UsePerEdgeOpacity && !settings.LeftVisible && settings.RightOpacity == 83 && settings.TopOpacity == 7 && settings.BottomOpacity == 11 && settings.LeftOpacity == 23 && settings.AspectRatio == EdgeOverlayAspectRatio.Standard5x4 && settings.Split == EdgeOverlaySplit.SideBySide && settings.DisplayMode == EdgeOverlayDisplayMode.Window, "Overlay settings save and reload in isolated storage");
            RenderSettingsUi(window);
            Toggle(Control<CheckBox>(window, "EdgeOverlayEnabledBox"), true);
            var overlayField = WindowType.GetField("_edgeOverlayWindow", Private)!;
            var overlay = (EdgeOverlayWindow)overlayField.GetValue(window)!;
            var overlayHandle = new WindowInteropHelper(overlay).Handle;
            window.Close();
            closed = true;
            DrainDispatcher();
            Require(overlayField.GetValue(window) is null && !IsWindow(overlayHandle), "Closing the main window destroys its active overlay and releases its reference");
        }
        finally { if (!closed) window.Close(); }
    }

    private static void RenderSettingsUi(MainWindow window)
    {
        RunWorkspaceNavigation(window);
        var panel = Control<Grid>(window, "EdgeOverlaySettingsPanel");
        var settingsField = WindowType.GetField("_edgeOverlaySettings", Private)!;
        var overlayField = WindowType.GetField("_edgeOverlayWindow", Private)!;
        var settings = (EdgeOverlaySettings)settingsField.GetValue(window)!;
        Require(panel.Visibility == Visibility.Visible && Control<Grid>(window, "EdgeOverlayView").Visibility == Visibility.Visible && Control<Grid>(window, "PreferencesView").Visibility == Visibility.Collapsed, "The workspace navigation opens edge overlay independently of preferences");
        Require(Control<ComboBox>(window, "EdgeOverlayShapeBox").Items.Count == 3 && Control<ComboBox>(window, "EdgeOverlaySizeBox").Items.Count == 7 && Control<ComboBox>(window, "EdgeOverlayLengthBox").Items.Count == 7 && Control<ComboBox>(window, "EdgeOverlayAspectRatioBox").Items.Count == 4 && Control<ComboBox>(window, "EdgeOverlaySplitBox").Items.Count == 3, "UI exposes the agreed shapes, size/length steps, ratios, and split modes");
        Require(Control<ComboBox>(window, "EdgeOverlayShapeBox").SelectedIndex == (int)EdgeOverlayShape.Triangle && Control<ComboBox>(window, "EdgeOverlaySizeBox").SelectedIndex == 2 && Control<ComboBox>(window, "EdgeOverlayLengthBox").SelectedIndex == 5 && Control<TextBox>(window, "EdgeOverlayColorBox").Text == "#234567", "Saved overlay settings populate the actual controls");
        Require(!Control<ComboBox>(window, "EdgeOverlayAspectRatioBox").IsEnabled && Control<Grid>(window, "EdgeOverlayPerEdgePanel").IsEnabled && !Control<Slider>(window, "EdgeOverlayOpacitySlider").IsEnabled, "Mode and opacity controls accurately show which settings apply");
        var colorBox = Control<TextBox>(window, "EdgeOverlayColorBox");
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "data", "settings.json");
        var savedBeforeInvalidColor = File.ReadAllText(settingsPath);
        colorBox.Text = "#12";
        Require(settings.ColorHex == "#234567" && Control<TextBlock>(window, "EdgeOverlayColorHint").Text.Contains("有效"), "Incomplete HEX input displays a hint and preserves the valid color");
        colorBox.Text = "# 00000";
        Require(settings.ColorHex == "#234567" && File.ReadAllText(settingsPath) == savedBeforeInvalidColor, "HEX input with internal whitespace does not throw, overwrite the valid color, or save invalid data");
        colorBox.Text = "#a1b2c3";
        Require(settings.ColorHex == "#A1B2C3" && ((SolidColorBrush)Control<Border>(window, "EdgeOverlayColorPreview").Background).Color == Color.FromRgb(0xA1, 0xB2, 0xC3), "Valid HEX input is normalized and reflected in the preview");
        var redPreset = LogicalDescendants(panel).OfType<Button>().Single(button => button.Tag as string == "#FF0000");
        redPreset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(settings.ColorHex == "#FF0000" && colorBox.Text == "#FF0000", "Color preset button follows the same save path as custom color");
        var bluePreset = LogicalDescendants(panel).OfType<Button>().Single(button => button.Tag as string == "#0099FF");
        bluePreset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(settings.ColorHex == "#0099FF" && colorBox.Text == "#0099FF", "Blue preset retains the original #0099FF color");
        Control<ComboBox>(window, "EdgeOverlayDisplayModeBox").SelectedIndex = (int)EdgeOverlayDisplayMode.Stretch;
        Control<ComboBox>(window, "EdgeOverlayAspectRatioBox").SelectedIndex = (int)EdgeOverlayAspectRatio.Standard4x3;
        Control<ComboBox>(window, "EdgeOverlaySplitBox").SelectedIndex = (int)EdgeOverlaySplit.TopBottom;
        Control<Slider>(window, "EdgeOverlayTopOpacitySlider").Value = 25;
        Control<Slider>(window, "EdgeOverlayBottomOpacitySlider").Value = 75;
        Toggle(Control<CheckBox>(window, "EdgeOverlayLeftVisibleBox"), true);
        Toggle(Control<CheckBox>(window, "EdgeOverlayTopVisibleBox"), false);
        Require(settings.DisplayMode == EdgeOverlayDisplayMode.Stretch && settings.AspectRatio == EdgeOverlayAspectRatio.Standard4x3 && settings.Split == EdgeOverlaySplit.TopBottom && settings.TopOpacity == 25 && settings.BottomOpacity == 75 && settings.LeftVisible && !settings.TopVisible && Control<ComboBox>(window, "EdgeOverlayAspectRatioBox").IsEnabled, "Programmatic control events update the intended overlay settings");
        Toggle(Control<CheckBox>(window, "EdgeOverlayPerEdgeBox"), false);
        Control<Slider>(window, "EdgeOverlayOpacitySlider").Value = 42;
        Require(!settings.UsePerEdgeOpacity && settings.Opacity == 42 && !Control<Grid>(window, "EdgeOverlayPerEdgePanel").IsEnabled && Control<Grid>(window, "EdgeOverlayPerEdgePanel").Visibility == Visibility.Collapsed && Control<Slider>(window, "EdgeOverlayOpacitySlider").IsEnabled, "Uniform opacity mode hides per-edge controls and saves its value");
        Toggle(Control<CheckBox>(window, "EdgeOverlayPerEdgeBox"), true);
        Require(Control<Grid>(window, "EdgeOverlayPerEdgePanel").Visibility == Visibility.Visible && Control<Grid>(window, "EdgeOverlayPerEdgePanel").IsEnabled && settings.TopOpacity == 25 && settings.BottomOpacity == 75, "Expanding per-edge opacity restores the controls without discarding their values");
        Toggle(Control<CheckBox>(window, "EdgeOverlayEnabledBox"), true);
        var overlay = (EdgeOverlayWindow)overlayField.GetValue(window)!;
        var handle = new WindowInteropHelper(overlay).Handle;
        Require(overlay.IsVisible && IsWindow(handle) && Control<TextBlock>(window, "EdgeOverlaySummaryText").Text.Contains("已开启"), "The actual enable checkbox creates the integrated overlay and reports its state");
        Control<ComboBox>(window, "EdgeOverlayShapeBox").SelectedIndex = (int)EdgeOverlayShape.Semicircle;
        Call(window, "ApplyEdgeOverlaySettings");
        Require(ReferenceEquals(overlayField.GetValue(window), overlay) && new WindowInteropHelper(overlay).Handle == handle && settings.Shape == EdgeOverlayShape.Semicircle, "Changing shape and reapplying reuse one overlay window");
        Toggle(Control<CheckBox>(window, "EdgeOverlayEnabledBox"), false);
        DrainDispatcher();
        Require(overlayField.GetValue(window) is null && !IsWindow(handle) && !settings.Enabled && Control<TextBlock>(window, "EdgeOverlaySummaryText").Text.Contains("已关闭"), "The actual disable checkbox releases the integrated overlay and reports its state");
        Call(window, "LoadSettings");
        Call(window, "RefreshEdgeOverlayControls");
        settings = (EdgeOverlaySettings)settingsField.GetValue(window)!;
        Require(settings.Shape == EdgeOverlayShape.Semicircle && settings.ColorHex == "#0099FF" && !settings.Enabled && settings.TopOpacity == 25 && settings.BottomOpacity == 75 && !settings.TopVisible, "Control changes persist through the real settings load/save methods");

        CaptureWorkspaceLayouts(window);
        Require(!window.IsVisible && WindowType.GetField("_listener", Private)!.GetValue(window) is null, "Offscreen rendering never shows the main application or starts its network listener");
    }

    private static void RunWorkspaceNavigation(MainWindow window)
    {
        var destinations = new[]
        {
            (Button: "RecentNavButton", View: "HomeView"),
            (Button: "ArchiveButton", View: "NotesView"),
            (Button: "MindMapNavButton", View: "TasksView"),
            (Button: "TrashNavButton", View: "TrashView"),
            (Button: "PreferencesNavButton", View: "PreferencesView"),
            (Button: "EdgeOverlayNavButton", View: "EdgeOverlayView"),
            (Button: "CrosshairNavButton", View: "CrosshairView")
        };
        foreach (var destination in destinations)
        {
            Click(window, "EdgeOverlayNavButton");
            Click(window, destination.Button);
            Require(destinations.All(item => Control<Grid>(window, item.View).Visibility == (item == destination ? Visibility.Visible : Visibility.Collapsed)), $"Clicking {destination.Button} displays only its workspace");
            Require(destinations.All(item => ReferenceEquals(Control<Button>(window, item.Button).Style, window.FindResource(item == destination ? "ActiveNavButton" : "NavButton"))), $"Clicking {destination.Button} resets the other navigation highlights");
        }
        Click(window, "PreferencesNavButton");
        Require(!LogicalDescendants(Control<Grid>(window, "PreferencesHomePanel")).OfType<Button>().Any(button => System.Windows.Automation.AutomationProperties.GetName(button) == "边缘叠加" || LogicalDescendants(button).OfType<TextBlock>().Any(text => text.Text == "边缘叠加")), "Preferences no longer contains a duplicate edge overlay entry");
        Require(!Control<TextBlock>(window, "PreferencesSubtitleText").Text.Contains("边缘叠加") && !Control<TextBlock>(window, "PreferencesSubtitleText").Text.Contains("声音") && !Control<TextBlock>(window, "PreferencesSubtitleText").Text.Contains("音效"), "Preferences describes only its remaining appearance setting");
        Click(window, "EdgeOverlayNavButton");
        using (var source = new HwndSource(new HwndSourceParameters("isolated-overlay-key-event") { Width = 1, Height = 1, WindowStyle = 0 }))
        {
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(escape);
            Require(escape.Handled && Control<Grid>(window, "HomeView").Visibility == Visibility.Visible && Control<Grid>(window, "EdgeOverlayView").Visibility == Visibility.Collapsed && ReferenceEquals(Control<Button>(window, "RecentNavButton").Style, window.FindResource("ActiveNavButton")), "Escape from edge overlay returns to the recent-transfer workspace");
        }
        Click(window, "EdgeOverlayNavButton");
    }

    private static void CaptureWorkspaceLayouts(MainWindow window)
    {
        var root = (FrameworkElement)window.Content;
        var view = Control<Grid>(window, "EdgeOverlayView");
        var scroll = Control<ScrollViewer>(window, "EdgeOverlayScrollViewer");
        var colorBox = Control<TextBox>(window, "EdgeOverlayColorBox");
        foreach (var size in new[] { new Size(1240, 800), new Size(980, 680) })
        {
            Toggle(Control<CheckBox>(window, "EdgeOverlayEnabledBox"), false);
            Toggle(Control<CheckBox>(window, "EdgeOverlayPerEdgeBox"), false);
            LayoutRoot(root, size);
            scroll.ScrollToTop();
            root.UpdateLayout();
            Require(Control<ComboBox>(window, "EdgeOverlayShapeBox").ActualWidth >= 120 && Control<ComboBox>(window, "EdgeOverlaySplitBox").ActualWidth >= 120 && colorBox.ActualWidth >= 100 && scroll.ViewportHeight > 200 && scroll.ExtentWidth <= scroll.ViewportWidth + 1, $"{size.Width}x{size.Height} keeps controls readable without horizontal clipping");
            SaveBitmap(Render(root, (int)size.Width, (int)size.Height), $"edge-workspace-{size.Width}x{size.Height}-disabled-offscreen.png");
            Toggle(Control<CheckBox>(window, "EdgeOverlayEnabledBox"), true);
            root.UpdateLayout();
            SaveBitmap(Render(root, (int)size.Width, (int)size.Height), $"edge-workspace-{size.Width}x{size.Height}-enabled-offscreen.png");
            Toggle(Control<CheckBox>(window, "EdgeOverlayPerEdgeBox"), true);
            root.UpdateLayout();
            foreach (var control in LogicalDescendants(scroll).OfType<Control>().Where(control => control is ComboBox or TextBox or Slider or CheckBox))
            {
                if (control.ActualWidth == 0) continue;
                var position = control.TranslatePoint(new Point(), scroll);
                Require(position.X >= -1 && position.X + control.ActualWidth <= scroll.ViewportWidth + 1, $"{size.Width}x{size.Height} keeps {control.Name} inside the form width");
            }
            SaveBitmap(Render(root, (int)size.Width, (int)size.Height), $"edge-workspace-{size.Width}x{size.Height}-per-edge-top-offscreen.png");
            scroll.ScrollToBottom();
            root.UpdateLayout();
            var lastSlider = Control<Slider>(window, "EdgeOverlayRightOpacitySlider");
            var lastPosition = lastSlider.TranslatePoint(new Point(0, 0), scroll);
            Require(lastSlider.IsEnabled && lastPosition.Y >= 0 && lastPosition.Y + lastSlider.ActualHeight <= scroll.ActualHeight, $"{size.Width}x{size.Height} scrolling exposes the usable final per-edge opacity control");
            SaveBitmap(Render(root, (int)size.Width, (int)size.Height), $"edge-workspace-{size.Width}x{size.Height}-per-edge-bottom-offscreen.png");
            CaptureDropDown(Control<ComboBox>(window, "EdgeOverlayShapeBox"), $"edge-shape-dropdown-{size.Width}x{size.Height}-template-offscreen.png");
        }
        Toggle(Control<CheckBox>(window, "EdgeOverlayEnabledBox"), false);
        colorBox.Text = "#F3F4F6";
        var swatches = LogicalDescendants(view).OfType<Border>().Where(border => border.Tag as string == "ThemePreview" && border.Background is SolidColorBrush).ToDictionary(border => border, border => ((SolidColorBrush)border.Background).Color);
        foreach (var theme in new[] { "Silver", "Japanese", "DeathStranding" })
        {
            Call(window, "ApplyTheme", theme, false);
            Call(window, "ApplyThemeToVisualTree");
            Require(swatches.All(item => ((SolidColorBrush)item.Key.Background).Color == item.Value) && ((SolidColorBrush)Control<Border>(window, "EdgeOverlayColorPreview").Background).Color == Color.FromRgb(0xF3, 0xF4, 0xF6), $"{theme} preserves the custom color and all color swatches during theme recoloring");
            Require(Contrast(((SolidColorBrush)colorBox.Foreground).Color, ((SolidColorBrush)colorBox.Background).Color) >= 4.5 && ReferenceEquals(Control<Button>(window, "EdgeOverlayNavButton").Style, window.FindResource("ActiveNavButton")), $"{theme} keeps HEX input readable and the workspace navigation active");
            foreach (var size in new[] { new Size(1240, 800), new Size(980, 680) })
            {
                LayoutRoot(root, size);
                scroll.ScrollToTop();
                root.UpdateLayout();
                SaveBitmap(Render(root, (int)size.Width, (int)size.Height), $"edge-workspace-{theme}-{size.Width}x{size.Height}-offscreen.png");
            }
        }
        Call(window, "ApplyTheme", "Silver", false);
        Call(window, "ApplyThemeToVisualTree");
        colorBox.Text = "#0099FF";
    }

    private static void CaptureDropDown(ComboBox combo, string name)
    {
        combo.ApplyTemplate();
        if (combo.Template.FindName("PART_Popup", combo) is not Popup { Child: FrameworkElement content })
        {
            Console.WriteLine("INFO: dropdown template cannot be rendered offscreen; real popup interaction remains unverified");
            return;
        }
        var width = Math.Max(120, combo.ActualWidth);
        content.Measure(new Size(width, double.PositiveInfinity));
        content.Arrange(new Rect(0, 0, width, Math.Ceiling(content.DesiredSize.Height)));
        content.UpdateLayout();
        var items = combo.Items.Cast<ComboBoxItem>().ToArray();
        if (content.ActualHeight == 0 || items.Any(item => item.ActualHeight == 0))
        {
            Console.WriteLine("INFO: hidden combo did not materialize dropdown items; real popup interaction remains unverified");
            return;
        }
        Require(items.All(item =>
        {
            var position = item.TranslatePoint(new Point(), content);
            return position.X >= -1 && position.Y >= -1 && position.X + item.ActualWidth <= content.ActualWidth + 1 && position.Y + item.ActualHeight <= content.ActualHeight + 1;
        }), "The actual shape dropdown template exposes every option without clipping");
        SaveBitmap(Render(content, (int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight)), name);
    }

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte value) { var normalized = value / 255d; return normalized <= 0.04045 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static void LayoutRoot(FrameworkElement root, Size size)
    {
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
    }

    private static void RunWindowLifecycle()
    {
        var overlay = new EdgeOverlayWindow();
        var closed = false;
        try
        {
            var settings = Settings(EdgeOverlayShape.Rectangle);
            settings.Enabled = false;
            overlay.ApplySettings(settings);
            Require(!overlay.IsVisible, "Disabled settings do not display the overlay");
            settings.Enabled = true;
            overlay.ApplySettings(settings);
            DrainDispatcher();
            var handle = new WindowInteropHelper(overlay).Handle;
            Require(handle != IntPtr.Zero && IsWindow(handle) && overlay.IsVisible, "Enabling creates a visible overlay HWND");
            long styles = IntPtr.Size == 8 ? GetWindowLongPtr(handle, -20).ToInt64() : GetWindowLong(handle, -20);
            Require((styles & 0x20) != 0 && (styles & 0x80) != 0 && (styles & 0x08000000) != 0, "Actual HWND has WS_EX_TRANSPARENT, TOOLWINDOW, and NOACTIVATE");
            Require(SendMessage(handle, 0x0084, IntPtr.Zero, IntPtr.Zero).ToInt64() == -1 && SendMessage(handle, 0x0021, IntPtr.Zero, IntPtr.Zero).ToInt64() == 3, "Actual window procedure returns HTTRANSPARENT and MA_NOACTIVATE for hit-test and mouse-activate messages");
            Require(!overlay.ShowInTaskbar && !overlay.ShowActivated && !overlay.IsActive, "Overlay does not activate or appear in the taskbar");
            settings.DisplayMode = EdgeOverlayDisplayMode.Window;
            overlay.ApplySettings(settings);
            var timer = (DispatcherTimer)typeof(EdgeOverlayWindow).GetField("_foregroundTimer", Private)!.GetValue(overlay)!;
            var topmostTimer = (DispatcherTimer)typeof(EdgeOverlayWindow).GetField("_topmostTimer", Private)!.GetValue(overlay)!;
            Require(timer.IsEnabled, "Window mode starts foreground tracking");
            Require(topmostTimer.IsEnabled && topmostTimer.Interval == TimeSpan.FromMilliseconds(500), "Active overlay maintains topmost position on the original 500 ms interval");
            settings.Enabled = false;
            overlay.ApplySettings(settings);
            DrainDispatcher();
            Require(!overlay.IsVisible && !timer.IsEnabled && !topmostTimer.IsEnabled && ((Canvas)overlay.Content).Children.Count == 0, "Disabling hides the overlay, stops both timers, and clears the drawn shapes");
            settings.Enabled = true;
            settings.DisplayMode = EdgeOverlayDisplayMode.Stretch;
            overlay.ApplySettings(settings);
            DrainDispatcher();
            Require(overlay.IsVisible, "Overlay can be enabled again after disabling");
            settings.DisplayMode = EdgeOverlayDisplayMode.Window;
            overlay.ApplySettings(settings);
            Require(timer.IsEnabled, "Foreground tracking restarts before close cleanup");
            overlay.Close();
            closed = true;
            DrainDispatcher();
            Require(!IsWindow(handle) && !overlay.IsVisible && !timer.IsEnabled && !topmostTimer.IsEnabled, "Close destroys the overlay HWND and leaves no timers running");
        }
        finally { if (!closed) overlay.Close(); }
    }

    private static EdgeOverlaySettings Settings(EdgeOverlayShape shape) => new() { Enabled = true, Shape = shape, Size = 0, Length = 0, Opacity = 100 };
    private static List<Shape> Shapes(EdgeOverlaySettings settings, Rect bounds, double scale = 1, bool applyAspectRatio = true)
    {
        var method = GeometryType.GetMethod("CreateShapes", Private)!;
        var values = new object?[] { settings, bounds, scale, applyAspectRatio };
        return ((IEnumerable)method.Invoke(null, values.Take(method.GetParameters().Length).ToArray())!).Cast<Shape>().ToList();
    }
    private static Rect SafeArea(double width, double height, EdgeOverlayAspectRatio aspect) => (Rect)GeometryType.GetMethod("GetSafeArea", Private)!.Invoke(null, [width, height, aspect])!;
    private static object? Call(object instance, string method, params object[] arguments) => instance.GetType().GetMethod(method, Private)!.Invoke(instance, arguments);
    private static T Control<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static void Click(MainWindow window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Toggle(CheckBox box, bool value)
    {
        box.IsChecked = value;
        box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in LogicalDescendants(child)) yield return descendant;
        }
    }
    private static Canvas Layout(IReadOnlyList<Shape> shapes, double width, double height)
    {
        var canvas = new Canvas { Width = width, Height = height };
        foreach (var shape in shapes) canvas.Children.Add(shape);
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();
        return canvas;
    }
    private static RenderTargetBitmap Render(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }
    private static byte Alpha(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3];
    }
    private static void SaveBitmap(BitmapSource bitmap, string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(EvidenceDirectory, name));
        encoder.Save(file);
    }
    private static void RequireBounds(IReadOnlyList<Shape> shapes, Visual parent, IReadOnlyList<Rect> expected, string message)
    {
        var remaining = shapes.Select(shape => shape.TransformToAncestor(parent).TransformBounds(shape.RenderedGeometry.Bounds)).ToList();
        foreach (var rect in expected)
        {
            var match = remaining.FindIndex(actual => RectNear(actual, rect));
            if (match < 0) throw new InvalidOperationException($"{message}: expected {rect}; actual {string.Join(" | ", remaining)}");
            remaining.RemoveAt(match);
        }
        Require(remaining.Count == 0, message);
    }
    private static bool RectNear(Rect actual, Rect expected) => Math.Abs(actual.X - expected.X) < 0.02 && Math.Abs(actual.Y - expected.Y) < 0.02 && Math.Abs(actual.Width - expected.Width) < 0.02 && Math.Abs(actual.Height - expected.Height) < 0.02;
    private static void RequireRect(Rect actual, Rect expected, string message) => Require(RectNear(actual, expected), $"{message}: {actual}");
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Passed.Add(message);
        Console.WriteLine("OK: " + message);
    }
    private static void DrainDispatcher() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int index);
}
