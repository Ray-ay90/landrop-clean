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
    private static readonly List<string> Limitations = [];
    private static readonly string EvidenceDirectory = Path.Combine(AppContext.BaseDirectory, "evidence");
    private static readonly Type GeometryType = typeof(CrosshairWindow).Assembly.GetType("LanDrop.Windows.CrosshairGeometry", true)!;
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
            WriteReport("passed");
            Console.WriteLine($"PASS: {Passed.Count} isolated crosshair checks; evidence: {EvidenceDirectory}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            WriteReport("failed", error.ToString());
            return 1;
        }
        finally { app?.Shutdown(); }
    }

    private static void WriteReport(string result, string? error = null) => File.WriteAllText(
        Path.Combine(EvidenceDirectory, "report.json"),
        JsonSerializer.Serialize(new { result, checks = Passed, limitations = Limitations, error }, new JsonSerializerOptions { WriteIndented = true }));

    private static void RunGeometry()
    {
        // Fixed lookup values and topology come from the statically inspected MotionStabilizer 2.6.0.0 CIL.
        var sizes = new[] { 6d, 10d, 16d, 24d, 34d, 48d, 68d };
        var thicknesses = new[] { 1d, 2d, 3d, 4d, 5d, 7d, 10d };
        for (var index = 0; index < sizes.Length; index++)
        {
            var settings = Settings(CrosshairShape.Circle);
            settings.Size = index;
            settings.Thickness = index;
            var ellipses = Shapes(settings, new Rect(0, 0, 1000, 800)).OfType<Ellipse>().OrderByDescending(shape => shape.Width).ToArray();
            Require(ellipses.Length == 2 && ellipses[0].Width == sizes[index] && ellipses[0].Height == sizes[index] && ellipses[0].StrokeThickness == thicknesses[index] && ellipses[1].Width == thicknesses[index] && ellipses[1].Height == thicknesses[index], $"Original CIL circle size/thickness step {index} has the fixed diameter, outline, and center-dot dimensions");
            Require(Canvas.GetLeft(ellipses[0]) == 500 - sizes[index] / 2 && Canvas.GetTop(ellipses[0]) == 400 - sizes[index] / 2 && Canvas.GetLeft(ellipses[1]) == 500 - thicknesses[index] / 2 && Canvas.GetTop(ellipses[1]) == 400 - thicknesses[index] / 2, $"Original CIL circle step {index} centers its ring and point");
        }
        foreach (var shape in Enum.GetValues<CrosshairShape>())
        {
            var settings = Settings(shape);
            var shapes = Shapes(settings, new Rect(0, 0, 1000, 800));
            var canvas = Layout(shapes, 1000, 800);
            Require(shapes.Count == (shape == CrosshairShape.Circle ? 2 : shape == CrosshairShape.Cross ? 3 : 1), $"{shape} retains the observed component count");
            RequireCentered(shapes, canvas, new Point(500, 400), $"{shape} is centered in the full-screen area");
            var bitmap = Render(canvas, 1000, 800);
            Require(Alpha(bitmap, 500, 400) == (shape == CrosshairShape.Diamond ? 0 : 255), $"{shape} retains the observed center-point or empty-center behavior");
            Require(Alpha(bitmap, 520, 420) == 0, $"{shape} leaves surrounding screen pixels transparent");
            if (shape == CrosshairShape.Cross)
            {
                var lines = shapes.OfType<Line>().ToArray();
                var lineBounds = lines.Select(line => Bounds(line, canvas)).ToArray();
                Require(lineBounds.Any(rect => RectNear(rect, new Rect(488, 400, 24, 0))) && lineBounds.Any(rect => RectNear(rect, new Rect(500, 388, 0, 24))) && lines.All(line => line.StrokeThickness == 4), "Original CIL cross has two complete 24-DIP lines with 4-DIP thickness");
                Require(Alpha(bitmap, 510, 400) == 255 && Alpha(bitmap, 500, 390) == 255 && Alpha(bitmap, 505, 405) == 0, "Cross renders full intersecting arms without filling diagonal space");
            }
            if (shape == CrosshairShape.Diamond)
            {
                var figure = ((PathGeometry)((System.Windows.Shapes.Path)shapes.Single()).Data).Figures.Single();
                var vertices = new[] { figure.StartPoint }.Concat(figure.Segments.Cast<LineSegment>().Select(segment => segment.Point)).ToArray();
                Require(figure.IsClosed && !figure.IsFilled && vertices.SequenceEqual(new[] { new Point(500, 388), new Point(512, 400), new Point(500, 412), new Point(488, 400) }), "Original CIL diamond uses four cardinal vertices of an unfilled closed 24-DIP outline");
            }
            SaveBitmap(bitmap, $"crosshair-{shape.ToString().ToLowerInvariant()}-offscreen.png");
        }

        var offset = Settings(CrosshairShape.Circle);
        var offsetShapes = Shapes(offset, new Rect(100, 50, 1000, 800));
        RequireCentered(offsetShapes, Layout(offsetShapes, 1200, 950), new Point(600, 450), "Non-origin screen bounds retain their own center");

        var alpha = Settings(CrosshairShape.Cross);
        alpha.ColorHex = "#12ABEF";
        alpha.Opacity = 60;
        var alphaShapes = Shapes(alpha, new Rect(0, 0, 1000, 800));
        var brushes = alphaShapes.SelectMany(shape => new[] { shape.Fill, shape.Stroke }).OfType<SolidColorBrush>().Where(brush => brush.Color.A != 0).ToArray();
        Require(brushes.Length > 0 && brushes.All(brush => brush.Color == Color.FromArgb(153, 0x12, 0xAB, 0xEF)), "Original 60 percent alpha and custom RGB are preserved in every crosshair component");
        alpha.Opacity = 0;
        var transparent = Render(Layout(Shapes(alpha, new Rect(0, 0, 1000, 800)), 1000, 800), 1000, 800);
        Require(Alpha(transparent, 500, 400) == 0, "Zero opacity leaves the center transparent");

        var split = Settings(CrosshairShape.Circle);
        split.AspectRatio = EdgeOverlayAspectRatio.Standard4x3;
        split.Split = EdgeOverlaySplit.SideBySide;
        var leftRight = Shapes(split, new Rect(0, 0, 1000, 800));
        var lrCanvas = Layout(leftRight, 1000, 800);
        RequireCenters(leftRight, lrCanvas, [new Point(250, 400), new Point(750, 400)], "4:3 safe area is split into independent left and right crosshair centers");
        split.Split = EdgeOverlaySplit.TopBottom;
        var topBottom = Shapes(split, new Rect(0, 0, 1000, 800));
        RequireCenters(topBottom, Layout(topBottom, 1000, 800), [new Point(500, 212.5), new Point(500, 587.5)], "4:3 safe area is split into independent top and bottom crosshair centers");

        var shifted = Settings(CrosshairShape.Circle);
        shifted.PositionX = 30;
        shifted.PositionY = -20;
        var shiftedShapes = Shapes(shifted, new Rect(100, 50, 1000, 800), 2);
        var shiftedCanvas = Layout(shiftedShapes, 1200, 950);
        RequireCentered(shiftedShapes, shiftedCanvas, new Point(630, 430), "Position is an integer DIP offset and is not multiplied by the monitor/window scale");
        var scaledRing = shiftedShapes.OfType<Ellipse>().OrderByDescending(shape => shape.Width).First();
        Require(scaledRing.Width == 48 && scaledRing.StrokeThickness == 8, "A 2:1 DPI ratio doubles diameter and thickness while preserving DIP position offset");

        var thinCross = Settings(CrosshairShape.Cross);
        thinCross.Thickness = 0;
        var thinDot = Shapes(thinCross, new Rect(0, 0, 1000, 800)).OfType<Ellipse>().Single();
        Require(thinDot.Width == 2 && thinDot.Height == 2 && Canvas.GetLeft(thinDot) == 499.5 && Canvas.GetTop(thinDot) == 399.5, "Original minimum-thickness cross preserves its 2-DIP dot and original half-DIP point placement");
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
            deviceId = "windows-crosshair-regression-synthetic",
            trustedDeviceKeys = new Dictionary<string, string> { ["android-regression-only"] = "synthetic-key-not-a-real-pairing-secret" },
            edgeOverlay = new EdgeOverlaySettings { Enabled = false, Shape = EdgeOverlayShape.Triangle, Size = 1, Length = 5, ColorHex = "#234567", Opacity = 38, RightVisible = false }
        }));
        var window = new MainWindow();
        var closed = false;
        try
        {
            var settingsField = WindowType.GetField("_crosshairSettings", Private)!;
            var overlayField = WindowType.GetField("_crosshairWindow", Private)!;
            var settings = (CrosshairSettings)settingsField.GetValue(window)!;
            Require(!settings.Enabled && settings.Shape == CrosshairShape.Cross && settings.Size == 3 && settings.Thickness == 3 && settings.ColorHex == "#FF0000" && settings.CustomColorHex == "#FF0000" && settings.Opacity == 80 && settings.PositionX == 0 && settings.PositionY == 0 && settings.MonitorDeviceName == "", "Legacy settings without crosshair load with the observed disabled, all-screen defaults");
            settings.Shape = CrosshairShape.Diamond;
            settings.Size = 2;
            settings.Thickness = 5;
            settings.AspectRatio = EdgeOverlayAspectRatio.Standard5x4;
            settings.Split = EdgeOverlaySplit.SideBySide;
            settings.PositionX = 15;
            settings.PositionY = -20;
            settings.MonitorDeviceName = "regression-display-only";
            settings.ColorHex = "#234567";
            settings.CustomColorHex = "#345678";
            settings.Opacity = 38;
            Call(window, "SaveSettings");
            using (var json = JsonDocument.Parse(File.ReadAllText(settingsPath)))
            {
                var root = json.RootElement;
                Require(root.GetProperty("deviceId").GetString() == "windows-crosshair-regression-synthetic" && root.GetProperty("trustedDeviceKeys").GetProperty("android-regression-only").GetString() == "synthetic-key-not-a-real-pairing-secret", "Saving crosshair preserves synthetic device identity and pairing");
                Require(root.GetProperty("receiveDirectory").GetString() == receiveDirectory && root.GetProperty("themeChoice").GetString() == "Silver", "Saving crosshair preserves receive and theme settings from legacy JSON");
                Require(new[] { "clickSoundEnabled", "clickSoundVolume", "completionSoundEnabled", "completionSoundVolume", "clickSoundChoice", "completionSoundChoice" }.All(key => !root.TryGetProperty(key, out _)), "Saving crosshair settings loaded from legacy JSON drops only the retired sound fields");
                var edge = root.GetProperty("edgeOverlay");
                Require(!edge.GetProperty("enabled").GetBoolean() && edge.GetProperty("shape").GetInt32() == 2 && edge.GetProperty("size").GetInt32() == 1 && edge.GetProperty("length").GetInt32() == 5 && edge.GetProperty("colorHex").GetString() == "#234567" && edge.GetProperty("opacity").GetInt32() == 38 && !edge.GetProperty("rightVisible").GetBoolean(), "Saving crosshair preserves independent edge-overlay settings");
            }
            settingsField.SetValue(window, new CrosshairSettings());
            Call(window, "LoadSettings");
            settings = (CrosshairSettings)settingsField.GetValue(window)!;
            Require(settings.Shape == CrosshairShape.Diamond && settings.Size == 2 && settings.Thickness == 5 && settings.AspectRatio == EdgeOverlayAspectRatio.Standard5x4 && settings.Split == EdgeOverlaySplit.SideBySide && settings.PositionX == 15 && settings.PositionY == -20 && settings.MonitorDeviceName == "regression-display-only" && settings.ColorHex == "#234567" && settings.CustomColorHex == "#345678" && settings.Opacity == 38, "All crosshair fields, including remembered custom color and selected display, save and reload through the real settings methods");
            Call(window, "RefreshCrosshairControls");
            var monitorBox = Control<ComboBox>(window, "CrosshairMonitorBox");
            Require(monitorBox.SelectedItem is ComboBoxItem { Tag: "regression-display-only" } && settings.MonitorDeviceName == "regression-display-only", "An offline selected display remains selected and preserved in the workspace");
            settings.MonitorDeviceName = "";
            Call(window, "SaveSettings");
            Call(window, "RefreshCrosshairControls");
            RunNavigation(window);
            Require(Control<ComboBox>(window, "CrosshairShapeBox").Items.Count == 3 && Control<ComboBox>(window, "CrosshairSizeBox").Items.Count == 7 && Control<ComboBox>(window, "CrosshairThicknessBox").Items.Count == 7 && Control<ComboBox>(window, "CrosshairAspectRatioBox").Items.Count == 4 && Control<ComboBox>(window, "CrosshairSplitBox").Items.Count == 3, "Workspace exposes all observed shapes, size/thickness steps, ratios, and splits");
            var allDisplays = monitorBox.Items.OfType<ComboBoxItem>().Single(item => item.Tag as string == "");
            var oneDisplay = monitorBox.Items.OfType<ComboBoxItem>().First(item => !string.IsNullOrEmpty(item.Tag as string));
            monitorBox.SelectedItem = oneDisplay;
            Require(settings.MonitorDeviceName == oneDisplay.Tag as string, "Selecting a display follows the actual settings save path");
            monitorBox.SelectedItem = allDisplays;
            Require(settings.MonitorDeviceName == "", "The all-displays option restores the original per-screen mode");
            Require(Control<ComboBox>(window, "CrosshairShapeBox").SelectedIndex == 2 && Control<ComboBox>(window, "CrosshairSizeBox").SelectedIndex == 2 && Control<ComboBox>(window, "CrosshairThicknessBox").SelectedIndex == 5 && Control<TextBox>(window, "CrosshairPositionXBox").Text == "15" && Control<TextBox>(window, "CrosshairPositionYBox").Text == "-20", "Saved settings populate actual crosshair controls");

            var colorBox = Control<TextBox>(window, "CrosshairColorBox");
            var savedBeforeInvalid = File.ReadAllText(settingsPath);
            colorBox.Text = "#12";
            Require(settings.ColorHex == "#234567" && File.ReadAllText(settingsPath) == savedBeforeInvalid, "Incomplete HEX input retains the valid color and saved settings");
            colorBox.Text = "# 00000";
            Require(settings.ColorHex == "#234567" && File.ReadAllText(settingsPath) == savedBeforeInvalid, "HEX whitespace is rejected without overwriting valid settings");
            colorBox.Text = "#a1b2c3";
            Require(settings.ColorHex == "#A1B2C3" && settings.CustomColorHex == "#A1B2C3" && ((SolidColorBrush)Control<Border>(window, "CrosshairColorPreview").Background).Color == Color.FromRgb(0xA1, 0xB2, 0xC3), "Valid HEX input is normalized, remembered as custom, and previewed");
            var view = Control<Grid>(window, "CrosshairView");
            var redPreset = LogicalDescendants(view).OfType<Button>().Single(button => button.Tag as string == "#FF0000");
            redPreset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(settings.ColorHex == "#FF0000" && settings.CustomColorHex == "#A1B2C3", "Selecting the red preset preserves the last custom color");
            var greenPreset = LogicalDescendants(view).OfType<Button>().Single(button => button.Tag as string == "#00FF00");
            greenPreset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(settings.ColorHex == "#00FF00" && settings.CustomColorHex == "#A1B2C3", "Switching between presets keeps the remembered custom color");
            Click(window, "CrosshairCustomColorButton");
            Require(settings.ColorHex == "#A1B2C3" && colorBox.Text == "#A1B2C3" && settings.CustomColorHex == "#A1B2C3", "The custom-color button restores the last entered custom color after preset changes");
            Control<ComboBox>(window, "CrosshairShapeBox").SelectedIndex = (int)CrosshairShape.Circle;
            Control<ComboBox>(window, "CrosshairSizeBox").SelectedIndex = 4;
            Control<ComboBox>(window, "CrosshairThicknessBox").SelectedIndex = 1;
            Control<ComboBox>(window, "CrosshairAspectRatioBox").SelectedIndex = (int)EdgeOverlayAspectRatio.Standard4x3;
            Control<ComboBox>(window, "CrosshairSplitBox").SelectedIndex = (int)EdgeOverlaySplit.TopBottom;
            var positionX = Control<TextBox>(window, "CrosshairPositionXBox");
            var positionY = Control<TextBox>(window, "CrosshairPositionYBox");
            positionX.Text = "-12";
            positionY.Text = "18";
            Control<Slider>(window, "CrosshairOpacitySlider").Value = 42;
            Require(settings.Shape == CrosshairShape.Circle && settings.Size == 4 && settings.Thickness == 1 && settings.AspectRatio == EdgeOverlayAspectRatio.Standard4x3 && settings.Split == EdgeOverlaySplit.TopBottom && settings.PositionX == -12 && settings.PositionY == 18 && settings.Opacity == 42, "Actual selection, position text, and opacity events update all intended fields");
            var savedBeforeInvalidPosition = File.ReadAllText(settingsPath);
            positionX.Text = "-";
            Require(settings.PositionX == -12 && File.ReadAllText(settingsPath) == savedBeforeInvalidPosition, "An incomplete signed position preserves the last valid model and saved settings");
            positionX.Text = "2147483648";
            Require(settings.PositionX == -12 && File.ReadAllText(settingsPath) == savedBeforeInvalidPosition, "Position overflow is rejected by integer parsing without saving invalid text");
            positionX.Text = "-12";
            Call(window, "CrosshairResetPositionButton_Click", window, new RoutedEventArgs());
            Require(settings.PositionX == 0 && settings.PositionY == 0 && positionX.Text == "0" && positionY.Text == "0", "Reset position clears both model and text fields");
            Require(Control<Canvas>(window, "CrosshairPreviewCanvas").Children.Count > 0, "The workspace includes a rendered crosshair preview");

            Toggle(Control<CheckBox>(window, "CrosshairEnabledBox"), true);
            var overlay = (CrosshairWindow)overlayField.GetValue(window)!;
            var handle = new WindowInteropHelper(overlay).Handle;
            Require(overlay.IsVisible && IsWindow(handle) && Control<TextBlock>(window, "CrosshairSummaryText").Text.Contains("已开启"), "Enabling creates the integrated crosshair window and reports its state");
            Control<ComboBox>(window, "CrosshairShapeBox").SelectedIndex = (int)CrosshairShape.Cross;
            Require(ReferenceEquals(overlayField.GetValue(window), overlay) && new WindowInteropHelper(overlay).Handle == handle, "Changing crosshair style reuses one native overlay window");
            Toggle(Control<CheckBox>(window, "EdgeOverlayEnabledBox"), true);
            var edgeField = WindowType.GetField("_edgeOverlayWindow", Private)!;
            var edgeOverlay = (EdgeOverlayWindow)edgeField.GetValue(window)!;
            var edgeHandle = new WindowInteropHelper(edgeOverlay).Handle;
            Require(IsWindow(handle) && IsWindow(edgeHandle) && !ReferenceEquals(overlay, edgeOverlay), "Crosshair and edge overlays can be enabled independently together");
            Toggle(Control<CheckBox>(window, "CrosshairEnabledBox"), false);
            DrainDispatcher();
            Require(overlayField.GetValue(window) is null && !IsWindow(handle) && IsWindow(edgeHandle) && !settings.Enabled && Control<TextBlock>(window, "CrosshairSummaryText").Text.Contains("已关闭"), "Disabling crosshair destroys only its own overlay");
            Toggle(Control<CheckBox>(window, "EdgeOverlayEnabledBox"), false);
            Call(window, "LoadSettings");
            Call(window, "RefreshCrosshairControls");
            settings = (CrosshairSettings)settingsField.GetValue(window)!;
            Require(settings.Shape == CrosshairShape.Cross && settings.Size == 4 && settings.Thickness == 1 && settings.PositionX == 0 && settings.PositionY == 0 && settings.ColorHex == "#A1B2C3" && settings.Opacity == 42 && !settings.Enabled, "Control edits and reset persist after isolated reload");
            CaptureLayouts(window);
            settingsField.SetValue(window, new CrosshairSettings());
            Call(window, "RefreshCrosshairControls");
            Call(window, "SaveSettings");
            CaptureDefaultLayouts(window);
            Require(!window.IsVisible && WindowType.GetField("_listener", Private)!.GetValue(window) is null, "Offscreen workspace checks never show the main application or start its listener");
            Toggle(Control<CheckBox>(window, "CrosshairEnabledBox"), true);
            Toggle(Control<CheckBox>(window, "EdgeOverlayEnabledBox"), true);
            var closingCrosshair = new WindowInteropHelper((CrosshairWindow)overlayField.GetValue(window)!).Handle;
            var closingEdge = new WindowInteropHelper((EdgeOverlayWindow)edgeField.GetValue(window)!).Handle;
            window.Close();
            closed = true;
            DrainDispatcher();
            Require(overlayField.GetValue(window) is null && edgeField.GetValue(window) is null && !IsWindow(closingCrosshair) && !IsWindow(closingEdge), "Closing the main window releases both independent overlays");
        }
        finally { if (!closed) window.Close(); }
    }

    private static void RunNavigation(MainWindow window)
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
            Click(window, "CrosshairNavButton");
            Click(window, destination.Button);
            Require(destinations.All(item => Control<Grid>(window, item.View).Visibility == (item == destination ? Visibility.Visible : Visibility.Collapsed)), $"Clicking {destination.Button} displays only its workspace");
            Require(destinations.All(item => ReferenceEquals(Control<Button>(window, item.Button).Style, window.FindResource(item == destination ? "ActiveNavButton" : "NavButton"))), $"Clicking {destination.Button} resets other navigation highlights");
        }
        using (var source = new HwndSource(new HwndSourceParameters("isolated-crosshair-key-event") { Width = 1, Height = 1, WindowStyle = 0 }))
        {
            var shapeBox = Control<ComboBox>(window, "CrosshairShapeBox");
            shapeBox.IsDropDownOpen = true;
            if (shapeBox.IsDropDownOpen)
            {
                var closeDropdown = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(closeDropdown);
                Require(closeDropdown.Handled && !shapeBox.IsDropDownOpen && Control<Grid>(window, "CrosshairView").Visibility == Visibility.Visible, "Escape closes an open crosshair dropdown before leaving its workspace");
            }
            else
            {
                const string limitation = "The hidden WPF ComboBox did not open its popup; Escape priority for an open dropdown requires interactive validation.";
                Limitations.Add(limitation);
                Console.WriteLine("INFO: " + limitation);
                shapeBox.IsDropDownOpen = false;
            }
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(escape);
            Require(escape.Handled && Control<Grid>(window, "HomeView").Visibility == Visibility.Visible && Control<Grid>(window, "CrosshairView").Visibility == Visibility.Collapsed, "Escape leaves the crosshair workspace and returns home");
        }
        Click(window, "CrosshairNavButton");
    }

    private static void CaptureLayouts(MainWindow window)
    {
        var root = (FrameworkElement)window.Content;
        var view = Control<Grid>(window, "CrosshairView");
        var colorBox = Control<TextBox>(window, "CrosshairColorBox");
        var scroll = LogicalDescendants(view).OfType<ScrollViewer>().First();
        colorBox.Text = "#F3F4F6";
        var swatches = LogicalDescendants(view).OfType<Border>().Where(border => border.Tag as string == "ThemePreview" && border.Background is SolidColorBrush).ToDictionary(border => border, border => ((SolidColorBrush)border.Background).Color);
        foreach (var theme in new[] { "Silver", "Japanese", "DeathStranding" })
        {
            Call(window, "ApplyTheme", theme, false);
            Call(window, "ApplyThemeToVisualTree");
            Require(swatches.All(item => ((SolidColorBrush)item.Key.Background).Color == item.Value) && ((SolidColorBrush)Control<Border>(window, "CrosshairColorPreview").Background).Color == Color.FromRgb(0xF3, 0xF4, 0xF6), $"{theme} preserves the selected color and preset swatches");
            Require(Contrast(((SolidColorBrush)colorBox.Foreground).Color, ((SolidColorBrush)colorBox.Background).Color) >= 4.5 && ReferenceEquals(Control<Button>(window, "CrosshairNavButton").Style, window.FindResource("ActiveNavButton")), $"{theme} keeps HEX input readable and workspace navigation active");
            foreach (var size in new[] { new Size(1240, 800), new Size(980, 680) })
            {
                LayoutRoot(root, size);
                scroll.ScrollToTop();
                root.UpdateLayout();
                Require(colorBox.ActualWidth >= 90 && Control<ComboBox>(window, "CrosshairShapeBox").ActualWidth >= 100 && scroll.ViewportHeight > 200 && scroll.ExtentWidth <= scroll.ViewportWidth + 1, $"{theme} {size.Width}x{size.Height} keeps readable controls without horizontal overflow");
                SaveBitmap(Render(root, (int)size.Width, (int)size.Height), $"crosshair-workspace-{theme}-{size.Width}x{size.Height}-top-offscreen.png");
                scroll.ScrollToBottom();
                root.UpdateLayout();
                foreach (var control in LogicalDescendants(scroll).OfType<Control>().Where(control => control is ComboBox or TextBox or Slider or CheckBox))
                {
                    if (control.ActualWidth == 0) continue;
                    var point = control.TranslatePoint(new Point(), scroll);
                    Require(point.X >= -1 && point.X + control.ActualWidth <= scroll.ViewportWidth + 1, $"{theme} {size.Width}x{size.Height} keeps {control.Name} within the form width");
                }
                var lastPosition = Control<TextBox>(window, "CrosshairPositionYBox").TranslatePoint(new Point(), scroll);
                Require(lastPosition.Y >= 0 && lastPosition.Y + Control<TextBox>(window, "CrosshairPositionYBox").ActualHeight <= scroll.ActualHeight, $"{theme} {size.Width}x{size.Height} scrolling exposes the final position field");
                SaveBitmap(Render(root, (int)size.Width, (int)size.Height), $"crosshair-workspace-{theme}-{size.Width}x{size.Height}-bottom-offscreen.png");
            }
        }
        Call(window, "ApplyTheme", "Silver", false);
        Call(window, "ApplyThemeToVisualTree");
        colorBox.Text = "#A1B2C3";
    }

    private static void CaptureDefaultLayouts(MainWindow window)
    {
        var root = (FrameworkElement)window.Content;
        var scroll = Control<ScrollViewer>(window, "CrosshairScrollViewer");
        Call(window, "ApplyTheme", "Silver", false);
        Call(window, "ApplyThemeToVisualTree");
        foreach (var size in new[] { new Size(1240, 800), new Size(980, 680) })
        {
            LayoutRoot(root, size);
            scroll.ScrollToTop();
            root.UpdateLayout();
            SaveBitmap(Render(root, (int)size.Width, (int)size.Height), $"default-silver-{size.Width}x{size.Height}-offscreen.png");
        }
    }

    private static void RunWindowLifecycle()
    {
        var overlay = new CrosshairWindow();
        var closed = false;
        try
        {
            var settings = Settings(CrosshairShape.Cross);
            settings.Enabled = false;
            overlay.ApplySettings(settings);
            Require(!overlay.IsVisible, "Disabled crosshair settings do not display a window");
            settings.Enabled = true;
            overlay.ApplySettings(settings);
            DrainDispatcher();
            var handle = new WindowInteropHelper(overlay).Handle;
            Require(handle != IntPtr.Zero && IsWindow(handle) && overlay.IsVisible, "Enabling creates a visible dedicated crosshair HWND");
            var styles = IntPtr.Size == 8 ? GetWindowLongPtr(handle, -20).ToInt64() : GetWindowLong(handle, -20);
            Require((styles & 0x08) != 0 && (styles & 0x20) != 0 && (styles & 0x80) != 0 && (styles & 0x08000000) != 0, "Crosshair HWND has TOPMOST, TRANSPARENT, TOOLWINDOW, and NOACTIVATE extended styles");
            Require(SendMessage(handle, 0x0084, IntPtr.Zero, IntPtr.Zero).ToInt64() == -1 && SendMessage(handle, 0x0021, IntPtr.Zero, IntPtr.Zero).ToInt64() == 3, "Crosshair window procedure returns HTTRANSPARENT and MA_NOACTIVATE");
            Require(!overlay.ShowInTaskbar && !overlay.ShowActivated && !overlay.IsActive, "Crosshair never activates or appears in the taskbar");
            var displays = CrosshairWindow.GetDisplays();
            Require(displays.Count > 0 && displays.All(display => !string.IsNullOrWhiteSpace(display.DeviceName)), "Display enumeration returns stable device names for the active screens");
            var allCount = ((Canvas)overlay.Content).Children.Count;
            Require(allCount == displays.Count * 3, "All-screen mode creates a complete crosshair for each currently active display");
            settings.MonitorDeviceName = displays[0].DeviceName;
            overlay.ApplySettings(settings);
            Require(((Canvas)overlay.Content).Children.Count == 3, "Selecting one active display draws only one complete crosshair");
            settings.MonitorDeviceName = "regression-nonexistent-display";
            overlay.ApplySettings(settings);
            Require(((Canvas)overlay.Content).Children.Count == allCount && settings.MonitorDeviceName == "regression-nonexistent-display", "An unavailable selected display temporarily falls back to all screens without discarding the saved choice");
            settings.MonitorDeviceName = "";
            overlay.ApplySettings(settings);
            settings.Enabled = false;
            overlay.ApplySettings(settings);
            DrainDispatcher();
            Require(!overlay.IsVisible && ((Canvas)overlay.Content).Children.Count == 0, "Disabling hides crosshair and clears drawn geometry");
            settings.Enabled = true;
            overlay.ApplySettings(settings);
            Require(overlay.IsVisible && new WindowInteropHelper(overlay).Handle == handle, "A disabled dedicated window can be enabled again");
            overlay.Close();
            closed = true;
            DrainDispatcher();
            Require(!IsWindow(handle) && !overlay.IsVisible, "Closing destroys the dedicated crosshair HWND");
            var timers = typeof(CrosshairWindow).GetFields(Private).Where(field => field.FieldType == typeof(DispatcherTimer)).Select(field => (DispatcherTimer)field.GetValue(overlay)!).ToArray();
            Require(timers.All(timer => !timer.IsEnabled), "Closing leaves no crosshair timers running");
        }
        finally { if (!closed) overlay.Close(); }
    }

    private static CrosshairSettings Settings(CrosshairShape shape) => new() { Enabled = true, Shape = shape, Size = 3, Thickness = 3, Opacity = 100 };
    private static List<Shape> Shapes(CrosshairSettings settings, Rect bounds, double scale = 1) => ((IEnumerable)GeometryType.GetMethod("CreateShapes", Private)!.Invoke(null, [settings, bounds, scale])!).Cast<Shape>().ToList();
    private static object? Call(object instance, string method, params object[] arguments) => instance.GetType().GetMethod(method, Private)!.Invoke(instance, arguments);
    private static T Control<T>(MainWindow window, string name) where T : FrameworkElement => (T)(window.FindName(name) ?? throw new InvalidOperationException($"Missing control: {name}"));
    private static void Click(MainWindow window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Toggle(CheckBox box, bool value) { box.IsChecked = value; box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
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
        LayoutRoot(canvas, new Size(width, height));
        return canvas;
    }
    private static void LayoutRoot(FrameworkElement root, Size size) { root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
    private static Rect Bounds(Shape shape, Visual parent) => shape.TransformToAncestor(parent).TransformBounds(shape.RenderedGeometry.Bounds);
    private static Point Center(Rect rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    private static bool RectNear(Rect actual, Rect expected) => Math.Abs(actual.X - expected.X) < 0.02 && Math.Abs(actual.Y - expected.Y) < 0.02 && Math.Abs(actual.Width - expected.Width) < 0.02 && Math.Abs(actual.Height - expected.Height) < 0.02;
    private static bool PointNear(Point first, Point second) => Math.Abs(first.X - second.X) < 0.02 && Math.Abs(first.Y - second.Y) < 0.02;
    private static void RequireCentered(IReadOnlyList<Shape> shapes, Visual parent, Point center, string message)
    {
        var union = Rect.Empty;
        foreach (var shape in shapes) union.Union(Bounds(shape, parent));
        Require(PointNear(Center(union), center), $"{message}: actual center {Center(union)}, expected {center}");
    }
    private static void RequireCenters(IReadOnlyList<Shape> shapes, Visual parent, IReadOnlyList<Point> centers, string message)
    {
        var actual = shapes.Select(shape => Center(Bounds(shape, parent))).ToArray();
        Require(actual.All(point => centers.Any(center => PointNear(point, center))) && centers.All(center => actual.Any(point => PointNear(point, center))), message);
    }
    private static RenderTargetBitmap Render(Visual visual, int width, int height) { var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); return bitmap; }
    private static byte Alpha(BitmapSource bitmap, int x, int y) { var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel[3]; }
    private static void SaveBitmap(BitmapSource bitmap, string name) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(EvidenceDirectory, name)); encoder.Save(file); }
    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte value) { var normalized = value / 255d; return normalized <= 0.04045 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); Passed.Add(message); Console.WriteLine("OK: " + message); }
    private static void DrainDispatcher() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int index);
}
