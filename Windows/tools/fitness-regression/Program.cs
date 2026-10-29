using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LanDrop.Windows;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly Type WindowType = typeof(MainWindow);
    private static readonly Type TaskType = WindowType.GetNestedType("WorkoutTask", BindingFlags.NonPublic)!;
    private static readonly Type ListType = typeof(List<>).MakeGenericType(TaskType);
    private static readonly JsonSerializerOptions Options = (JsonSerializerOptions)WindowType.GetField("JsonOptions", Private)!.GetValue(null)!;
    private static readonly string Fixture = """
        [{"id":"fitness-regression-only","title":"隔离健身回归 · 非用户任务","status":"completed","createdAt":100,"updatedAt":200,"startedAt":100,"completedAt":300,
        "exercises":[{"id":"planned","name":"测试动作","restSeconds":90,"notes":"原计划不能被反馈覆盖","sets":[
        {"index":1,"targetWeight":10,"targetReps":12,"actualWeight":10,"actualReps":12,"completed":true,"completedAt":110},
        {"index":2,"targetWeight":10,"targetReps":10,"actualWeight":10,"actualReps":9,"completed":true,"completedAt":120},
        {"index":3,"targetWeight":10,"targetReps":10,"actualWeight":10,"actualReps":10,"completed":false,"completedAt":0}]},
        {"id":"skipped","name":"直臂下拉","sets":[{"index":1,"targetWeight":10,"targetReps":12,"completed":false,"completedAt":0}]}]}]
        """;

    [STAThread]
    private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            Application.Current?.Shutdown();
            Environment.ExitCode = 1;
        }
    }

    private static void Run(string[] args)
    {
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new MainWindow();
        if (args.Contains("--silver"))
        {
            RunSilverRegression(window);
            app.Shutdown();
            return;
        }
        if (args.Length > 1 && args[0] == "--verify")
        {
            var baseline = Read(File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "tools", "fitness-regression", "windows-out.json")));
            WindowType.GetField("_tasks", Private)!.SetValue(window, baseline);
            var incoming = Read(File.ReadAllText(args[1]));
            Require((int)Call(window, "MergeTasks", incoming)! == 1, "Android supplementation merges back to Windows");
            var merged = (IList)WindowType.GetField("_tasks", Private)!.GetValue(window)!;
            var updated = ((IList)Get(((IList)Get(merged[0]!, "Exercises")!)[0]!, "Sets")!)[1]!;
            Require((string?)Get(updated, "MovementQuality") == "compensated" && (string?)Get(updated, "DiscomfortNotes") == "测试：右肩拉扯", "Android group feedback survives Windows read and merge");
            app.Shutdown();
            return;
        }
        var list = Read(Fixture);
        WindowType.GetField("_tasks", Private)!.SetValue(window, list);
        var task = list[0]!;
        Call(window, "NormalizeTask", task);
        var exercise = ((IList)Get(task, "Exercises")!)[0]!;
        var sets = (IList)Get(exercise, "Sets")!;
        Require(Get(task, "Effort") is null && Get(sets[1]!, "Rir") is null && Get(sets[1]!, "MovementQuality") is null && Get(sets[1]!, "Discomfort") is null, "Legacy feedback remains null");
        Require(ReferenceEquals(Call(window, "LastCompletedSet", exercise), sets[1]), "Default feedback binds last completed group, not unfinished group");
        Call(window, "RenderTaskDetail", task);
        Require(FindText(window, "未记录"), "Legacy task renders unrecorded feedback");
        Set(sets[0]!, "Rir", "0");
        Set(sets[1]!, "Rir", "4+");
        Set(sets[1]!, "MovementQuality", "stable");
        Set(sets[1]!, "Discomfort", "none");
        Set(((IList)Get(task, "Exercises")!)[1]!, "SkipReason", "不舒服没做");
        var extraJson = """
          {"id":"fitness-regression-only","title":"隔离健身回归 · 非用户任务","status":"completed","createdAt":100,"updatedAt":250,"startedAt":100,"completedAt":300,
          "effort":"有点费力","discomfort":"present","discomfortNotes":"测试：肩部紧","feedbackNotes":"可以之后补填",
          "exercises":[{"id":"additional","name":"大剪刀","isAdditional":true,"sets":[{"index":1,"actualWeight":0,"actualReps":12,"completed":true,"completedAt":125,"rir":"uncertain","movementQuality":"uncertain"}]}]}
          """;
        var extraTask = Read("[" + extraJson + "]")[0]!;
        ((IList)Get(task, "Exercises")!).Add(((IList)Get(extraTask, "Exercises")!)[0]);
        Set(task, "Effort", Get(extraTask, "Effort"));
        Set(task, "Discomfort", "present");
        Set(task, "DiscomfortNotes", "测试：肩部紧");
        Set(task, "FeedbackNotes", "可以之后补填");
        Call(window, "SaveWorkoutFeedback", task);
        Require((long)Get(task, "UpdatedAt")! > 200 && (long)Get(task, "CompletedAt")! == 300, "Feedback updates timestamp without changing settlement");
        Call(window, "LoadTasks");
        var saved = (IList)WindowType.GetField("_tasks", Private)!.GetValue(window)!;
        var savedJson = JsonSerializer.Serialize(saved, ListType, Options);
        using (var savedDocument = JsonDocument.Parse(savedJson))
        {
            var savedTask = savedDocument.RootElement[0];
            var original = savedTask.GetProperty("exercises")[0];
            Require(original.GetProperty("sets")[0].GetProperty("rir").GetString() == "0" && original.GetProperty("sets")[1].GetProperty("rir").GetString() == "4+", "Zero RIR is distinct from unrecorded and 4+");
            Require(original.GetProperty("sets")[2].GetProperty("rir").ValueKind == JsonValueKind.Null, "Unfinished group feedback stays empty");
            Require(original.GetProperty("notes").GetString() == "原计划不能被反馈覆盖" && original.GetProperty("sets")[1].GetProperty("actualReps").GetInt32() == 9, "Plan and actual group records preserved");
            Require(savedTask.GetProperty("exercises")[2].GetProperty("isAdditional").GetBoolean(), "Additional exercise survives save and reload");
        }
        Require((int)Call(window, "MergeTasks", Read(Fixture))! == 0, "Stale snapshots do not overwrite supplemented feedback");
        var output = Path.Combine(Environment.CurrentDirectory, "tools", "fitness-regression", "windows-out.json");
        File.WriteAllText(output, savedJson);
        Console.WriteLine("PASS: isolated Windows fitness regression; user release/data untouched");
        if (args.Contains("--interactive") || args.Contains("--sync-server"))
        {
            var latest = ((IList)WindowType.GetField("_tasks", Private)!.GetValue(window)!)[0]!;
            Call(window, "RenderTaskDetail", latest);
            window.Title = "Fitness regression · isolated";
            var frame = new System.Windows.Threading.DispatcherFrame();
            window.Closed += (_, _) => frame.Continue = false;
            window.Show();
            if (args.Contains("--sync-server"))
            {
                var pairFile = Path.Combine(Environment.CurrentDirectory, "tools", "fitness-regression", "windows-pair.json");
                var ready = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                var paired = false;
                var supplemented = false;
                ready.Tick += (_, _) =>
                {
                    if (!paired && WindowType.GetField("_listener", Private)!.GetValue(window) is System.Net.Sockets.TcpListener listener && listener.Server.IsBound)
                    {
                        var port = WindowType.GetField("_port", Private)!.GetValue(window);
                        var token = WindowType.GetField("_token", Private)!.GetValue(window);
                        var endpoints = (IList)WindowType.GetField("_localEndpoints", Private)!.GetValue(window)!;
                        var host = endpoints.Count > 0 ? Get(endpoints[0]!, "Address") : "127.0.0.1";
                        File.WriteAllText(pairFile, JsonSerializer.Serialize(new { port, token, host }, Options));
                        paired = true;
                    }
                    var synced = (IList)WindowType.GetField("_tasks", Private)!.GetValue(window)!;
                    if (!supplemented && synced.Count > 0 && (string?)Get(synced[0]!, "FeedbackNotes") == "Android live sync")
                    {
                        var group = ((IList)Get(((IList)Get(synced[0]!, "Exercises")!)[0]!, "Sets")!)[1]!;
                        Require((string?)Get(group, "MovementQuality") == "compensated" && (string?)Get(group, "DiscomfortNotes") == "测试：右肩拉扯", "Live Android feedback received by Windows");
                        Set(synced[0]!, "Effort", "Windows 回传补填");
                        Call(window, "SaveWorkoutFeedback", synced[0]!);
                        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "tools", "fitness-regression", "live-sync-windows.json"), JsonSerializer.Serialize(synced, ListType, Options));
                        supplemented = true;
                    }
                };
                ready.Start();
            }
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            app.Shutdown();
        }
        else app.Shutdown();
    }

    private static IList Read(string json) => (IList)JsonSerializer.Deserialize(json, ListType, Options)!;
    private static void RunSilverRegression(MainWindow window)
    {
        Call(window, "ApplyTheme", "DarkSouls", false);
        Require((string)WindowType.GetField("_themeChoice", Private)!.GetValue(window)! == "Silver", "Legacy default migrates to silver");
        var noteType = WindowType.GetNestedType("NoteRecord", BindingFlags.NonPublic)!;
        var notes = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(noteType))!;
        foreach (var title in new[] { "给未来的自己", "训练随记" })
        {
            var note = Activator.CreateInstance(noteType)!;
            Set(note, "Id", Guid.NewGuid().ToString("N")); Set(note, "Title", title);
            Set(note, "CreatedAt", DateTimeOffset.Now); Set(note, "UpdatedAt", DateTimeOffset.Now);
            Set(note, "ContentXaml", "<FlowDocument xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Paragraph><Run Foreground=\"#FFFFFF\">银白界面 · 隔离测试记录</Run></Paragraph><Paragraph>☐ 保留排版与待办</Paragraph></FlowDocument>");
            Set(note, "Preview", "银白界面 · 隔离测试记录"); notes.Add(note);
        }
        WindowType.GetField("_notes", Private)!.SetValue(window, notes);
        Call(window, "HideViews"); Call(window, "OpenNote", notes[0]!);
        var editor = (RichTextBox)window.FindName("NoteEditor");
        Require(((FrameworkElement)window.FindName("NotesView")).Visibility == Visibility.Visible && ((FrameworkElement)window.FindName("EditorView")).Visibility == Visibility.Visible, "Library remains visible beside editor");
        Require(((SolidColorBrush)editor.CaretBrush).Color == Color.FromRgb(38, 42, 51), "Dark caret on white canvas");
        var run = ((Paragraph)editor.Document.Blocks.FirstBlock).Inlines.FirstInline!;
        Require(((SolidColorBrush)run.Foreground).Color == Color.FromRgb(38, 42, 51), "Legacy white text remains readable");
        Call(window, "ApplyTheme", "Japanese", false); Call(window, "ApplyTheme", "Silver", false);
        Require(((SolidColorBrush)run.Foreground).Color == Color.FromRgb(38, 42, 51), "Document follows runtime theme changes");
        Call(window, "IncreaseNoteFontSizeButton_Click", window, new RoutedEventArgs());
        Call(window, "SaveCurrentNote");
        Require((int)Get(notes[0]!, "FontSize")! == 19, "Typography persists in silver editor");
        var root = (FrameworkElement)window.Content;
        foreach (var size in new[] { new Size(1240, 800), new Size(980, 680) })
        {
            root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
            Call(window, "ApplyThemeToVisualTree"); root.UpdateLayout();
            Require(editor.ActualWidth >= 470 && editor.ActualHeight > 300, "Readable editor at width " + size.Width);
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(Environment.CurrentDirectory, "tools", "fitness-regression", "silver-windows-" + size.Width + ".png"));
            encoder.Save(output);
        }
        Call(window, "OpenNoteCard_Click", new Button { Tag = (string)Get(notes[1]!, "Id")! }, new RoutedEventArgs());
        Require(new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Contains("隔离测试记录"), "Switching notes preserves content");
        Call(window, "DeleteNoteButton_Click", new Button { Tag = (string)Get(notes[1]!, "Id")! }, new RoutedEventArgs());
        Require(((FrameworkElement)window.FindName("EditorView")).Visibility == Visibility.Collapsed && ((FrameworkElement)window.FindName("NotesEmptyView")).Visibility == Visibility.Visible, "Deleting selection clears stale editor");
        Call(window, "HideViews");
        Require(((FrameworkElement)window.FindName("NotesEmptyView")).Visibility == Visibility.Collapsed, "Empty workspace does not cover other pages");
        Console.WriteLine("PASS: silver layout regression; synthetic data only");
    }
    private static object? Get(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static object? Call(MainWindow window, string name, params object[] args) => WindowType.GetMethod(name, Private)!.Invoke(window, args);
    private static void Require(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS: " + label); }
    private static bool FindText(DependencyObject view, string value)
    {
        if (view is TextBlock text && text.Text.Contains(value)) return true;
        foreach (var child in LogicalTreeHelper.GetChildren(view).OfType<DependencyObject>()) if (FindText(child, value)) return true;
        return false;
    }
}
