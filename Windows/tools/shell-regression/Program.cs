using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LanDrop.Windows;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    private static readonly List<string> Passed = [];
    private static readonly string EvidenceDirectory = Path.Combine(AppContext.BaseDirectory, "evidence");
    private static readonly string[] LegacySoundKeys = ["clickSoundEnabled", "clickSoundVolume", "completionSoundEnabled", "completionSoundVolume", "clickSoundChoice", "completionSoundChoice"];
    private static readonly byte[] LegacySoundMarker = [82, 73, 70, 70, 0, 0, 0, 0, 87, 65, 86, 69];
    private static readonly string LegacySoundPath = Path.Combine(AppContext.BaseDirectory, "data", "sounds", "synthetic-retained.wav");
    private static readonly (string Button, string View, string Title)[] Destinations =
    [
        ("RecentNavButton", "HomeView", "最近传输"),
        ("ArchiveButton", "NotesView", "本地记录"),
        ("MindMapNavButton", "TasksView", "任务"),
        ("CrosshairNavButton", "CrosshairView", "屏幕准心"),
        ("EdgeOverlayNavButton", "EdgeOverlayView", "边缘叠加"),
        ("TrashNavButton", "TrashView", "回收站"),
        ("PreferencesNavButton", "PreferencesView", "偏好设置")
    ];

    [STAThread]
    private static int Main()
    {
        Directory.CreateDirectory(EvidenceDirectory);
        Application? app = null;
        MainWindow? window = null;
        try
        {
            PrepareSyntheticData();
            app = new App();
            ((App)app).InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window = new MainWindow();
            Require(!window.IsLoaded && !window.IsVisible && Field(window, "_listener") is null, "Constructing the test window does not show it or start its receive listener");
            RunSoundRemoval(window);
            RunNavigationAndShell(window);
            RunIcon(window);
            RunPreferences(window);
            RunNotes(window);
            RunConnectionDialog(window);
            CaptureThemes(window);
            Require(!window.IsLoaded && !window.IsVisible && Field(window, "_listener") is null, "All shell checks finish without showing the main window or starting its listener");
            Report("passed");
            Console.WriteLine($"PASS: {Passed.Count} isolated shell checks; evidence: {EvidenceDirectory}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            Report("failed", error.ToString());
            return 1;
        }
        finally
        {
            window?.Close();
            app?.Shutdown();
        }
    }

    private static void PrepareSyntheticData()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new
        {
            receiveDirectory = Path.Combine(AppContext.BaseDirectory, "received"),
            clickSoundEnabled = true,
            clickSoundVolume = 1.0,
            completionSoundEnabled = true,
            completionSoundVolume = 1.0,
            clickSoundChoice = 4,
            completionSoundChoice = 4,
            themeChoice = "Silver",
            deviceId = "windows-shell-regression-synthetic",
            trustedDeviceKeys = new Dictionary<string, string>(),
            edgeOverlay = new EdgeOverlaySettings { Enabled = false },
            crosshair = new CrosshairSettings { Enabled = false }
        }));
        File.WriteAllText(Path.Combine(directory, "notes.json"), "[]");
        File.WriteAllText(Path.Combine(directory, "tasks.json"), "[]");
        Directory.CreateDirectory(Path.GetDirectoryName(LegacySoundPath)!);
        File.WriteAllBytes(LegacySoundPath, LegacySoundMarker);
    }

    private static void RunSoundRemoval(MainWindow window)
    {
        var type = typeof(MainWindow);
        var removedMethods = new[] { "PlayFunctionSound", "PlayCompletionSound", "PlaySynthesizedSound", "PlayPreset", "TryPlayOverride", "SoundPresetButton_Click", "PreviewClickSoundButton_Click", "PreviewCompletionSoundButton_Click", "OpenSoundSettingsButton_Click" };
        Require(removedMethods.All(name => type.GetMethod(name, Private) is null), "The compiled application no longer contains sound playback, synthesis, override, or settings handlers");
        var removedFields = new[] { "_soundDirectory", "_soundPlayer", "_soundStream", "_clickSoundEnabled", "_clickSoundVolume", "_clickSoundChoice", "_completionSoundEnabled", "_completionSoundVolume", "_completionSoundChoice" };
        Require(removedFields.All(name => type.GetField(name, Private) is null), "The compiled application no longer stores sound paths, players, streams, or preferences");
        var settingsType = type.GetNestedType("AppSettings", BindingFlags.NonPublic)!;
        Require(settingsType.GetProperties(Private).All(property => !property.Name.Contains("Sound", StringComparison.OrdinalIgnoreCase)), "The settings contract no longer declares sound preferences");
        Require(window.FindName("OpenSoundSettingsButton") is null && window.FindName("SoundSettingsPanel") is null && !Application.Current.Resources.Contains("SoundTile"), "Sound controls and their dedicated style are absent from the compiled UI");
        Call(window, "SaveSettings");
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "settings.json")));
        var root = saved.RootElement;
        Require(LegacySoundKeys.All(key => !root.TryGetProperty(key, out _)), "Saving settings loaded from sound-enabled legacy JSON omits every retired sound field");
        Require(root.GetProperty("deviceId").GetString() == "windows-shell-regression-synthetic" && root.GetProperty("themeChoice").GetString() == "Silver" && root.GetProperty("receiveDirectory").GetString() == Path.Combine(AppContext.BaseDirectory, "received"), "Legacy sound removal preserves device identity, theme, and receive directory");
        Require(File.ReadAllBytes(LegacySoundPath).SequenceEqual(LegacySoundMarker), "Removing the sound feature leaves historical custom WAV files untouched");
    }

    private static void RunNavigationAndShell(MainWindow window)
    {
        var root = (FrameworkElement)window.Content;
        var sidebar = Element<Border>(window, "SidebarPanel");
        var sidebarColumn = Named<ColumnDefinition>(window, "SidebarColumn");
        var titleBar = Element<Border>(window, "WorkspaceTitleBar");
        var toggle = Element<Button>(window, "SidebarToggleButton");
        foreach (var size in new[] { new Size(1240, 800), new Size(980, 680) })
        {
            Layout(root, size);
            Require(Math.Abs(sidebarColumn.ActualWidth - 232) < 1 && sidebar.ActualHeight >= root.ActualHeight - 4 && sidebar.TranslatePoint(new Point(), root).Y <= 2, $"{size.Width}x{size.Height} has a 232-DIP sidebar spanning the full window height");
            Require(Math.Abs(titleBar.ActualHeight - 48) < 1 && titleBar.TranslatePoint(new Point(), root).X >= sidebarColumn.ActualWidth, $"{size.Width}x{size.Height} places the compact title bar above the main workspace");
            var before = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "settings.json"));
            Click(window, "SidebarToggleButton");
            Layout(root, size);
            Require(sidebarColumn.ActualWidth < 1 && sidebar.Visibility == Visibility.Collapsed, $"{size.Width}x{size.Height} collapses the sidebar without a residual blank column");
            Require(toggle.Visibility == Visibility.Visible && toggle.ActualWidth >= 24 && toggle.ActualHeight >= 24 && Within(toggle, root), $"{size.Width}x{size.Height} keeps the sidebar expand control reachable in the title bar");
            if (size.Width == 980) Capture(window, size, "shell-Silver-980x680-collapsed.png");
            Click(window, "SidebarToggleButton");
            Layout(root, size);
            Require(Math.Abs(sidebarColumn.ActualWidth - 232) < 1 && sidebar.Visibility == Visibility.Visible && File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "settings.json")) == before, $"{size.Width}x{size.Height} restores the sidebar using only in-memory layout state");
        }
        foreach (var destination in Destinations)
        {
            Click(window, destination.Button);
            Layout(root, new Size(1240, 800));
            Require(Element<TextBlock>(window, "WorkspaceTitleText").Text == destination.Title, $"{destination.Title} navigation updates the title bar");
            Require(Destinations.All(item => Element<Grid>(window, item.View).Visibility == (item == destination ? Visibility.Visible : Visibility.Collapsed)), $"{destination.Title} navigation displays only its selected workspace");
            Require(Destinations.All(item => ReferenceEquals(Element<Button>(window, item.Button).Style, window.FindResource(item == destination ? "ActiveNavButton" : "NavButton"))), $"{destination.Title} navigation keeps one active highlight");
            Require(Within(Element<Grid>(window, destination.View), root) && Within(Element<Button>(window, destination.Button), sidebar), $"{destination.Title} page and navigation stay inside the available window");
            Capture(window, new Size(1240, 800), $"shell-Silver-1240x800-{destination.View}.png");
        }
        foreach (var (viewName, scrollName, lastName) in new[]
        {
            ("CrosshairView", "CrosshairScrollViewer", "CrosshairPositionYBox"),
            ("EdgeOverlayView", "EdgeOverlayScrollViewer", "EdgeOverlayRightOpacitySlider")
        })
        {
            Click(window, viewName == "CrosshairView" ? "CrosshairNavButton" : "EdgeOverlayNavButton");
            if (viewName == "EdgeOverlayView")
            {
                var perEdge = Element<CheckBox>(window, "EdgeOverlayPerEdgeBox");
                perEdge.IsChecked = true;
                perEdge.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            Layout(root, new Size(980, 680));
            var scroll = Element<ScrollViewer>(window, scrollName);
            scroll.ScrollToBottom();
            root.UpdateLayout();
            var last = Element<FrameworkElement>(window, lastName);
            var point = last.TranslatePoint(new Point(), scroll);
            Require(scroll.ExtentWidth <= scroll.ViewportWidth + 1 && point.Y >= -1 && point.Y + last.ActualHeight <= scroll.ActualHeight + 1, $"{viewName} remains horizontally contained and exposes its last control in the narrow shell");
            Capture(window, new Size(980, 680), $"shell-Silver-980x680-{viewName}-bottom.png");
        }
        Call(window, "MaximizeButton_Click", window, new RoutedEventArgs());
        Require(window.WindowState == WindowState.Maximized && !window.IsVisible, "The maximize action changes window state without showing the test window");
        Call(window, "MaximizeButton_Click", window, new RoutedEventArgs());
        Require(window.WindowState == WindowState.Normal && !window.IsVisible, "The maximize action restores normal window state");
    }

    private static void RunIcon(MainWindow window)
    {
        Require(window.Icon is BitmapSource { PixelWidth: >= 16, PixelHeight: >= 16 }, "The window has a decoded application icon instead of the system default");
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/LanDrop;component/Assets/app.ico"))?.Stream;
        Require(stream is not null, "The ICO is packaged as a WPF application resource");
        using (stream)
        {
            var decoder = new IconBitmapDecoder(stream!, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var sizes = decoder.Frames.Select(frame => frame.PixelWidth).ToHashSet();
            Require(new[] { 16, 24, 32, 48, 64, 128, 256 }.All(sizes.Contains), "The icon contains small through high-DPI frames for Windows surfaces");
        }
        var brandImages = Descendants((DependencyObject)window.Content).OfType<Image>()
            .Where(image => image.Source is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } && image.Source.ToString().Contains("brand-v", StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(brandImages.Length > 0, "The shell uses the packaged V brand image");
    }

    private static void RunPreferences(MainWindow window)
    {
        var root = (FrameworkElement)window.Content;
        foreach (var size in new[] { new Size(1240, 800), new Size(980, 680) })
        {
            Click(window, "PreferencesNavButton");
            Layout(root, size);
            var home = Element<Grid>(window, "PreferencesHomePanel");
            Require(home.Visibility == Visibility.Visible && home.ActualHeight <= 140 && Within(home, Element<Grid>(window, "PreferencesView")), $"{size.Width}x{size.Height} keeps the remaining theme preference compact at the top of the workspace");
            Require(Within(Element<Button>(window, "OpenThemeSettingsButton"), home) && Descendants(home).OfType<Button>().Count() == 1, $"{size.Width}x{size.Height} exposes only the reachable theme entry");
            Capture(window, size, $"shell-Silver-{size.Width}x{size.Height}-PreferencesView.png");
            Click(window, "OpenThemeSettingsButton");
            Layout(root, size);
            var panel = Element<Grid>(window, "ThemeSettingsPanel");
            Require(panel.Visibility == Visibility.Visible && home.Visibility == Visibility.Collapsed && Element<TextBlock>(window, "WorkspaceTitleText").Text == "偏好设置", $"{size.Width}x{size.Height} the theme entry opens its preference panel");
            Require(Within(panel, Element<Grid>(window, "PreferencesView")) && Descendants(panel).OfType<Button>().Where(button => button.ActualWidth > 0).All(button => Within(button, panel)), $"{size.Width}x{size.Height} the theme panel keeps its buttons within the viewport");
            Capture(window, size, $"shell-Silver-{size.Width}x{size.Height}-ThemeSettingsPanel.png");
            var back = Descendants(panel).OfType<Button>().Single(button => button.Content is string text && text.Contains("返回偏好设置"));
            back.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(home.Visibility == Visibility.Visible && panel.Visibility == Visibility.Collapsed, $"{size.Width}x{size.Height} the actual theme return button restores preferences");
        }
    }

    private static void RunNotes(MainWindow window)
    {
        var type = typeof(MainWindow).GetNestedType("NoteRecord", BindingFlags.NonPublic)!;
        var notes = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
        foreach (var title in new[] { "工作随记", "训练备忘", "周末安排" })
        {
            var note = Activator.CreateInstance(type)!;
            Set(note, "Id", Guid.NewGuid().ToString("N"));
            Set(note, "Title", title);
            Set(note, "CreatedAt", new DateTimeOffset(2026, 10, 28, 9, 30, 0, TimeSpan.FromHours(8)));
            Set(note, "UpdatedAt", new DateTimeOffset(2026, 10, 28, 9, 30, 0, TimeSpan.FromHours(8)));
            Set(note, "Preview", "整理想法，留下下一步。");
            Set(note, "ContentXaml", "<FlowDocument xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Paragraph>今天的记录</Paragraph><Paragraph>整理想法，留下下一步。</Paragraph><Paragraph>☐ 完成一件小事</Paragraph></FlowDocument>");
            notes.Add(note);
        }
        typeof(MainWindow).GetField("_notes", Private)!.SetValue(window, notes);
        Call(window, "OpenNote", notes[0]!);
        var root = (FrameworkElement)window.Content;
        foreach (var size in new[] { new Size(1240, 800), new Size(980, 680) })
        {
            Layout(root, size);
            Call(window, "ApplyThemeToVisualTree");
            root.UpdateLayout();
            var editor = Element<RichTextBox>(window, "NoteEditor");
            Require(Element<Grid>(window, "NotesView").Visibility == Visibility.Visible && Element<Grid>(window, "EditorView").Visibility == Visibility.Visible, $"{size.Width}x{size.Height} preserves the notes list beside the selected document");
            Require(editor.ActualWidth >= 470 && editor.ActualHeight > 300, $"{size.Width}x{size.Height} retains at least 470 DIP of readable note width");
            var cards = Descendants(Element<StackPanel>(window, "NoteCardsPanel")).OfType<Button>().Where(button => button.Tag is string).ToArray();
            Require(cards.Length == 3 && cards.All(card => card.ActualHeight is >= 80 and <= 100 && card.BorderThickness == new Thickness(0)), $"{size.Width}x{size.Height} renders compact borderless note rows");
            Require(cards.All(card => Descendants(card).OfType<TextBlock>().Any(text => text.Text == "整理想法，留下下一步。" && text.TextWrapping == TextWrapping.NoWrap && text.ActualHeight <= 18)), $"{size.Width}x{size.Height} keeps note previews on one readable line");
            Require(Within(editor, root) && Element<TextBlock>(window, "WorkspaceTitleText").Text == "本地记录", $"{size.Width}x{size.Height} keeps the note editor inside the workspace with its matching title");
            Capture(window, size, $"shell-Silver-{size.Width}x{size.Height}-notes.png");
            if (size.Width == 980)
            {
                Click(window, "SidebarToggleButton");
                Layout(root, size);
                Require(editor.ActualWidth >= 470 && Element<Grid>(window, "NotesView").Visibility == Visibility.Visible, "Collapsing navigation preserves the selected note and its list");
                Capture(window, size, "shell-Silver-980x680-notes-collapsed.png");
                Click(window, "SidebarToggleButton");
            }
        }
    }

    private static void RunConnectionDialog(MainWindow window)
    {
        var root = (FrameworkElement)window.Content;
        var overlay = Element<Grid>(window, "ConnectionOverlay");
        Click(window, "ConnectionSigilButton");
        Require(overlay.Visibility == Visibility.Visible, "The connection entry still opens its pairing dialog");
        foreach (var size in new[] { new Size(1240, 800), new Size(980, 680) })
        {
            Layout(root, size);
            var scroll = Element<ScrollViewer>(window, "ConnectionDialogScrollViewer");
            Require(Within(scroll, overlay) && scroll.ActualHeight <= overlay.ActualHeight + 1 && scroll.ExtentWidth <= scroll.ViewportWidth + 1, $"{size.Width}x{size.Height} constrains the connection dialog to the available height and width");
            scroll.ScrollToTop();
            root.UpdateLayout();
            Capture(window, size, $"shell-Silver-{size.Width}x{size.Height}-connection-top.png");
            scroll.ScrollToBottom();
            root.UpdateLayout();
            var last = Element<TextBlock>(window, "OverlayRecentText");
            var point = last.TranslatePoint(new Point(), scroll);
            Require(point.Y >= -1 && point.Y + last.ActualHeight <= scroll.ActualHeight + 1, $"{size.Width}x{size.Height} scrolling exposes the bottom of the connection dialog");
            Capture(window, size, $"shell-Silver-{size.Width}x{size.Height}-connection-bottom.png");
        }
        overlay.Visibility = Visibility.Collapsed;
    }

    private static void CaptureThemes(MainWindow window)
    {
        var root = (FrameworkElement)window.Content;
        foreach (var theme in new[] { "Silver", "Japanese", "DeathStranding" })
        {
            Click(window, "CrosshairNavButton");
            var previousRoles = Call(window, "CurrentThemeRoles")!;
            Call(window, "ApplyTheme", theme, false);
            Layout(root, new Size(1240, 800));
            ApplyOffscreenTheme(window, previousRoles);
            root.UpdateLayout();
            Require(Within(Element<Border>(window, "SidebarPanel"), root) && Within(Element<Border>(window, "WorkspaceTitleBar"), root), $"{theme} keeps shell surfaces inside the window");
            Capture(window, new Size(1240, 800), $"shell-{theme}-1240x800-crosshair.png");
            Layout(root, new Size(980, 680));
            root.UpdateLayout();
            Capture(window, new Size(980, 680), $"shell-{theme}-980x680-crosshair.png");
        }
    }

    private static T Named<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException($"Missing {typeof(T).Name}: {name}");
    private static T Element<T>(MainWindow window, string name) where T : FrameworkElement => Named<T>(window, name);
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static object? Call(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static void Set(object instance, string name, object value) => instance.GetType().GetProperty(name, Private)!.SetValue(instance, value);
    private static void Click(MainWindow window, string name) => Element<Button>(window, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Layout(FrameworkElement root, Size size) { root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
    private static bool Within(FrameworkElement element, FrameworkElement parent)
    {
        var point = element.TranslatePoint(new Point(), parent);
        return point.X >= -1 && point.Y >= -1 && point.X + element.ActualWidth <= parent.ActualWidth + 1 && point.Y + element.ActualHeight <= parent.ActualHeight + 1;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }
    private static void Capture(MainWindow window, Size size, string name)
    {
        var root = (FrameworkElement)window.Content;
        ApplyOffscreenTheme(window, Call(window, "CurrentThemeRoles")!);
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(EvidenceDirectory, name));
        encoder.Save(file);
    }
    private static void ApplyOffscreenTheme(MainWindow window, object roles)
    {
        // The unshown Window has no template visual child; use the production traversal on its laid-out Content.
        var colors = Application.Current.Resources.OfType<DictionaryEntry>()
            .Where(item => item.Key is string && item.Value is SolidColorBrush)
            .ToDictionary(item => (string)item.Key, item => ((SolidColorBrush)item.Value!).Color.ToString());
        Call(window, "RethemeVisualTree", window.Content, roles, colors);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); Passed.Add(message); Console.WriteLine("OK: " + message); }
    private static void Report(string result, string? error = null) => File.WriteAllText(Path.Combine(EvidenceDirectory, "report.json"), JsonSerializer.Serialize(new
    {
        result,
        checks = Passed,
        limitations = new[] { "Main window and connection screenshots are WPF offscreen renders; no physical mouse, keyboard, window dragging, desktop icon-cache, or multi-monitor acceptance is claimed.", "The connection dialog is rendered before listener startup and has no live pairing token or QR bitmap." },
        error
    }, new JsonSerializerOptions { WriteIndented = true }));
}
