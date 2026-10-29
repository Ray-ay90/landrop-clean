using Microsoft.Win32;
using QRCoder;
using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Line = System.Windows.Shapes.Line;

namespace LanDrop.Windows;

public partial class MainWindow : Window
{
    private const int DiscoveryPort = 41820;
    private const int PreferredPort = 41821;
    private const long MaxPayloadBytes = 512L * 1024 * 1024;
    private const int DefaultNoteFontSize = 18;
    private const double DefaultNoteLineSpacing = 1.3;
    private const double DefaultNoteLetterSpacing = 0.02;
    private readonly string _token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
    private string _deviceId = $"windows-{Guid.NewGuid():N}";
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "data", "settings.json");
    private readonly string _notesPath = Path.Combine(AppContext.BaseDirectory, "data", "notes.json");
    private readonly string _tasksPath = Path.Combine(AppContext.BaseDirectory, "data", "tasks.json");
    private readonly string _recycleBinNotesPath = Path.Combine(AppContext.BaseDirectory, "data", "recycle-bin", "notes.json");
    private readonly string _noteImagesDirectory = Path.Combine(AppContext.BaseDirectory, "data", "images");
    private readonly string _mindMapsPath = Path.Combine(AppContext.BaseDirectory, "data", "mindmaps.json");
    private readonly string _recycleBinMindMapsPath = Path.Combine(AppContext.BaseDirectory, "data", "recycle-bin", "mindmaps.json");
    private string _receiveDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "局域投送");
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _pairingLock = new();
    private TcpListener? _listener;
    private UdpClient? _discoveryClient;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource _shutdown = new();
    private List<LocalEndpoint> _localEndpoints = [];
    private int _port = PreferredPort;
    private string _themeChoice = "Silver";
    private EdgeOverlaySettings _edgeOverlaySettings = new();
    private EdgeOverlayWindow? _edgeOverlayWindow;
    private bool _updatingEdgeOverlayControls = true;
    private List<NoteRecord> _notes = [];
    private List<NoteRecord> _deletedNotes = [];
    private NoteRecord? _currentNote;
    private List<WorkoutTask> _tasks = [];
    private WorkoutTask? _currentTask;
    private bool _completedTasksExpanded;
    private Image? _selectedImage;
    private Border? _selectedImageFrame;
    private bool _resizingImage;
    private Point _imageResizeStart;
    private double _imageResizeStartWidth;
    private bool _imageResizeUsesY;
    private int _imageResizeSign = 1;
    private bool _loadingNote;
    private List<MindMapRecord> _mindMaps = [];
    private List<MindMapRecord> _deletedMindMaps = [];
    private MindMapRecord? _currentMindMap;
    private MindMapNode? _selectedMindMapNode;
    private readonly Stack<string> _mindMapUndo = new();
    private Border? _draggingMindMapNode;
    private Point _mindMapDragStart;
    private Point _mindMapNodeStart;
    private Size _mindMapNodeSizeStart;
    private bool _resizingMindMapNode;
    private bool _mindMapResizeUsesY;
    private int _mindMapResizeSign = 1;
    private bool _panningMindMap;
    private Point _mindMapPanStart;
    private Point _mindMapTranslateStart;
    private bool _resizingMindMapCanvas;
    private bool _mindMapCanvasResizeUsesY;
    private int _mindMapCanvasResizeSign;
    private Point _mindMapCanvasResizeStart;
    private Size _mindMapCanvasSizeStart;
    private Thickness _mindMapCanvasMarginStart;
    private Dictionary<string, Point> _mindMapCanvasNodesStart = [];
    private bool _mindMapFrameTransformApplied;
    private Dictionary<string, string> _trustedDeviceKeys = [];
    private readonly Dictionary<string, List<PendingDiscovery>> _pendingDiscoveries = [];

    public MainWindow()
    {
        InitializeComponent();
        ConfigureThemeSelector();
        HideSidebarBranding();
        ConfigureTitleBar();
        LoadSettings();
        RefreshCrosshairControls();
        RefreshEdgeOverlayControls();
        ApplyTheme(_themeChoice, false);
        LoadNotes();
        LoadDeletedNotes();
        LoadTasks();
        LoadMindMaps();
        LoadDeletedMindMaps();
        Directory.CreateDirectory(_receiveDirectory);
        ReceiveDirectoryBox.Text = _receiveDirectory;
        Loaded += (_, _) =>
        {
            ApplyThemeToVisualTree();
            ApplyEdgeOverlaySettings();
            ApplyCrosshairSettings();
            _ = StartServerAsync();
        };
        Closed += (_, _) => StopEverything();
    }

    private async Task StartServerAsync()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, PreferredPort);
            try
            {
                _listener.Start();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                AppLog.Info($"Preferred port {PreferredPort} is already in use; falling back to a random port.");
                _listener.Stop();
                _listener = new TcpListener(IPAddress.Any, 0);
                _listener.Start();
            }

            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _localEndpoints = GetLocalAddresses(_port);
            AddressComboBox.ItemsSource = _localEndpoints;
            AddressComboBox.DisplayMemberPath = nameof(LocalEndpoint.DisplayName);
            AddressComboBox.SelectedIndex = 0;
            UpdateQr();
            _ = RunDiscoveryAsync(_shutdown.Token);

            SetStatus("等待手机扫码或自动连接", false);
            var portNote = _port == PreferredPort ? "" : "（默认端口被占用，已自动切换）";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 接收服务已启动：{_localEndpoints[0].Address}:{_port}{portNote}");

            while (!_shutdown.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                _ = HandleClientAsync(client, _shutdown.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("Receive service failed to start.", ex);
            SetStatus("接收服务启动失败", false, true);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 错误：{ex.Message}");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var hello = await ReadFrameHeaderAsync(stream, cancellationToken);
            if (hello.Type != "hello" || string.IsNullOrWhiteSpace(hello.DeviceId))
                throw new InvalidDataException("设备身份无效");

            string? pairKeyForReply = null;
            var savePairing = false;
            var authenticated = false;
            lock (_pairingLock)
            {
                if (CryptographicEquals(hello.Token, _token))
                {
                    pairKeyForReply = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                    _trustedDeviceKeys[hello.DeviceId] = pairKeyForReply;
                    _pendingDiscoveries.Remove(hello.DeviceId);
                    savePairing = true;
                    authenticated = true;
                }
                else if (_trustedDeviceKeys.TryGetValue(hello.DeviceId, out var pairKey) &&
                         _pendingDiscoveries.TryGetValue(hello.DeviceId, out var pendingDiscoveries))
                {
                    var now = DateTimeOffset.UtcNow;
                    pendingDiscoveries.RemoveAll(pending => pending.ExpiresAt < now);
                    var pending = pendingDiscoveries.FirstOrDefault(candidate =>
                        CryptographicEquals(hello.Token, ComputeDiscoveryProof(pairKey, candidate.Nonce)));
                    if (pending is not null)
                    {
                        pendingDiscoveries.Remove(pending);
                        authenticated = true;
                        pairKeyForReply = ComputeDiscoveryProof(pairKey, pending.Nonce + ":server");
                    }
                    if (pendingDiscoveries.Count == 0) _pendingDiscoveries.Remove(hello.DeviceId);
                }
            }
            if (!authenticated) throw new InvalidDataException("配对令牌无效");
            if (savePairing) SaveSettings();

            var oldClient = _client;
            _client = client;
            _stream = stream;
            oldClient?.Dispose();

            await SendFrameAsync(new FrameHeader("hello_ack", pairKeyForReply, null, 0, Environment.MachineName, _deviceId), null, cancellationToken);
            AppLog.Info($"Authenticated device connection established. DeviceId={hello.DeviceId}; DeviceName={hello.DeviceName}.");
            Dispatcher.Invoke(() =>
            {
                PeerText.Text = $"已连接：{hello.DeviceName ?? "安卓设备"}";
                SendTextButton.IsEnabled = true;
                SendFileButton.IsEnabled = true;
                SyncNotesStatusText.Text = "等待 Android 发起同步全部";
                ConnectionSigilButton.Content = "已连接";
                OpenDeviceView();
            });
            SetStatus("设备已连接", true);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] {hello.DeviceName ?? "安卓设备"} 已连接");

            while (!cancellationToken.IsCancellationRequested && client == _client)
            {
                var frame = await ReadFrameHeaderAsync(stream, cancellationToken);
                switch (frame.Type)
                {
                    case "text":
                        AppendLog($"[{DateTime.Now:HH:mm:ss}] 手机：{frame.Message}");
                        break;
                    case "file":
                        await ReceiveFileAsync(stream, frame, cancellationToken);
                        break;
                    case "notes_sync":
                        await ReceiveNotesSyncAsync(stream, frame, cancellationToken);
                        await SendNotesSnapshotAsync("notes_sync_reply", cancellationToken, false);
                        break;
                    case "notes_sync_reply":
                        await ReceiveNotesSyncAsync(stream, frame, cancellationToken);
                        break;
                    case "sync_all":
                        await ReceiveAllSyncAsync(stream, frame, cancellationToken);
                        await SendAllSnapshotAsync("sync_all_reply", cancellationToken);
                        break;
                    case "sync_all_reply":
                        await ReceiveAllSyncAsync(stream, frame, cancellationToken);
                        break;
                    case "ping":
                        await SendFrameAsync(new FrameHeader("pong", null, null, 0, null, _deviceId), null, cancellationToken);
                        break;
                    default:
                        throw new InvalidDataException($"未知数据类型：{frame.Type}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("Client connection ended with an error.", ex);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 连接结束：{ex.Message}");
        }
        finally
        {
            if (client == _client)
            {
                _stream = null;
                _client = null;
                Dispatcher.Invoke(() =>
                {
                    PeerText.Text = "尚未连接设备";
                    SendTextButton.IsEnabled = false;
                    SendFileButton.IsEnabled = false;
                    SyncNotesStatusText.Text = "等待 Android 重新连接";
                });
                SetStatus("等待手机重新连接", false);
            }
            client.Dispose();
        }
    }

    private async Task RunDiscoveryAsync(CancellationToken cancellationToken)
    {
        try
        {
            _discoveryClient = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await _discoveryClient.ReceiveAsync(cancellationToken);
                DiscoveryPacket? request;
                try
                {
                    request = JsonSerializer.Deserialize<DiscoveryPacket>(received.Buffer, JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (request?.Type != "landrop_discover" || string.IsNullOrWhiteSpace(request.DeviceId)) continue;

                string nonce;
                lock (_pairingLock)
                {
                    if (!_trustedDeviceKeys.ContainsKey(request.DeviceId)) continue;
                    nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                    if (!_pendingDiscoveries.TryGetValue(request.DeviceId, out var pendingDiscoveries))
                    {
                        pendingDiscoveries = [];
                        _pendingDiscoveries[request.DeviceId] = pendingDiscoveries;
                    }
                    var now = DateTimeOffset.UtcNow;
                    pendingDiscoveries.RemoveAll(pending => pending.ExpiresAt < now);
                    if (pendingDiscoveries.Count >= 8) pendingDiscoveries.RemoveAt(0);
                    pendingDiscoveries.Add(new PendingDiscovery(nonce, now.AddSeconds(20)));
                }
                var response = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryPacket(
                    "landrop_offer", _deviceId, Environment.MachineName, _port, nonce), JsonOptions);
                await _discoveryClient.SendAsync(response, received.RemoteEndPoint, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("Automatic device discovery stopped with an error.", ex);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 自动连接服务不可用：{ex.Message}");
        }
    }

    private async Task ReceiveFileAsync(NetworkStream stream, FrameHeader frame, CancellationToken cancellationToken)
    {
        if (frame.PayloadLength < 0 || frame.PayloadLength > MaxPayloadBytes)
            throw new InvalidDataException("文件大小超出限制");

        var safeName = Path.GetFileName(string.IsNullOrWhiteSpace(frame.Name) ? "未命名文件" : frame.Name);
        var receiveDirectory = _receiveDirectory;
        Directory.CreateDirectory(receiveDirectory);
        var destination = GetUniquePath(receiveDirectory, safeName);
        var temporary = destination + ".part";
        try
        {
            UpdateReceiveProgress(safeName, 0, frame.PayloadLength, "正在接收");
            await using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await CopyFileWithProgressAsync(stream, file, safeName, frame.PayloadLength, cancellationToken);
            file.Close();
            File.Move(temporary, destination);
            UpdateReceiveProgress(Path.GetFileName(destination), frame.PayloadLength, frame.PayloadLength, "接收完成");
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 已接收文件：{Path.GetFileName(destination)}（{FormatBytes(frame.PayloadLength)}）");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to receive file '{safeName}'.", ex);
            if (File.Exists(temporary)) File.Delete(temporary);
            UpdateReceiveProgress(safeName, 0, frame.PayloadLength, "接收失败");
            throw;
        }
    }

    private async void SendTextButton_Click(object sender, RoutedEventArgs e)
    {
        var text = MessageBox.Text.Trim();
        if (text.Length == 0) return;
        try
        {
            await SendFrameAsync(new FrameHeader("text", null, null, 0, text, _deviceId), null, _shutdown.Token);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 电脑：{text}");
            MessageBox.Clear();
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to send text.", ex);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 发送失败：{ex.Message}");
        }
    }

    private async void SendFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择要发送到手机的文件" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await using var file = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await SendFrameAsync(new FrameHeader("file", null, Path.GetFileName(dialog.FileName), file.Length, null, _deviceId), file, _shutdown.Token);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 已发送文件：{Path.GetFileName(dialog.FileName)}（{FormatBytes(file.Length)}）");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to send file '{dialog.FileName}'.", ex);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 发送失败：{ex.Message}");
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_receiveDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _receiveDirectory) { UseShellExecute = true });
    }

    private async void SyncNotesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveCurrentNote();
            await SendNotesSnapshotAsync("notes_sync", _shutdown.Token, true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to sync notes.", ex);
            SyncNotesStatusText.Text = "同步失败，请检查设备连接";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 文本同步失败：{ex.Message}");
        }
    }

    private async Task SendNotesSnapshotAsync(string type, CancellationToken cancellationToken, bool reportToUser)
    {
        if (_client is not { Connected: true }) throw new IOException("设备尚未连接");

        var records = _notes.Select(note => new SyncNote(
            note.Id,
            note.Title,
            GetFullDocumentText(ParseDocument(note.ContentXaml)),
            note.UpdatedAt.ToUnixTimeMilliseconds(),
            note.FontSize,
            note.LineSpacing,
            note.LetterSpacing)).ToList();
        var payload = JsonSerializer.SerializeToUtf8Bytes(records, JsonOptions);
        if (payload.Length > 16 * 1024 * 1024) throw new InvalidDataException("文本记录过多，无法同步");

        await using var stream = new MemoryStream(payload, writable: false);
        await SendFrameAsync(new FrameHeader(type, null, "notes.json", payload.Length, null, _deviceId), stream, cancellationToken);
        if (reportToUser)
        {
            SyncNotesStatusText.Text = $"已发送 {records.Count} 条记录，等待手机合并";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 已发送 {records.Count} 条文本记录用于同步");
        }
    }

    private async Task ReceiveNotesSyncAsync(Stream stream, FrameHeader frame, CancellationToken cancellationToken)
    {
        if (frame.PayloadLength < 0 || frame.PayloadLength > 16L * 1024 * 1024)
            throw new InvalidDataException("文本同步数据大小无效");
        var payload = new byte[(int)frame.PayloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        var incoming = JsonSerializer.Deserialize<List<SyncNote>>(payload, JsonOptions)
            ?? throw new InvalidDataException("无法读取手机端文本记录");

        await Dispatcher.InvokeAsync(() =>
        {
            var changed = 0;
            foreach (var item in incoming.Where(item => !string.IsNullOrWhiteSpace(item.Id)))
            {
                var content = item.Content ?? string.Empty;
                var existing = _notes.FirstOrDefault(note => note.Id == item.Id);
                if (existing is not null && existing.UpdatedAt.ToUnixTimeMilliseconds() > item.UpdatedAt) continue;

                if (existing is null)
                {
                    existing = new NoteRecord { Id = item.Id, CreatedAt = DateTimeOffset.Now };
                    _notes.Add(existing);
                }
                existing.Title = string.IsNullOrWhiteSpace(item.Title) ? SyncTitle(content) : item.Title.Trim();
                existing.ContentXaml = PlainDocumentXaml(content);
                existing.Preview = content.Trim();
                ApplyIncomingTypography(existing, item);
                existing.UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, item.UpdatedAt));
                changed++;
            }
            if (changed > 0)
            {
                SaveNotes();
                RefreshNoteCards();
            }
            SyncNotesStatusText.Text = changed == 0 ? "两端文本记录已是最新" : $"已合并手机端 {changed} 条记录";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 文本同步完成：合并 {changed} 条手机记录");
        });
    }

    private async Task SendAllSnapshotAsync(string type, CancellationToken cancellationToken)
    {
        if (_client is not { Connected: true }) throw new IOException("设备尚未连接");
        SaveCurrentNote();
        var notes = _notes.Select(note => new SyncNote(
            note.Id,
            note.Title,
            GetFullDocumentText(ParseDocument(note.ContentXaml)),
            note.UpdatedAt.ToUnixTimeMilliseconds(),
            note.FontSize,
            note.LineSpacing,
            note.LetterSpacing)).ToList();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new SyncEnvelope(notes, _tasks), JsonOptions);
        if (payload.Length > 32 * 1024 * 1024) throw new InvalidDataException("同步数据过多");
        await using var stream = new MemoryStream(payload, writable: false);
        await SendFrameAsync(new FrameHeader(type, null, "sync-all.json", payload.Length, null, _deviceId), stream, cancellationToken);
    }

    private async Task ReceiveAllSyncAsync(Stream stream, FrameHeader frame, CancellationToken cancellationToken)
    {
        if (frame.PayloadLength < 0 || frame.PayloadLength > 32L * 1024 * 1024)
            throw new InvalidDataException("同步数据大小无效");
        var payload = new byte[(int)frame.PayloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        var incoming = JsonSerializer.Deserialize<SyncEnvelope>(payload, JsonOptions)
            ?? throw new InvalidDataException("无法读取 Android 同步数据");

        await Dispatcher.InvokeAsync(() =>
        {
            LoadTasks();
            var noteChanges = MergeNotes(incoming.Notes ?? []);
            var taskChanges = MergeTasks(incoming.Tasks ?? []);
            if (noteChanges > 0)
            {
                SaveNotes();
                RefreshNoteCards();
            }
            if (taskChanges > 0)
            {
                SaveTasks();
                RefreshTaskCards();
            }
            if (TaskDetailView.Visibility == Visibility.Visible && _currentTask is not null)
            {
                var current = _tasks.FirstOrDefault(task => task.Id == _currentTask.Id);
                if (current is not null && current.Status is not "deleted" and not "purged") RenderTaskDetail(current);
                else BackToTasksButton_Click(this, new RoutedEventArgs());
            }
            SyncNotesStatusText.Text = $"同步完成：文本 {noteChanges} 条，任务 {taskChanges} 项";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] Android 同步全部完成：文本 {noteChanges} 条，任务 {taskChanges} 项");
        });
    }

    private int MergeNotes(IEnumerable<SyncNote> incoming)
    {
        var changed = 0;
        foreach (var item in incoming.Where(item => !string.IsNullOrWhiteSpace(item.Id)))
        {
            var content = item.Content ?? string.Empty;
            var existing = _notes.FirstOrDefault(note => note.Id == item.Id);
            if (existing is not null && existing.UpdatedAt.ToUnixTimeMilliseconds() > item.UpdatedAt) continue;
            if (existing is null)
            {
                existing = new NoteRecord { Id = item.Id, CreatedAt = DateTimeOffset.Now };
                _notes.Add(existing);
            }
            existing.Title = string.IsNullOrWhiteSpace(item.Title) ? SyncTitle(content) : item.Title.Trim();
            existing.ContentXaml = PlainDocumentXaml(content);
            existing.Preview = content.Trim();
            ApplyIncomingTypography(existing, item);
            existing.UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, item.UpdatedAt));
            changed++;
        }
        return changed;
    }

    private int MergeTasks(IEnumerable<WorkoutTask> incoming)
    {
        var changed = 0;
        foreach (var task in incoming.Where(task => !string.IsNullOrWhiteSpace(task.Id)))
        {
            NormalizeTask(task);
            var existing = _tasks.FirstOrDefault(item => item.Id == task.Id);
            if (existing is not null && existing.UpdatedAt > task.UpdatedAt) continue;
            if (existing is null) _tasks.Add(task);
            else _tasks[_tasks.IndexOf(existing)] = task;
            changed++;
        }
        return changed;
    }

    private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择文件接收目录",
            InitialDirectory = Directory.Exists(_receiveDirectory) ? _receiveDirectory : null
        };
        if (dialog.ShowDialog(this) == true)
            ApplyReceiveDirectory(dialog.FolderName);
    }

    private void ReceiveDirectoryBox_LostFocus(object sender, RoutedEventArgs e) =>
        ApplyReceiveDirectory(ReceiveDirectoryBox.Text);

    private void ReceiveDirectoryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyReceiveDirectory(ReceiveDirectoryBox.Text);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void ApplyReceiveDirectory(string value)
    {
        string fullPath;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(expanded)) throw new InvalidDataException("目录不能为空");
            fullPath = Path.GetFullPath(expanded);
            Directory.CreateDirectory(fullPath);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Invalid receive directory: {value}", ex);
            ReceiveDirectoryBox.Text = _receiveDirectory;
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 无法使用该目录：{ex.Message}");
            return;
        }

        if (string.Equals(fullPath, _receiveDirectory, StringComparison.OrdinalIgnoreCase))
        {
            ReceiveDirectoryBox.Text = fullPath;
            return;
        }

        _receiveDirectory = fullPath;
        ReceiveDirectoryBox.Text = fullPath;
        AppendLog($"[{DateTime.Now:HH:mm:ss}] 接收目录已改为：{fullPath}");
        try
        {
            SaveSettings();
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to save receive directory setting.", ex);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 目录本次有效，但未能保存设置：{ex.Message}");
        }
    }

    private void HomeView_MouseMove(object sender, MouseEventArgs e)
    {
        // 保留事件入口，避免旧版本的视差背景影响新的信息面板布局。
    }

    private void HomeView_MouseLeave(object sender, MouseEventArgs e)
    {
    }

    private void OpenNotesButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentNote();
        HideViews();
        NotesView.Visibility = Visibility.Visible;
        SetActiveNavigation(ArchiveButton);
        var note = _notes.FirstOrDefault(item => item.Id == _currentNote?.Id) ?? _notes.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
        if (note is not null) OpenNote(note);
        else { NotesEmptyView.Visibility = Visibility.Visible; RefreshNoteCards(); }
    }

    private void OpenTrashButton_Click(object sender, RoutedEventArgs e)
    {
        HideViews();
        TrashView.Visibility = Visibility.Visible;
        SetActiveNavigation(TrashNavButton);
        RefreshTrashCards();
    }

    private void OpenTasksButton_Click(object sender, RoutedEventArgs e)
    {
        LoadTasks();
        HideViews();
        TasksView.Visibility = Visibility.Visible;
        SetActiveNavigation(MindMapNavButton);
        RefreshTaskCards();
    }

    private void RefreshTasksButton_Click(object sender, RoutedEventArgs e)
    {
        LoadTasks();
        RefreshTaskCards();
    }

    private void BackToTasksButton_Click(object sender, RoutedEventArgs e)
    {
        HideViews();
        TasksView.Visibility = Visibility.Visible;
        SetActiveNavigation(MindMapNavButton);
        RefreshTaskCards();
    }

    private void RefreshTaskCards()
    {
        TaskCardsPanel.Children.Clear();
        CompletedTaskCardsPanel.Children.Clear();
        var visibleTasks = _tasks.Where(task => task.Status is not "deleted" and not "purged").ToList();
        var active = visibleTasks.Count(task => task.Status is not "completed" and not "archived");
        var completed = visibleTasks.Count - active;
        TaskSummaryText.Text = visibleTasks.Count == 0
            ? "尚无任务。确认训练计划后会显示在这里。"
            : $"进行中 {active} 项    已完成 {completed} 项    同步由 Android 发起";

        foreach (var task in visibleTasks.Where(task => task.Status is not "completed" and not "archived").OrderByDescending(task => task.UpdatedAt))
        {
            NormalizeTask(task);
            var total = TotalSets(task);
            var done = CompletedSets(task);
            var card = new Button
            {
                Style = (Style)FindResource("ActionCard"),
                Tag = task.Id,
                MinHeight = 132,
                Padding = new Thickness(18),
                Margin = new Thickness(0, 0, 0, 12),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Background = (Brush)FindResource("SurfaceBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1)
            };
            var status = task.Status is "completed" or "archived" ? "委托完成" : done > 0 ? "进行中" : "等待执行";
            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            var initial = string.IsNullOrWhiteSpace(task.Title) ? "训" : task.Title.Trim()[..1];
            var mark = new Border
            {
                Width = 48, Height = 48, CornerRadius = new CornerRadius(9),
                Background = (Brush)FindResource("SurfaceElevatedBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = initial, Foreground = (Brush)FindResource("AccentBrush"), FontSize = 18,
                    FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            layout.Children.Add(mark);
            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 18, 0) };
            details.Children.Add(new TextBlock { Text = task.Title, Foreground = (Brush)FindResource("PrimaryTextBrush"), FontSize = 19, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            details.Children.Add(new TextBlock { Text = $"预计 {task.EstimatedMinutes} 分钟    {total} 组", Foreground = (Brush)FindResource("MutedTextBrush"), FontSize = 12, Margin = new Thickness(0, 7, 0, 0) });
            var exercises = string.Join("、", task.Exercises.Take(3).Select(item => item.Name));
            details.Children.Add(new TextBlock { Text = exercises, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)FindResource("SecondaryTextBrush"), FontSize = 12, Margin = new Thickness(0, 9, 0, 0) });
            Grid.SetColumn(details, 1);
            layout.Children.Add(details);
            var progressPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 10, 0) };
            var statusTag = new Border
            {
                Background = (Brush)FindResource("SurfaceElevatedBrush"), BorderBrush = (Brush)FindResource("StrongBorderBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 5, 10, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = new TextBlock { Text = status, Foreground = (Brush)FindResource("AccentBrush"), FontSize = 11, FontWeight = FontWeights.SemiBold }
            };
            progressPanel.Children.Add(statusTag);
            var progressHeader = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            progressHeader.Children.Add(new TextBlock { Text = "完成进度", Foreground = (Brush)FindResource("MutedTextBrush"), FontSize = 11, VerticalAlignment = VerticalAlignment.Bottom });
            var progressText = new TextBlock { Text = $"{done}/{total} 组", Foreground = (Brush)FindResource("PrimaryTextBrush"), FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
            progressHeader.Children.Add(progressText);
            progressPanel.Children.Add(progressHeader);
            var progress = new ProgressBar { Minimum = 0, Maximum = Math.Max(1, total), Value = done, Height = 5, Margin = new Thickness(0, 18, 0, 0), Style = (Style)FindResource("DarkProgress") };
            progress.Margin = new Thickness(0, 8, 0, 0);
            progressPanel.Children.Add(progress);
            Grid.SetColumn(progressPanel, 2);
            layout.Children.Add(progressPanel);
            var arrow = new TextBlock { Text = "›", Foreground = (Brush)FindResource("SecondaryTextBrush"), FontSize = 26, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(arrow, 3);
            layout.Children.Add(arrow);
            card.Content = layout;
            card.Click += OpenTaskCard_Click;
            card.ContextMenu = CreateTaskContextMenu(task.Id);
            TaskCardsPanel.Children.Add(card);
        }

        CompletedTasksToggleButton.Content = $"已完成事项 · {completed} 项{(completed > 0 ? _completedTasksExpanded ? "  ⌃" : "  ⌄" : "")}";
        CompletedTasksToggleButton.IsEnabled = completed > 0;
        CompletedTaskCardsPanel.Visibility = completed > 0 && _completedTasksExpanded ? Visibility.Visible : Visibility.Collapsed;

        foreach (var task in visibleTasks.Where(task => task.Status is "completed" or "archived")
                     .OrderByDescending(task => task.CompletedAt > 0 ? task.CompletedAt : task.UpdatedAt))
        {
            NormalizeTask(task);
            var total = TotalSets(task);
            var done = CompletedSets(task);
            var finishedAt = task.CompletedAt > 0 ? task.CompletedAt : task.UpdatedAt;
            var date = DateTimeOffset.FromUnixTimeMilliseconds(finishedAt).ToLocalTime().ToString("yyyy-MM-dd");
            var card = new Button
            {
                Style = (Style)FindResource("ActionCard"),
                Tag = task.Id,
                Height = 72,
                Padding = new Thickness(16, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = (Brush)FindResource("SurfaceBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1)
            };
            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = task.Title, Foreground = (Brush)FindResource("PrimaryTextBrush"), FontSize = 14,
                FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
            });
            content.Children.Add(new TextBlock
            {
                Text = $"{date} · {done}/{total} 组", Foreground = (Brush)FindResource("MutedTextBrush"),
                FontSize = 11, Margin = new Thickness(0, 3, 0, 0)
            });
            layout.Children.Add(content);
            var action = new TextBlock
            {
                Text = "查看 ›", Foreground = (Brush)FindResource("SecondaryTextBrush"),
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
            };
            Grid.SetColumn(action, 1);
            layout.Children.Add(action);
            card.Content = layout;
            card.Click += OpenTaskCard_Click;
            card.ContextMenu = CreateTaskContextMenu(task.Id);
            CompletedTaskCardsPanel.Children.Add(card);
        }
    }

    private ContextMenu CreateTaskContextMenu(string taskId)
    {
        var menu = new ContextMenu();
        var deleteItem = new MenuItem { Header = "删除", Tag = taskId };
        deleteItem.Click += DeleteTaskButton_Click;
        menu.Items.Add(deleteItem);
        return menu;
    }

    private void CompletedTasksToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _completedTasksExpanded = !_completedTasksExpanded;
        var completed = _tasks.Count(task => task.Status is "completed" or "archived");
        CompletedTasksToggleButton.Content = $"已完成事项 · {completed} 项  {(_completedTasksExpanded ? "⌃" : "⌄")}";
        CompletedTaskCardsPanel.Visibility = _completedTasksExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenTaskCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var task = _tasks.FirstOrDefault(item => item.Id == id);
        if (task is null) return;
        _currentTask = task;
        RenderTaskDetail(task);
    }

    private void RenderTaskDetail(WorkoutTask task)
    {
        _currentTask = task;
        HideViews();
        TaskDetailView.Visibility = Visibility.Visible;
        SetActiveNavigation(MindMapNavButton);
        TaskDetailTitleText.Text = task.Title;
        var total = TotalSets(task);
        var done = CompletedSets(task);
        TaskDetailStatusText.Text = task.Status is "completed" or "archived"
            ? done == total ? "委托完成" : $"已结算 · {done} / {total} 组"
            : $"进行中 · {done} / {total} 组";
        TaskDetailContentPanel.Children.Clear();

        var overview = new Border
        {
            Background = (Brush)FindResource("SurfaceBrush"), BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(22), Margin = new Thickness(0, 0, 0, 16)
        };
        var overviewContent = new StackPanel();
        overviewContent.Children.Add(new TextBlock { Text = $"委托 #{task.Id}", Foreground = (Brush)FindResource("MutedTextBrush"), FontSize = 12 });
        overviewContent.Children.Add(new TextBlock { Text = $"{done} / {total} 组", Foreground = (Brush)FindResource("PrimaryTextBrush"), FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        overviewContent.Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(1, total), Value = done, Height = 6, Margin = new Thickness(0, 12, 0, 0), Style = (Style)FindResource("DarkProgress") });
        if (!string.IsNullOrWhiteSpace(task.Notes)) overviewContent.Children.Add(new TextBlock { Text = task.Notes, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("SecondaryTextBrush"), Margin = new Thickness(0, 14, 0, 0) });
        overview.Child = overviewContent;
        TaskDetailContentPanel.Children.Add(overview);
        var overall = new StackPanel();
        overall.Children.Add(FeedbackText("整次训练反馈", 18));
        overall.Children.Add(FeedbackText($"整体费力：{task.Effort ?? "未记录"}\n不适：{DiscomfortText(task.Discomfort, task.DiscomfortNotes)}\n备注：{task.FeedbackNotes ?? "未记录"}"));
        overall.Children.Add(FeedbackButton("填写 / 修改整体反馈", () => EditTaskFeedback(task)));
        TaskDetailContentPanel.Children.Add(FeedbackCard(overall));

        foreach (var exercise in task.Exercises)
        {
            var card = new Border
            {
                Background = (Brush)FindResource("SurfaceBrush"), BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(22), Margin = new Thickness(0, 0, 0, 16)
            };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = exercise.Name + (exercise.IsAdditional ? " · 临时新增" : ""), Foreground = (Brush)FindResource("PrimaryTextBrush"), FontSize = 18, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = $"{exercise.Sets.Count} 组 · 休息 {exercise.RestSeconds} 秒", Foreground = (Brush)FindResource("MutedTextBrush"), FontSize = 12, Margin = new Thickness(0, 6, 0, 10) });
            foreach (var set in exercise.Sets.OrderBy(item => item.Index))
            {
                var weight = set.Completed ? set.ActualWeight : set.TargetWeight;
                var reps = set.Completed ? set.ActualReps : set.TargetReps;
                panel.Children.Add(new TextBlock
                {
                    Text = $"{(set.Completed ? "✓" : "□")}  第 {set.Index} 组     {weight:0.##} kg × {reps} 次",
                    Foreground = set.Completed ? (Brush)FindResource("MutedTextBrush") : (Brush)FindResource("PrimaryTextBrush"),
                    FontSize = 14,
                    Padding = new Thickness(0, 9, 0, 9)
                });
                panel.Children.Add(FeedbackText($"余力：{FeedbackLabel(set.Rir, RirValues, RirLabels)} · 动作：{FeedbackLabel(set.MovementQuality, QualityValues, QualityLabels)}\n不适：{DiscomfortText(set.Discomfort, set.DiscomfortNotes)}"));
                if (set.Completed) panel.Children.Add(FeedbackButton($"第 {set.Index} 组反馈", () => EditSetFeedback(task, exercise, set)));
            }
            var last = LastCompletedSet(exercise);
            if (last is not null) panel.Children.Add(FeedbackButton($"记录末组反馈 · 第 {last.Index} 组", () => EditSetFeedback(task, exercise, last)));
            panel.Children.Add(FeedbackText($"未做 / 少做原因：{exercise.SkipReason ?? "未记录"}"));
            panel.Children.Add(FeedbackButton("填写 / 修改原因", () => EditSkipReason(task, exercise)));
            if (!string.IsNullOrWhiteSpace(exercise.Notes)) panel.Children.Add(new TextBlock { Text = exercise.Notes, Foreground = (Brush)FindResource("MutedTextBrush"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
            card.Child = panel;
            TaskDetailContentPanel.Children.Add(card);
        }
        TaskDetailContentPanel.Children.Add(FeedbackButton("＋ 临时新增 / 补记动作", () => AddWorkoutExercise(task)));

        if (task.Status is "completed" or "archived")
        {
            var volume = task.Exercises.SelectMany(item => item.Sets).Where(item => item.Completed).Sum(item => item.ActualWeight * item.ActualReps);
            TaskDetailContentPanel.Children.Add(new TextBlock { Text = $"训练已结算 · 总训练容量 {volume:0.##} kg", Foreground = (Brush)FindResource("SecondaryTextBrush"), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 20) });
        }
    }

    private static readonly string?[] RirValues = [null, "0", "1", "2", "3", "4+", "uncertain"];
    private static readonly string[] RirLabels = ["未记录", "0 次", "1 次", "2 次", "3 次", "4 次以上", "不确定"];
    private static readonly string?[] QualityValues = [null, "stable", "compensated", "uncertain"];
    private static readonly string[] QualityLabels = ["未记录", "稳定", "后几次幅度缩小或借力", "动作不熟、不确定"];
    private static readonly string?[] DiscomfortValues = [null, "none", "present"];
    private static readonly string[] DiscomfortLabels = ["未记录", "无", "有"];

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FeedbackLabel(string? value, string?[] values, string[] labels)
    {
        var index = Array.IndexOf(values, value);
        return index >= 0 ? labels[index] : value ?? "未记录";
    }

    private static string DiscomfortText(string? value, string? notes) => FeedbackLabel(value, DiscomfortValues, DiscomfortLabels) + (notes is null ? "" : " · " + notes);

    private static WorkoutSet? LastCompletedSet(WorkoutExercise exercise) => exercise.Sets.Where(set => set.Completed).OrderByDescending(set => set.CompletedAt).ThenByDescending(set => set.Index).FirstOrDefault();

    private TextBlock FeedbackText(string value, double size = 13) => new()
    {
        Text = value, TextWrapping = TextWrapping.Wrap, FontSize = size,
        Foreground = (Brush)FindResource(size > 13 ? "PrimaryTextBrush" : "SecondaryTextBrush"), Margin = new Thickness(0, 8, 0, 8)
    };

    private Border FeedbackCard(StackPanel content) => new()
    {
        Child = content, Background = (Brush)FindResource("SurfaceBrush"), BorderBrush = (Brush)FindResource("BorderBrush"),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(22), Margin = new Thickness(0, 0, 0, 16)
    };

    private Button FeedbackButton(string title, Action click)
    {
        var button = new Button { Content = title, Style = (Style)FindResource("SecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 8, 6), Padding = new Thickness(14, 8, 14, 8) };
        button.Click += (_, _) => click();
        return button;
    }

    private TextBox FeedbackInput(StackPanel panel, string title, string? value)
    {
        panel.Children.Add(FeedbackText(title));
        var input = new TextBox { Text = value ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 40, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(input);
        return input;
    }

    private Button FeedbackChoice(StackPanel panel, string title, string? value, string?[] values, string[] labels)
    {
        var choice = FeedbackButton($"{title}：{FeedbackLabel(value, values, labels)}", () => { });
        choice.Tag = value;
        choice.HorizontalAlignment = HorizontalAlignment.Stretch;
        choice.HorizontalContentAlignment = HorizontalAlignment.Left;
        choice.Click += (_, _) =>
        {
            var menu = new ContextMenu { Background = (Brush)FindResource("SurfaceBrush"), Foreground = (Brush)FindResource("PrimaryTextBrush") };
            for (var i = 0; i < values.Length; i++)
            {
                var selected = i;
                var item = new MenuItem { Header = labels[i], Background = menu.Background, Foreground = menu.Foreground, Padding = new Thickness(12, 8, 12, 8) };
                item.Click += (_, _) => { choice.Tag = values[selected]; choice.Content = $"{title}：{labels[selected]}"; };
                menu.Items.Add(item);
            }
            menu.PlacementTarget = choice;
            menu.IsOpen = true;
        };
        panel.Children.Add(choice);
        return choice;
    }

    private void ShowFeedbackDialog(string title, StackPanel form, Func<bool> save, string saveTitle = "保存反馈")
    {
        var window = new Window
        {
            Owner = this, Title = title, Width = 540, Height = Math.Min(650, SystemParameters.WorkArea.Height - 70),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("SurfaceBrush"),
            Foreground = (Brush)FindResource("PrimaryTextBrush"), FontFamily = FontFamily, ResizeMode = ResizeMode.NoResize
        };
        var layout = new DockPanel { Margin = new Thickness(24) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = FeedbackButton("暂不填写", window.Close);
        var confirm = FeedbackButton(saveTitle, () => { if (save()) window.Close(); });
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);
        layout.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        window.Content = layout;
        window.ShowDialog();
    }

    private void SaveWorkoutFeedback(WorkoutTask task)
    {
        if (!_tasks.Contains(task))
        {
            System.Windows.MessageBox.Show(this, "任务已由同步更新，本次编辑未保存。请重新打开反馈后填写。", "任务已更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        task.UpdatedAt = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), task.UpdatedAt + 1);
        SaveTasks();
        RenderTaskDetail(task);
    }

    private void EditSetFeedback(WorkoutTask task, WorkoutExercise exercise, WorkoutSet set)
    {
        var form = new StackPanel();
        form.Children.Add(FeedbackText($"{exercise.Name} · 第 {set.Index} 组", 18));
        form.Children.Add(FeedbackText("评价固定关联这一组；未填写保留为空，可之后补填。"));
        var rir = FeedbackChoice(form, "还能规范做几次", set.Rir, RirValues, RirLabels);
        var quality = FeedbackChoice(form, "动作保持得怎么样", set.MovementQuality, QualityValues, QualityLabels);
        var discomfort = FeedbackChoice(form, "有没有不舒服", set.Discomfort, DiscomfortValues, DiscomfortLabels);
        var details = FeedbackInput(form, "不适部位和感受（选择“有”时填写）", set.DiscomfortNotes);
        var error = FeedbackText("");
        form.Children.Add(error);
        ShowFeedbackDialog("逐组反馈", form, () =>
        {
            if ((string?)discomfort.Tag == "present" && EmptyToNull(details.Text) is null) { error.Text = "请填写部位和感受，或暂不填写反馈。"; return false; }
            set.Rir = (string?)rir.Tag;
            set.MovementQuality = (string?)quality.Tag;
            set.Discomfort = (string?)discomfort.Tag;
            set.DiscomfortNotes = set.Discomfort == "present" ? EmptyToNull(details.Text) : null;
            SaveWorkoutFeedback(task);
            return true;
        });
    }

    private void EditTaskFeedback(WorkoutTask task)
    {
        var form = new StackPanel();
        form.Children.Add(FeedbackText("整次训练反馈", 18));
        form.Children.Add(FeedbackText("可全部或部分留空；原计划说明和结算状态保持不变。"));
        var effort = FeedbackInput(form, "整体费力程度（按自己的感受描述）", task.Effort);
        var discomfort = FeedbackChoice(form, "有没有不舒服", task.Discomfort, DiscomfortValues, DiscomfortLabels);
        var details = FeedbackInput(form, "不适部位和感受（选择“有”时填写）", task.DiscomfortNotes);
        var notes = FeedbackInput(form, "自由备注", task.FeedbackNotes);
        var error = FeedbackText("");
        form.Children.Add(error);
        ShowFeedbackDialog("整次训练反馈", form, () =>
        {
            if ((string?)discomfort.Tag == "present" && EmptyToNull(details.Text) is null) { error.Text = "请填写部位和感受，或暂不填写反馈。"; return false; }
            task.Effort = EmptyToNull(effort.Text);
            task.Discomfort = (string?)discomfort.Tag;
            task.DiscomfortNotes = task.Discomfort == "present" ? EmptyToNull(details.Text) : null;
            task.FeedbackNotes = EmptyToNull(notes.Text);
            SaveWorkoutFeedback(task);
            return true;
        });
    }

    private void EditSkipReason(WorkoutTask task, WorkoutExercise exercise)
    {
        var form = new StackPanel();
        form.Children.Add(FeedbackText(exercise.Name + " · 未做 / 少做原因", 18));
        form.Children.Add(FeedbackText("只记录原因，不自动勾选或取消任何组。"));
        var reason = FeedbackInput(form, "原因（可留空）", exercise.SkipReason);
        ShowFeedbackDialog("未做 / 少做原因", form, () => { exercise.SkipReason = EmptyToNull(reason.Text); SaveWorkoutFeedback(task); return true; }, "保存原因");
    }

    private void AddWorkoutExercise(WorkoutTask task)
    {
        var form = new StackPanel();
        form.Children.Add(FeedbackText("临时新增 / 补记动作", 18));
        form.Children.Add(FeedbackText("原计划保持不变；补记已完成动作时填写每组实际重量和次数。不同组数值可在 Android 逐组调整。"));
        var name = FeedbackInput(form, "动作名称", null);
        var count = FeedbackInput(form, "组数（1–100）", "4");
        var completed = new CheckBox { Content = "补记：这些组已经完成", Foreground = (Brush)FindResource("PrimaryTextBrush"), Margin = new Thickness(0, 12, 0, 0) };
        form.Children.Add(completed);
        var weight = FeedbackInput(form, "每组实际重量 / kg（徒手可填 0）", null);
        var reps = FeedbackInput(form, "每组实际次数", null);
        var error = FeedbackText("");
        form.Children.Add(error);
        ShowFeedbackDialog("临时新增动作", form, () =>
        {
            if (EmptyToNull(name.Text) is null || !int.TryParse(count.Text, out var number) || number < 1 || number > 100) { error.Text = "请填写名称和 1–100 的整数型组数。"; return false; }
            var done = completed.IsChecked == true;
            double actualWeight = 0;
            int actualReps = 0;
            if (done && (!double.TryParse(weight.Text, out actualWeight) || !double.IsFinite(actualWeight) || actualWeight < 0 || !int.TryParse(reps.Text, out actualReps) || actualReps < 1)) { error.Text = "补记已完成组需填写非负重量和正整数次数。"; return false; }
            var exercise = new WorkoutExercise { Id = Guid.NewGuid().ToString("N"), Name = name.Text.Trim(), IsAdditional = true };
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (var i = 1; i <= number; i++) exercise.Sets.Add(new WorkoutSet { Index = i, ActualWeight = actualWeight, ActualReps = actualReps, Completed = done, CompletedAt = done ? now : 0 });
            task.Exercises.Add(exercise);
            SaveWorkoutFeedback(task);
            return true;
        }, "添加动作");
    }

    private static int TotalSets(WorkoutTask task) => task.Exercises.Sum(exercise => exercise.Sets.Count);

    private static int CompletedSets(WorkoutTask task) => task.Exercises.Sum(exercise => exercise.Sets.Count(set => set.Completed));

    private void OpenMindMapButton_Click(object sender, RoutedEventArgs e)
    {
        HideViews();
        MindMapView.Visibility = Visibility.Visible;
        SetActiveNavigation(MindMapNavButton);
        RefreshMindMapCards();
    }

    private void NewMindMapButton_Click(object sender, RoutedEventArgs e)
    {
        var centerTopic = PromptForText("新建思维导图", "请输入中心主题", "中心主题");
        if (string.IsNullOrWhiteSpace(centerTopic)) return;
        var map = new MindMapRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = centerTopic.Trim(),
            UpdatedAt = DateTimeOffset.Now,
            Nodes = [new MindMapNode { Id = Guid.NewGuid().ToString("N"), Text = centerTopic.Trim(), X = 520, Y = 360, Color = "#313A44" }]
        };
        _mindMaps.Add(map);
        OpenMindMapEditor(map);
    }

    private void OpenMindMapCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var map = _mindMaps.FirstOrDefault(item => item.Id == id);
        if (map is not null) OpenMindMapEditor(map);
    }

    private void DeleteMindMapButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        var map = _mindMaps.FirstOrDefault(item => item.Id == id);
        if (map is null) return;
        _mindMaps.Remove(map);
        map.DeletedAt = DateTimeOffset.Now;
        _deletedMindMaps.Add(map);
        SaveMindMaps();
        SaveDeletedMindMaps();
        RefreshMindMapCards();
    }

    private void PermanentlyDeleteMindMapButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var map = _deletedMindMaps.FirstOrDefault(item => item.Id == id);
        if (map is null) return;
        _deletedMindMaps.Remove(map);
        SaveDeletedMindMaps();
        RefreshTrashCards();
    }

    private void RestoreMindMapButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var map = _deletedMindMaps.FirstOrDefault(item => item.Id == id);
        if (map is null) return;
        _deletedMindMaps.Remove(map);
        if (_mindMaps.Any(item => item.Id == map.Id)) map.Id = Guid.NewGuid().ToString("N");
        map.DeletedAt = null;
        map.UpdatedAt = DateTimeOffset.Now;
        _mindMaps.Add(map);
        SaveMindMaps();
        SaveDeletedMindMaps();
        RefreshMindMapCards();
        RefreshTrashCards();
        AppendLog($"[{DateTime.Now:HH:mm:ss}] 已恢复思维导图：{map.Title}");
    }

    private void OpenMindMapEditor(MindMapRecord map)
    {
        _currentMindMap = map;
        _selectedMindMapNode = null;
        _mindMapUndo.Clear();
        MindMapTitleBox.Text = map.Title;
        MindMapCanvas.Width = map.CanvasWidth;
        MindMapCanvas.Height = map.CanvasHeight;
        MindMapCanvasFrame.Margin = new Thickness(map.CanvasLeft, map.CanvasTop, 260, 260);
        MindMapScrollViewer.Background = new SolidColorBrush(Color.FromRgb(16, 17, 19));
        if (!_mindMapFrameTransformApplied)
        {
            MindMapCanvasFrame.RenderTransform = MindMapCanvas.RenderTransform;
            MindMapCanvas.RenderTransform = Transform.Identity;
            _mindMapFrameTransformApplied = true;
        }
        MindMapScaleTransform.ScaleX = MindMapScaleTransform.ScaleY = 1;
        MindMapTranslateTransform.X = MindMapTranslateTransform.Y = 0;
        MindMapCanvasFrame.BorderBrush = new SolidColorBrush(Color.FromRgb(90, 178, 255));
        MindMapCanvasFrame.BorderThickness = new Thickness(4);
        HideViews();
        MindMapEditorView.Visibility = Visibility.Visible;
        SetActiveNavigation(MindMapNavButton);
        RenderMindMap();
        MindMapCanvas.Focus();
    }

    private void RefreshMindMapCards()
    {
        MindMapCardsPanel.Children.Clear();
        var maps = _mindMaps.OrderByDescending(map => map.UpdatedAt).ToList();
        MindMapSummaryText.Text = maps.Count == 0 ? "尚无导图。新建第一张导图。" : $"已保存 {maps.Count} 张导图，全部仅保存在这台电脑。";
        foreach (var map in maps)
        {
            var card = new Button { Width = 270, Height = 150, Margin = new Thickness(0, 0, 16, 16), Tag = map.Id, Style = (Style)FindResource("ActionCard"), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Padding = new Thickness(16) };
            card.Click += OpenMindMapCard_Click;
            var menu = new ContextMenu();
            var delete = new MenuItem { Header = "删除", Tag = map.Id };
            delete.Click += DeleteMindMapButton_Click;
            menu.Items.Add(delete);
            card.ContextMenu = menu;
            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.Children.Add(new TextBlock { Text = map.Title, Foreground = Brushes.White, FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis });
            var count = new TextBlock { Text = $"{map.Nodes.Count} 个节点", Foreground = new SolidColorBrush(Color.FromRgb(154, 161, 165)), FontSize = 12, Margin = new Thickness(0, 12, 0, 8) };
            Grid.SetRow(count, 1); body.Children.Add(count);
            var date = new TextBlock { Text = map.UpdatedAt.LocalDateTime.ToString("yyyy.MM.dd  HH:mm"), Foreground = new SolidColorBrush(Color.FromRgb(116, 124, 129)), FontSize = 10 };
            Grid.SetRow(date, 2); body.Children.Add(date);
            card.Content = body;
            MindMapCardsPanel.Children.Add(card);
        }
    }

    private void MindMapTitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_currentMindMap is null || string.IsNullOrWhiteSpace(MindMapTitleBox.Text)) return;
        _currentMindMap.Title = MindMapTitleBox.Text.Trim();
        _currentMindMap.UpdatedAt = DateTimeOffset.Now;
        SaveMindMaps();
    }

    private void SaveMindMapButton_Click(object sender, RoutedEventArgs e)
    {
        SaveMindMaps();
    }

    private void BackToHomeFromNotesButton_Click(object sender, RoutedEventArgs e)
    {
        ShowHome();
    }

    private void OpenRecentTransfersButton_Click(object sender, RoutedEventArgs e)
    {
        ShowHome();
    }

    private void NewNoteButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentNote();
        var note = new NoteRecord { Id = Guid.NewGuid().ToString("N"), Title = "未命名记录", ContentXaml = EmptyDocumentXaml(), CreatedAt = DateTimeOffset.Now, UpdatedAt = DateTimeOffset.Now };
        _notes.Add(note);
        OpenNote(note);
    }

    private void OpenNoteCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var note = _notes.FirstOrDefault(item => item.Id == id);
        if (note is null) return;
        SaveCurrentNote();
        OpenNote(note);
    }

    private void DeleteNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        var note = _notes.FirstOrDefault(item => item.Id == id);
        if (note is null) return;

        var deletingCurrent = _currentNote?.Id == note.Id;
        if (deletingCurrent) { SaveCurrentNote(); _currentNote = null; }
        _notes.Remove(note);
        note.DeletedAt = DateTimeOffset.Now;
        _deletedNotes.Add(note);

        try
        {
            SaveNotes();
            SaveDeletedNotes();
            RefreshNoteCards();
            if (deletingCurrent) { EditorView.Visibility = Visibility.Collapsed; NotesEmptyView.Visibility = Visibility.Visible; }
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 记录已移入回收站：{note.Title}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to move note to recycle bin.", ex);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 删除记录失败：{ex.Message}");
        }
    }

    private void PermanentlyDeleteNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var note = _deletedNotes.FirstOrDefault(item => item.Id == id);
        if (note is null) return;

        _deletedNotes.Remove(note);
        try
        {
            SaveDeletedNotes();
            RefreshTrashCards();
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 已永久删除记录：{note.Title}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to permanently delete note.", ex);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 永久删除失败：{ex.Message}");
        }
    }

    private void RestoreNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var note = _deletedNotes.FirstOrDefault(item => item.Id == id);
        if (note is null) return;

        _deletedNotes.Remove(note);
        if (_notes.Any(item => item.Id == note.Id)) note.Id = Guid.NewGuid().ToString("N");
        note.DeletedAt = null;
        note.UpdatedAt = DateTimeOffset.Now;
        _notes.Add(note);
        try
        {
            SaveNotes();
            SaveDeletedNotes();
            RefreshNoteCards();
            RefreshTrashCards();
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 已恢复记录：{note.Title}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to restore note from recycle bin.", ex);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 恢复记录失败：{ex.Message}");
        }
    }

    private void RestoreDeletedTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var task = _tasks.FirstOrDefault(item => item.Id == id && item.Status == "deleted");
        if (task is null) return;
        task.Status = task.PreviousStatus is "pending" or "active" or "completed" or "archived"
            ? task.PreviousStatus
            : task.CompletedAt > 0 ? "completed" : CompletedSets(task) > 0 ? "active" : "pending";
        task.PreviousStatus = "";
        task.DeletedAt = 0;
        task.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SaveTasks();
        RefreshTaskCards();
        RefreshTrashCards();
        AppendLog($"[{DateTime.Now:HH:mm:ss}] 已恢复任务：{task.Title}");
    }

    private void DeleteTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        var task = _tasks.FirstOrDefault(item => item.Id == id && item.Status is not "deleted" and not "purged");
        if (task is null) return;
        task.PreviousStatus = task.Status;
        task.Status = "deleted";
        task.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        task.UpdatedAt = task.DeletedAt;
        if (_currentTask?.Id == task.Id) _currentTask = null;
        SaveTasks();
        RefreshTaskCards();
        AppendLog($"[{DateTime.Now:HH:mm:ss}] 任务已移入回收站：{task.Title}");
    }

    private void PermanentlyDeleteTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var task = _tasks.FirstOrDefault(item => item.Id == id && item.Status == "deleted");
        if (task is null) return;
        if (System.Windows.MessageBox.Show(
                this,
                $"永久删除后无法恢复。\n\n{task.Title}",
                "永久删除这个任务？",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var title = task.Title;
        var purgedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        task.Title = "已永久删除任务";
        task.SourceProject = "";
        task.Status = "purged";
        task.PreviousStatus = "";
        task.ScheduledDate = "";
        task.EstimatedMinutes = 0;
        task.Notes = "";
        task.Effort = task.Discomfort = task.DiscomfortNotes = task.FeedbackNotes = null;
        task.CreatedAt = 0;
        task.StartedAt = 0;
        task.CompletedAt = 0;
        task.DeletedAt = purgedAt;
        task.UpdatedAt = purgedAt;
        task.Exercises.Clear();
        SaveTasks();
        RefreshTaskCards();
        RefreshTrashCards();
        AppendLog($"[{DateTime.Now:HH:mm:ss}] 已永久删除任务：{title}");
    }

    private void OpenNote(NoteRecord note)
    {
        _currentNote = note;
        _selectedImage = null;
        _loadingNote = true;
        try
        {
            NoteTitleBox.Text = note.Title;
            NoteEditor.Document = ParseDocument(note.ContentXaml);
            ApplyNoteTextTheme();
            AttachInteractiveNoteElements(NoteEditor.Document);
            ApplyNoteTypography(note, false);
            ApplyChecklistStyles(NoteEditor.Document);
        }
        finally { _loadingNote = false; }
        NotesView.Visibility = Visibility.Visible;
        NotesEmptyView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Visible;
        RefreshNoteCards();
        SetActiveNavigation(ArchiveButton);
        NoteTitleBox.Focus();
        NoteTitleBox.SelectAll();
        UpdateNoteHistoryButtons();
    }

    private void SaveNoteButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentNote();
        RefreshNoteCards();
    }

    private void ApplyNoteTextTheme()
    {
        var wasLoading = _loadingNote;
        _loadingNote = true;
        try
        {
            NoteEditor.Document.SetResourceReference(TextElement.ForegroundProperty, "PrimaryTextBrush");
            foreach (var element in FindLogicalChildren<TextElement>(NoteEditor.Document))
            {
                if (element.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush brush &&
                    brush.Color.ToString() is "#FFFFFFFF" or "#FF000000" or "#FFF1F0EE" or "#FFE7E9EA" or "#FF262A33" or "#FF2A2420" or "#FF18242A")
                    element.ClearValue(TextElement.ForegroundProperty);
            }
            ApplyChecklistStyles(NoteEditor.Document);
        }
        finally { _loadingNote = wasLoading; }
    }

    private void NoteTitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingNote && _currentNote is not null) _currentNote.UpdatedAt = DateTimeOffset.Now;
    }

    private void NoteEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingNote && _currentNote is not null)
        {
            _currentNote.UpdatedAt = DateTimeOffset.Now;
            ApplyChecklistStyles(NoteEditor.Document);
        }
        UpdateNoteHistoryButtons();
    }

    private void NoteEditor_SelectionChanged(object sender, RoutedEventArgs e) => UpdateNoteHistoryButtons();

    private void NoteUndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (NoteEditor.CanUndo) NoteEditor.Undo();
        NoteEditor.Focus();
        UpdateNoteHistoryButtons();
    }

    private void NoteRedoButton_Click(object sender, RoutedEventArgs e)
    {
        if (NoteEditor.CanRedo) NoteEditor.Redo();
        NoteEditor.Focus();
        UpdateNoteHistoryButtons();
    }

    private void ToggleNoteChecklistButton_Click(object sender, RoutedEventArgs e)
    {
        if (NoteEditor.CaretPosition.Paragraph is not { } paragraph) return;
        var text = ParagraphText(paragraph);
        if (text.StartsWith("☐ ", StringComparison.Ordinal) || text.StartsWith("☑ ", StringComparison.Ordinal))
            RemoveChecklistMarker(paragraph);
        else if (paragraph.Inlines.FirstInline is { } firstInline)
            paragraph.Inlines.InsertBefore(firstInline, new Run("☐ "));
        else
            paragraph.Inlines.Add(new Run("☐ "));
        ApplyChecklistStyle(paragraph);
        MarkCurrentNoteChanged();
        NoteEditor.Focus();
    }

    private void DecreaseNoteFontSizeButton_Click(object sender, RoutedEventArgs e) => ChangeNoteFontSize(-1);

    private void IncreaseNoteFontSizeButton_Click(object sender, RoutedEventArgs e) => ChangeNoteFontSize(1);

    private void DecreaseNoteLineSpacingButton_Click(object sender, RoutedEventArgs e) => ChangeNoteLineSpacing(-0.1);

    private void IncreaseNoteLineSpacingButton_Click(object sender, RoutedEventArgs e) => ChangeNoteLineSpacing(0.1);

    private void DecreaseNoteLetterSpacingButton_Click(object sender, RoutedEventArgs e) => ChangeNoteLetterSpacing(-0.02);

    private void IncreaseNoteLetterSpacingButton_Click(object sender, RoutedEventArgs e) => ChangeNoteLetterSpacing(0.02);

    private void ResetNoteTypographyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote is null) return;
        _currentNote.FontSize = DefaultNoteFontSize;
        _currentNote.LineSpacing = DefaultNoteLineSpacing;
        _currentNote.LetterSpacing = DefaultNoteLetterSpacing;
        ApplyNoteTypography(_currentNote, true);
    }

    private void ChangeNoteFontSize(int delta)
    {
        if (_currentNote is null) return;
        _currentNote.FontSize = Math.Clamp(_currentNote.FontSize + delta, 12, 32);
        ApplyNoteTypography(_currentNote, true);
    }

    private void ChangeNoteLineSpacing(double delta)
    {
        if (_currentNote is null) return;
        _currentNote.LineSpacing = Math.Clamp(Math.Round(_currentNote.LineSpacing + delta, 1), 1.0, 2.0);
        ApplyNoteTypography(_currentNote, true);
    }

    private void ChangeNoteLetterSpacing(double delta)
    {
        if (_currentNote is null) return;
        _currentNote.LetterSpacing = Math.Clamp(Math.Round(_currentNote.LetterSpacing + delta, 2), 0, 0.16);
        ApplyNoteTypography(_currentNote, true);
    }

    private void ApplyNoteTypography(NoteRecord note, bool markChanged)
    {
        NormalizeNoteTypography(note);
        NoteEditor.FontSize = note.FontSize;
        NoteEditor.Document.FontSize = note.FontSize;
        NoteEditor.Document.FontStretch = LetterSpacingStretch(note.LetterSpacing);
        foreach (var paragraph in NoteEditor.Document.Blocks.OfType<Paragraph>())
        {
            paragraph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            paragraph.LineHeight = note.FontSize * note.LineSpacing;
        }
        NoteFontSizeText.Text = note.FontSize.ToString();
        NoteLineSpacingText.Text = note.LineSpacing.ToString("0.0");
        NoteLetterSpacingText.Text = note.LetterSpacing.ToString("0.00");
        if (markChanged) MarkCurrentNoteChanged();
    }

    private static FontStretch LetterSpacingStretch(double letterSpacing)
    {
        var stretch = 5 + (int)Math.Round(NormalizeNoteLetterSpacing(letterSpacing) / 0.04,
            MidpointRounding.AwayFromZero);
        return FontStretch.FromOpenTypeStretch(Math.Clamp(stretch, 5, 9));
    }

    private void MarkCurrentNoteChanged()
    {
        if (_currentNote is not null) _currentNote.UpdatedAt = DateTimeOffset.Now;
        UpdateNoteHistoryButtons();
    }

    private void UpdateNoteHistoryButtons()
    {
        if (NoteUndoButton is not null) NoteUndoButton.IsEnabled = NoteEditor?.CanUndo == true;
        if (NoteRedoButton is not null) NoteRedoButton.IsEnabled = NoteEditor?.CanRedo == true;
    }

    private bool TryToggleChecklistAt(Point point)
    {
        var position = NoteEditor.GetPositionFromPoint(point, true);
        var paragraph = position?.Paragraph;
        if (paragraph is null) return false;
        var text = ParagraphText(paragraph);
        if (!text.StartsWith("☐", StringComparison.Ordinal) && !text.StartsWith("☑", StringComparison.Ordinal)) return false;

        var markerPosition = TextPositionAtOffset(paragraph, 0);
        if (markerPosition is null) return false;
        var markerRect = markerPosition.GetCharacterRect(LogicalDirection.Forward);
        if (point.X > markerRect.Left + NoteEditor.FontSize * 1.8 ||
            point.Y < markerRect.Top - 4 || point.Y > markerRect.Bottom + 4) return false;

        var markerEnd = TextPositionAtOffset(paragraph, 1);
        if (markerEnd is null) return false;
        new TextRange(markerPosition, markerEnd).Text = text[0] == '☐' ? "☑" : "☐";
        ApplyChecklistStyle(paragraph);
        MarkCurrentNoteChanged();
        return true;
    }

    private static void RemoveChecklistMarker(Paragraph paragraph)
    {
        var text = ParagraphText(paragraph);
        var length = text.Length > 1 && text[1] == ' ' ? 2 : 1;
        var start = TextPositionAtOffset(paragraph, 0);
        var end = TextPositionAtOffset(paragraph, length);
        if (start is not null && end is not null) new TextRange(start, end).Text = string.Empty;
        paragraph.ClearValue(TextElement.ForegroundProperty);
    }

    private static string ParagraphText(Paragraph paragraph) =>
        new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.TrimEnd('\r', '\n');

    private static TextPointer? TextPositionAtOffset(Paragraph paragraph, int offset)
    {
        var pointer = paragraph.ContentStart;
        var remaining = offset;
        while (pointer is not null && pointer.CompareTo(paragraph.ContentEnd) < 0)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var text = pointer.GetTextInRun(LogicalDirection.Forward);
                if (remaining <= text.Length) return pointer.GetPositionAtOffset(remaining, LogicalDirection.Forward);
                remaining -= text.Length;
            }
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
        return remaining == 0 ? paragraph.ContentEnd : null;
    }

    private static void ApplyChecklistStyles(FlowDocument document)
    {
        foreach (var paragraph in document.Blocks.OfType<Paragraph>()) ApplyChecklistStyle(paragraph);
    }

    private static void ApplyChecklistStyle(Paragraph paragraph)
    {
        var text = ParagraphText(paragraph);
        if (text.StartsWith("☑", StringComparison.Ordinal))
            paragraph.SetResourceReference(TextElement.ForegroundProperty, "MutedTextBrush");
        else if (text.StartsWith("☐", StringComparison.Ordinal))
            paragraph.ClearValue(TextElement.ForegroundProperty);
    }

    private void InsertImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote is null) return;
        var dialog = new OpenFileDialog { Title = "选择插入到笔记的图片", Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            Directory.CreateDirectory(_noteImagesDirectory);
            var storedPath = Path.Combine(_noteImagesDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(dialog.FileName)}");
            File.Copy(dialog.FileName, storedPath);
            var source = new BitmapImage();
            source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.UriSource = new Uri(storedPath, UriKind.Absolute); source.EndInit(); source.Freeze();
            var image = new Image { Source = source, Stretch = Stretch.Uniform, Cursor = Cursors.Arrow };
            var frame = CreateImageFrame(image, Math.Min(420, Math.Max(180, source.PixelWidth)));
            NoteEditor.Document.Blocks.Add(new BlockUIContainer(frame) { Margin = new Thickness(0, 12, 0, 12) });
        }
        catch (Exception ex) { AppLog.Error($"Failed to insert note image '{dialog.FileName}'.", ex); AppendLog($"[{DateTime.Now:HH:mm:ss}] 插入图片失败：{ex.Message}"); }
    }

    private void NoteEditor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var frame = FindImageFrame(e.OriginalSource);
        if (frame is null)
        {
            if (TryToggleChecklistAt(e.GetPosition(NoteEditor))) e.Handled = true;
            return;
        }
        var hitImageContent = IsImageContentHit(e.OriginalSource);
        var position = e.GetPosition(frame);
        var onResizeEdge = frame == _selectedImageFrame &&
            TryGetImageResizeEdge(frame, position, out _imageResizeUsesY, out _imageResizeSign);

        if (e.ClickCount >= 2 && hitImageContent)
        {
            SelectImage(frame);
            ClearEditorSelectionSoon();
            e.Handled = true;
            return;
        }

        if (onResizeEdge)
        {
            _resizingImage = true;
            _imageResizeStart = e.GetPosition(NoteEditor);
            _imageResizeStartWidth = frame.Width;
            frame.CaptureMouse();
        }
        else if (!hitImageContent)
        {
            DeselectImage();
        }

        ClearEditorSelectionSoon();
        e.Handled = true;
    }

    private void NoteEditor_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_resizingImage && _selectedImageFrame is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            ResizeSelectedImageFrame(e.GetPosition(NoteEditor));
            ClearEditorSelectionSoon();
            e.Handled = true;
            return;
        }

        var frame = FindImageFrame(e.OriginalSource);
        if (frame is null) return;
        frame.Cursor = frame == _selectedImageFrame && TryGetImageResizeEdge(frame, e.GetPosition(frame), out var usesY, out _)
            ? usesY ? Cursors.SizeNS : Cursors.SizeWE
            : Cursors.Arrow;
    }

    private void NoteEditor_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizingImage) return;
        _resizingImage = false;
        _selectedImageFrame?.ReleaseMouseCapture();
        if (_currentNote is not null) _currentNote.UpdatedAt = DateTimeOffset.Now;
        ClearEditorSelectionSoon();
        e.Handled = true;
    }

    private void NoteImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var frame = sender as Border ?? FindParent<Border>(sender as DependencyObject, "NoteImageFrame");
        if (frame is null) return;
        var hitImageContent = IsImageContentHit(e.OriginalSource);
        if (e.ClickCount == 2 && hitImageContent)
        {
            SelectImage(frame);
            ClearEditorSelection();
            e.Handled = true;
            return;
        }

        var position = e.GetPosition(frame);
        if (frame != _selectedImageFrame || !TryGetImageResizeEdge(frame, position, out _imageResizeUsesY, out _imageResizeSign))
        {
            if (!hitImageContent) DeselectImage();
            ClearEditorSelection();
            e.Handled = true;
            return;
        }
        _resizingImage = true;
        _imageResizeStart = e.GetPosition(NoteEditor);
        _imageResizeStartWidth = frame.Width;
        frame.CaptureMouse();
        ClearEditorSelection();
        e.Handled = true;
    }

    private void NoteImage_MouseMove(object sender, MouseEventArgs e)
    {
        var frame = sender as Border ?? FindParent<Border>(sender as DependencyObject, "NoteImageFrame");
        if (frame is null) return;
        if (_resizingImage && _selectedImageFrame == frame && e.LeftButton == MouseButtonState.Pressed)
        {
            ResizeSelectedImageFrame(e.GetPosition(NoteEditor));
            ClearEditorSelection();
            e.Handled = true;
            return;
        }

        frame.Cursor = frame == _selectedImageFrame && TryGetImageResizeEdge(frame, e.GetPosition(frame), out var usesY, out _)
            ? usesY ? Cursors.SizeNS : Cursors.SizeWE
            : Cursors.Arrow;
    }

    private void NoteImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var frame = sender as Border ?? FindParent<Border>(sender as DependencyObject, "NoteImageFrame");
        if (frame is null || !_resizingImage) return;
        _resizingImage = false;
        frame.ReleaseMouseCapture();
        if (_currentNote is not null) _currentNote.UpdatedAt = DateTimeOffset.Now;
        e.Handled = true;
    }

    private void SelectImage(Border frame)
    {
        DeselectImage();
        _selectedImageFrame = frame;
        _selectedImageFrame.BorderBrush = new SolidColorBrush(Color.FromRgb(30, 144, 255));
        _selectedImageFrame.BorderThickness = new Thickness(2);
        _selectedImage = frame.Child as Image;
    }

    private void DeselectImage()
    {
        if (_selectedImageFrame is not null)
        {
            _selectedImageFrame.BorderBrush = Brushes.Transparent;
            _selectedImageFrame.BorderThickness = new Thickness(2);
            _selectedImageFrame.Cursor = Cursors.Arrow;
        }
        _selectedImageFrame = null;
        _selectedImage = null;
    }

    private void ResizeSelectedImageFrame(Point current)
    {
        if (_selectedImageFrame is null) return;
        var delta = _imageResizeUsesY ? current.Y - _imageResizeStart.Y : current.X - _imageResizeStart.X;
        _selectedImageFrame.Width = Math.Clamp(_imageResizeStartWidth + delta * _imageResizeSign, 120, 680);
    }

    private void ClearEditorSelection()
    {
        NoteEditor.Selection.Select(NoteEditor.CaretPosition, NoteEditor.CaretPosition);
    }

    private void ClearEditorSelectionSoon()
    {
        ClearEditorSelection();
        Dispatcher.BeginInvoke(new Action(ClearEditorSelection), System.Windows.Threading.DispatcherPriority.Background);
    }

    private static Border? FindImageFrame(object source)
    {
        return source is DependencyObject dependencyObject
            ? FindSelfOrParent<Border>(dependencyObject, "NoteImageFrame")
            : null;
    }

    private static bool IsImageContentHit(object source)
    {
        return source is DependencyObject dependencyObject &&
            FindSelfOrParent<Image>(dependencyObject, "NoteImage") is not null;
    }

    private static bool TryGetImageResizeEdge(FrameworkElement element, Point position, out bool usesY, out int sign)
    {
        const double edge = 10;
        var left = position.X <= edge;
        var right = position.X >= element.ActualWidth - edge;
        var top = position.Y <= edge;
        var bottom = position.Y >= element.ActualHeight - edge;

        if (left || right)
        {
            usesY = false;
            sign = left ? -1 : 1;
            return true;
        }

        if (top || bottom)
        {
            usesY = true;
            sign = top ? -1 : 1;
            return true;
        }

        usesY = false;
        sign = 1;
        return false;
    }

    private Border CreateImageFrame(Image image, double width)
    {
        image.Tag = "NoteImage";
        var frame = new Border { Tag = "NoteImageFrame", Width = width, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6), Padding = new Thickness(2), Margin = new Thickness(0, 12, 0, 12), Child = image, Cursor = Cursors.Arrow, Focusable = false };
        AttachImageSelection(frame);
        return frame;
    }

    private void RenderMindMap()
    {
        MindMapCanvas.Children.Clear();
        if (_currentMindMap is null) return;
        foreach (var node in _currentMindMap.Nodes.Where(node => node.ParentId is not null))
        {
            var parent = _currentMindMap.Nodes.FirstOrDefault(item => item.Id == node.ParentId);
            if (parent is null) continue;
            MindMapCanvas.Children.Add(new Line { X1 = parent.X + parent.Width, Y1 = parent.Y + parent.Height / 2, X2 = node.X, Y2 = node.Y + node.Height / 2, Stroke = new SolidColorBrush(Color.FromRgb(75, 84, 91)), StrokeThickness = 1.5, IsHitTestVisible = false });
        }
        foreach (var node in _currentMindMap.Nodes)
        {
            var border = new Border { Tag = node.Id, Width = node.Width, Height = node.Height, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(node.Color)), BorderBrush = node == _selectedMindMapNode ? new SolidColorBrush(Color.FromRgb(88, 178, 255)) : new SolidColorBrush(Color.FromRgb(79, 89, 97)), BorderThickness = node == _selectedMindMapNode ? new Thickness(2) : new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 8, 12, 8), Cursor = Cursors.SizeAll };
            border.Child = new TextBlock { Text = node.Text, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            border.MouseLeftButtonDown += MindMapNode_MouseLeftButtonDown;
            border.MouseMove += MindMapNode_MouseMove;
            border.MouseLeftButtonUp += MindMapNode_MouseLeftButtonUp;
            border.MouseRightButtonUp += MindMapNode_MouseRightButtonUp;
            Canvas.SetLeft(border, node.X); Canvas.SetTop(border, node.Y); Panel.SetZIndex(border, 1);
            MindMapCanvas.Children.Add(border);
        }
    }

    private void MindMapNode_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string id } border || _currentMindMap is null) return;
        _selectedMindMapNode = _currentMindMap.Nodes.FirstOrDefault(node => node.Id == id);
        if (_selectedMindMapNode is null) return;
        if (e.ClickCount == 2)
        {
            _draggingMindMapNode?.ReleaseMouseCapture();
            _draggingMindMapNode = null;
            BeginMindMapNodeEdit(border, _selectedMindMapNode);
            e.Handled = true;
            return;
        }
        TakeMindMapSnapshot();
        _mindMapDragStart = e.GetPosition(MindMapCanvas);
        _mindMapNodeStart = new Point(_selectedMindMapNode.X, _selectedMindMapNode.Y);
        _mindMapNodeSizeStart = new Size(_selectedMindMapNode.Width, _selectedMindMapNode.Height);
        _resizingMindMapNode = TryGetMindMapResizeEdge(border, e.GetPosition(border), out _mindMapResizeUsesY, out _mindMapResizeSign);
        _draggingMindMapNode = border;
        border.CaptureMouse();
        MindMapCanvas.Focus();
        border.BorderBrush = new SolidColorBrush(Color.FromRgb(88, 178, 255));
        border.BorderThickness = new Thickness(2);
        e.Handled = true;
    }

    private void MindMapNode_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Border border) return;
        if (_draggingMindMapNode?.Tag is not string id || _currentMindMap is null || e.LeftButton != MouseButtonState.Pressed)
        {
            border.Cursor = border.Tag is string selectedId && _selectedMindMapNode?.Id == selectedId && TryGetMindMapResizeEdge(border, e.GetPosition(border), out var usesY, out _)
                ? usesY ? Cursors.SizeNS : Cursors.SizeWE
                : Cursors.SizeAll;
            return;
        }
        var node = _currentMindMap.Nodes.FirstOrDefault(item => item.Id == id);
        if (node is null) return;
        var position = e.GetPosition(MindMapCanvas);
        if (_resizingMindMapNode)
        {
            var delta = _mindMapResizeUsesY ? position.Y - _mindMapDragStart.Y : position.X - _mindMapDragStart.X;
            if (_mindMapResizeUsesY)
            {
                node.Height = Math.Clamp(_mindMapNodeSizeStart.Height + delta * _mindMapResizeSign, 44, 360);
                if (_mindMapResizeSign < 0) node.Y = Math.Max(0, _mindMapNodeStart.Y + _mindMapNodeSizeStart.Height - node.Height);
            }
            else
            {
                node.Width = Math.Clamp(_mindMapNodeSizeStart.Width + delta * _mindMapResizeSign, 110, 520);
                if (_mindMapResizeSign < 0) node.X = Math.Max(0, _mindMapNodeStart.X + _mindMapNodeSizeStart.Width - node.Width);
            }
        }
        else
        {
            node.X = Math.Max(0, _mindMapNodeStart.X + position.X - _mindMapDragStart.X);
            node.Y = Math.Max(0, _mindMapNodeStart.Y + position.Y - _mindMapDragStart.Y);
        }
        _currentMindMap.UpdatedAt = DateTimeOffset.Now;
        Canvas.SetLeft(_draggingMindMapNode, node.X); Canvas.SetTop(_draggingMindMapNode, node.Y);
        _draggingMindMapNode.Width = node.Width; _draggingMindMapNode.Height = node.Height;
    }

    private void MindMapNode_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingMindMapNode is null) return;
        _draggingMindMapNode.ReleaseMouseCapture();
        _draggingMindMapNode = null;
        _resizingMindMapNode = false;
        SaveMindMaps();
        RenderMindMap();
    }

    private void MindMapNode_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string id } border || _currentMindMap is null) return;
        _selectedMindMapNode = _currentMindMap.Nodes.FirstOrDefault(node => node.Id == id);
        if (_selectedMindMapNode is null) return;
        var menu = new ContextMenu();
        var add = new MenuItem { Header = "添加子节点" }; add.Click += (_, _) => AddMindMapChild(_selectedMindMapNode);
        var delete = new MenuItem { Header = "删除" }; delete.Click += (_, _) => DeleteSelectedMindMapNode();
        var colors = new MenuItem { Header = "更改颜色" };
        foreach (var color in new[] { ("蓝灰", "#313A44"), ("墨绿", "#2D413B"), ("暖棕", "#493B32"), ("紫灰", "#40384B") })
        {
            var item = new MenuItem { Header = color.Item1 }; item.Click += (_, _) => { if (_selectedMindMapNode is null) return; TakeMindMapSnapshot(); _selectedMindMapNode.Color = color.Item2; TouchMindMap(); RenderMindMap(); };
            colors.Items.Add(item);
        }
        menu.Items.Add(add); menu.Items.Add(delete); menu.Items.Add(colors);
        border.ContextMenu = menu; menu.IsOpen = true;
        e.Handled = true;
    }

    private void AddMindMapChild(MindMapNode? parent, string text = "新节点")
    {
        if (parent is null || _currentMindMap is null) return;
        TakeMindMapSnapshot();
        var child = new MindMapNode { Id = Guid.NewGuid().ToString("N"), ParentId = parent.Id, Text = text, X = parent.X + parent.Width + 40, Y = parent.Y + parent.Height + 45, Color = "#313A44" };
        _currentMindMap.Nodes.Add(child); _selectedMindMapNode = child; TouchMindMap(); RenderMindMap();
    }

    private void DeleteSelectedMindMapNode()
    {
        if (_currentMindMap is null || _selectedMindMapNode is null) return;
        if (_selectedMindMapNode.ParentId is null) { System.Windows.MessageBox.Show("中心主题不能删除。", "思维导图", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        TakeMindMapSnapshot();
        var deleteIds = new HashSet<string> { _selectedMindMapNode.Id };
        for (var changed = true; changed;) { changed = false; foreach (var node in _currentMindMap.Nodes.Where(node => node.ParentId is not null && deleteIds.Contains(node.ParentId))) changed |= deleteIds.Add(node.Id); }
        _currentMindMap.Nodes.RemoveAll(node => deleteIds.Contains(node.Id)); _selectedMindMapNode = null; TouchMindMap(); RenderMindMap();
    }

    private void BeginMindMapNodeEdit(Border border, MindMapNode node)
    {
        var editor = new TextBox { Text = node.Text, Padding = new Thickness(8, 4, 8, 4), Background = new SolidColorBrush(Color.FromRgb(28, 31, 33)), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0), AcceptsReturn = false };
        void Commit()
        {
            var text = editor.Text.Trim(); if (!string.IsNullOrWhiteSpace(text)) { TakeMindMapSnapshot(); node.Text = text; TouchMindMap(); }
            RenderMindMap();
        }
        editor.LostKeyboardFocus += (_, _) => Commit();
        editor.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } else if (e.Key == Key.Escape) { RenderMindMap(); e.Handled = true; } };
        border.Child = editor; editor.Focus(); editor.SelectAll();
    }

    private void MindMapCanvas_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.MiddleButton != MouseButtonState.Pressed)
        {
            if (e.OriginalSource == MindMapCanvas)
            {
                _selectedMindMapNode = null;
                RenderMindMap();
                MindMapCanvas.Focus();
            }
            return;
        }
        _panningMindMap = true; _mindMapPanStart = e.GetPosition(this); _mindMapTranslateStart = new Point(MindMapTranslateTransform.X, MindMapTranslateTransform.Y); MindMapCanvas.CaptureMouse(); e.Handled = true;
    }

    private void MindMapCanvas_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panningMindMap || e.MiddleButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this); MindMapTranslateTransform.X = _mindMapTranslateStart.X + point.X - _mindMapPanStart.X; MindMapTranslateTransform.Y = _mindMapTranslateStart.Y + point.Y - _mindMapPanStart.Y;
    }

    private void MindMapCanvas_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panningMindMap) return; _panningMindMap = false; MindMapCanvas.ReleaseMouseCapture();
    }

    private void MindMapCanvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var scale = Math.Clamp(MindMapScaleTransform.ScaleX + (e.Delta > 0 ? 0.1 : -0.1), 0.4, 2.2); MindMapScaleTransform.ScaleX = MindMapScaleTransform.ScaleY = scale; e.Handled = true;
    }

    private void MindMapCanvas_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { DeleteSelectedMindMapNode(); e.Handled = true; }
        else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _mindMapUndo.TryPop(out var snapshot)) { RestoreMindMapSnapshot(snapshot); e.Handled = true; }
        else if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _selectedMindMapNode is not null && Clipboard.ContainsText())
        {
            var text = Clipboard.GetText().Trim();
            if (!string.IsNullOrWhiteSpace(text)) AddMindMapChild(_selectedMindMapNode, text);
            e.Handled = true;
        }
    }

    private static bool TryGetMindMapResizeEdge(FrameworkElement element, Point position, out bool usesY, out int sign)
    {
        const double edge = 9;
        var left = position.X <= edge; var right = position.X >= element.ActualWidth - edge;
        var top = position.Y <= edge; var bottom = position.Y >= element.ActualHeight - edge;
        if (left || right) { usesY = false; sign = left ? -1 : 1; return true; }
        if (top || bottom) { usesY = true; sign = top ? -1 : 1; return true; }
        usesY = false; sign = 1; return false;
    }

    private void MindMapCanvasFrame_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border frame || !TryGetMindMapCanvasResizeEdge(frame, e.GetPosition(frame), out _mindMapCanvasResizeUsesY, out _mindMapCanvasResizeSign)) return;
        _resizingMindMapCanvas = true;
        _mindMapCanvasResizeStart = e.GetPosition(frame);
        _mindMapCanvasSizeStart = new Size(MindMapCanvas.Width, MindMapCanvas.Height);
        _mindMapCanvasMarginStart = frame.Margin;
        _mindMapCanvasNodesStart = _currentMindMap?.Nodes.ToDictionary(node => node.Id, node => new Point(node.X, node.Y)) ?? [];
        frame.CaptureMouse();
        e.Handled = true;
    }

    private void MindMapCanvasFrame_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Border frame) return;
        if (_resizingMindMapCanvas && e.LeftButton == MouseButtonState.Pressed)
        {
            var point = e.GetPosition(frame);
            var delta = _mindMapCanvasResizeUsesY ? point.Y - _mindMapCanvasResizeStart.Y : point.X - _mindMapCanvasResizeStart.X;
            if (_mindMapCanvasResizeUsesY)
            {
                var height = Math.Max(500, _mindMapCanvasSizeStart.Height + delta * _mindMapCanvasResizeSign);
                if (_mindMapCanvasResizeSign < 0) SetMindMapCanvasOrigin(0, _mindMapCanvasSizeStart.Height - height);
                MindMapCanvas.Height = _currentMindMap!.CanvasHeight = height;
            }
            else
            {
                var width = Math.Max(700, _mindMapCanvasSizeStart.Width + delta * _mindMapCanvasResizeSign);
                if (_mindMapCanvasResizeSign < 0) SetMindMapCanvasOrigin(_mindMapCanvasSizeStart.Width - width, 0);
                MindMapCanvas.Width = _currentMindMap!.CanvasWidth = width;
            }
            e.Handled = true;
            return;
        }
        frame.Cursor = TryGetMindMapCanvasResizeEdge(frame, e.GetPosition(frame), out var usesY, out _) ? usesY ? Cursors.SizeNS : Cursors.SizeWE : Cursors.Arrow;
    }

    private void MindMapCanvasFrame_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border frame || !_resizingMindMapCanvas) return;
        _resizingMindMapCanvas = false;
        frame.ReleaseMouseCapture();
        TouchMindMap();
        RenderMindMap();
        e.Handled = true;
    }

    private void SetMindMapCanvasOrigin(double x, double y)
    {
        if (_currentMindMap is null) return;
        MindMapCanvasFrame.Margin = new Thickness(Math.Max(40, _mindMapCanvasMarginStart.Left + x), Math.Max(40, _mindMapCanvasMarginStart.Top + y), _mindMapCanvasMarginStart.Right, _mindMapCanvasMarginStart.Bottom);
        _currentMindMap.CanvasLeft = MindMapCanvasFrame.Margin.Left;
        _currentMindMap.CanvasTop = MindMapCanvasFrame.Margin.Top;
        foreach (var node in _currentMindMap.Nodes)
            if (_mindMapCanvasNodesStart.TryGetValue(node.Id, out var start)) { node.X = start.X - x; node.Y = start.Y - y; }
    }

    private static bool TryGetMindMapCanvasResizeEdge(FrameworkElement frame, Point position, out bool usesY, out int sign)
    {
        const double edge = 12;
        if (position.X <= edge) { usesY = false; sign = -1; return true; }
        if (position.X >= frame.ActualWidth - edge) { usesY = false; sign = 1; return true; }
        if (position.Y <= edge) { usesY = true; sign = -1; return true; }
        if (position.Y >= frame.ActualHeight - edge) { usesY = true; sign = 1; return true; }
        usesY = false; sign = 1;
        return false;
    }


    private void TakeMindMapSnapshot()
    {
        if (_currentMindMap is null) return;
        _mindMapUndo.Push(JsonSerializer.Serialize(_currentMindMap, JsonOptions));
    }

    private void RestoreMindMapSnapshot(string snapshot)
    {
        var map = JsonSerializer.Deserialize<MindMapRecord>(snapshot, JsonOptions); if (map is null) return;
        var index = _mindMaps.FindIndex(item => item.Id == map.Id); if (index >= 0) _mindMaps[index] = map; _currentMindMap = map; _selectedMindMapNode = null; SaveMindMaps(); RenderMindMap();
    }

    private void TouchMindMap()
    {
        if (_currentMindMap is null) return; _currentMindMap.UpdatedAt = DateTimeOffset.Now; SaveMindMaps();
    }

    private void LoadMindMaps()
    {
        try { if (File.Exists(_mindMapsPath)) _mindMaps = JsonSerializer.Deserialize<List<MindMapRecord>>(File.ReadAllText(_mindMapsPath), JsonOptions) ?? []; }
        catch (Exception ex) { AppLog.Error("Failed to load mind maps; using empty list.", ex); _mindMaps = []; }
    }

    private void LoadDeletedMindMaps()
    {
        try { if (File.Exists(_recycleBinMindMapsPath)) _deletedMindMaps = JsonSerializer.Deserialize<List<MindMapRecord>>(File.ReadAllText(_recycleBinMindMapsPath), JsonOptions) ?? []; }
        catch (Exception ex) { AppLog.Error("Failed to load deleted mind maps; using empty list.", ex); _deletedMindMaps = []; }
    }

    private void SaveMindMaps()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_mindMapsPath)!); File.WriteAllText(_mindMapsPath, JsonSerializer.Serialize(_mindMaps, JsonOptions)); }
        catch (Exception ex) { AppLog.Error("Failed to save mind maps.", ex); }
    }

    private void SaveDeletedMindMaps()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_recycleBinMindMapsPath)!); File.WriteAllText(_recycleBinMindMapsPath, JsonSerializer.Serialize(_deletedMindMaps, JsonOptions)); }
        catch (Exception ex) { AppLog.Error("Failed to save deleted mind maps.", ex); }
    }

    private static string? PromptForText(string title, string message, string initialText)
    {
        var input = new TextBox { Text = initialText, MinWidth = 310 };
        var dialog = new Window { Title = title, Width = 410, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current.MainWindow, ResizeMode = ResizeMode.NoResize, Background = (Brush)Application.Current.Resources["SurfaceBrush"] };
        var panel = new StackPanel { Margin = new Thickness(22) }; panel.Children.Add(new TextBlock { Text = message, Foreground = (Brush)Application.Current.Resources["PrimaryTextBrush"], Margin = new Thickness(0, 0, 0, 10) }); panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) }; var ok = new Button { Content = "创建", IsDefault = true, Padding = new Thickness(12, 6, 12, 6) };
        cancel.Click += (_, _) => dialog.DialogResult = false; ok.Click += (_, _) => dialog.DialogResult = true; buttons.Children.Add(cancel); buttons.Children.Add(ok); panel.Children.Add(buttons); dialog.Content = panel;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    private void RefreshNoteCards()
    {
        NoteCardsPanel.Children.Clear();
        var ordered = _notes.OrderByDescending(note => note.UpdatedAt).ToList();
        NotesSummaryText.Text = ordered.Count == 0 ? "尚无记录" : $"{ordered.Count} 则本地记录";
        foreach (var note in ordered)
        {
            var container = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var card = new Button { Height = 92, Tag = note.Id, Style = (Style)FindResource("ActionCard"), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Padding = new Thickness(12, 10, 12, 10) };
            card.SetResourceReference(Control.BackgroundProperty, _currentNote?.Id == note.Id && EditorView.Visibility == Visibility.Visible ? "SurfaceElevatedBrush" : "TopBarBrush");
            card.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
            card.Click += OpenNoteCard_Click;
            var menu = new ContextMenu();
            var deleteItem = new MenuItem { Header = "删除", Tag = note.Id };
            deleteItem.Click += DeleteNoteButton_Click;
            menu.Items.Add(deleteItem);
            card.ContextMenu = menu;
            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var title = new TextBlock { Text = note.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            content.Children.Add(title);
            var preview = new TextBlock { Text = string.IsNullOrWhiteSpace(note.Preview) ? "空白记录" : note.Preview, FontSize = 12, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 5, 0, 5), MaxHeight = 18 };
            preview.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            Grid.SetRow(preview, 1); content.Children.Add(preview);
            var date = new TextBlock { Text = note.UpdatedAt.LocalDateTime.ToString("MM.dd  HH:mm"), FontSize = 11 };
            date.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            Grid.SetRow(date, 2); content.Children.Add(date);
            card.Content = content;
            container.Children.Add(card);
            NoteCardsPanel.Children.Add(container);
        }
    }

    private void RefreshTrashCards()
    {
        TrashCardsPanel.Children.Clear();
        var ordered = _deletedNotes.OrderByDescending(note => note.DeletedAt ?? note.UpdatedAt).ToList();
        var deletedTasks = _tasks.Where(task => task.Status == "deleted").OrderByDescending(task => task.DeletedAt).ToList();
        var total = ordered.Count + _deletedMindMaps.Count + deletedTasks.Count;
        TrashSummaryText.Text = total == 0 ? "回收站为空。" : $"回收站中有 {total} 项内容，可恢复或永久删除。";
        foreach (var task in deletedTasks)
        {
            var container = new StackPanel { Width = 270, Margin = new Thickness(0, 0, 16, 16) };
            var card = new Border { Height = 132, Background = new SolidColorBrush(Color.FromRgb(21, 23, 25)), BorderBrush = new SolidColorBrush(Color.FromRgb(41, 45, 48)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16) };
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = task.Title, Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 235)), FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new TextBlock { Text = $"训练任务 · {CompletedSets(task)}/{TotalSets(task)} 组", Foreground = new SolidColorBrush(Color.FromRgb(154, 161, 165)), FontSize = 12, Margin = new Thickness(0, 12, 0, 0) });
            var deletedAt = task.DeletedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(task.DeletedAt).LocalDateTime.ToString("yyyy.MM.dd  HH:mm") : "未知时间";
            content.Children.Add(new TextBlock { Text = $"删除于 {deletedAt}", Foreground = new SolidColorBrush(Color.FromRgb(116, 124, 129)), FontSize = 10, Margin = new Thickness(0, 8, 0, 0) });
            card.Child = content;
            container.Children.Add(card);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var restoreButton = new Button { Tag = task.Id, Style = (Style)FindResource("PrimaryButton"), Content = "恢复", Padding = new Thickness(12, 6, 12, 6) };
            restoreButton.Click += RestoreDeletedTaskButton_Click;
            actions.Children.Add(restoreButton);
            var deleteButton = new Button { Tag = task.Id, Style = (Style)FindResource("SecondaryButton"), Content = "永久删除", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
            deleteButton.Click += PermanentlyDeleteTaskButton_Click;
            actions.Children.Add(deleteButton);
            container.Children.Add(actions);
            TrashCardsPanel.Children.Add(container);
        }
        foreach (var note in ordered)
        {
            var container = new StackPanel { Width = 270, Margin = new Thickness(0, 0, 16, 16) };
            var card = new Border { Height = 132, Background = new SolidColorBrush(Color.FromRgb(21, 23, 25)), BorderBrush = new SolidColorBrush(Color.FromRgb(41, 45, 48)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16) };
            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.Children.Add(new TextBlock { Text = note.Title, Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 235)), FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis });
            var preview = new TextBlock { Text = string.IsNullOrWhiteSpace(note.Preview) ? "空白记录" : note.Preview, Foreground = new SolidColorBrush(Color.FromRgb(154, 161, 165)), FontSize = 12, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 12, 0, 8), MaxHeight = 48 };
            Grid.SetRow(preview, 1); content.Children.Add(preview);
            var deletedAt = note.DeletedAt?.LocalDateTime.ToString("yyyy.MM.dd  HH:mm") ?? "未知删除时间";
            var date = new TextBlock { Text = $"删除于 {deletedAt}", Foreground = new SolidColorBrush(Color.FromRgb(116, 124, 129)), FontSize = 10 };
            Grid.SetRow(date, 2); content.Children.Add(date);
            card.Child = content;
            container.Children.Add(card);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var restoreButton = new Button { Tag = note.Id, Style = (Style)FindResource("PrimaryButton"), Content = "恢复", Padding = new Thickness(12, 6, 12, 6) };
            restoreButton.Click += RestoreNoteButton_Click;
            actions.Children.Add(restoreButton);
            var deleteButton = new Button { Tag = note.Id, Style = (Style)FindResource("SecondaryButton"), Content = "永久删除", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
            deleteButton.Click += PermanentlyDeleteNoteButton_Click;
            actions.Children.Add(deleteButton);
            container.Children.Add(actions);
            TrashCardsPanel.Children.Add(container);
        }
        foreach (var map in _deletedMindMaps.OrderByDescending(map => map.DeletedAt ?? map.UpdatedAt))
        {
            var container = new StackPanel { Width = 270, Margin = new Thickness(0, 0, 16, 16) };
            var card = new Border { Height = 132, Background = new SolidColorBrush(Color.FromRgb(21, 23, 25)), BorderBrush = new SolidColorBrush(Color.FromRgb(41, 45, 48)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16) };
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = map.Title, Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 235)), FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new TextBlock { Text = $"思维导图 · {map.Nodes.Count} 个节点", Foreground = new SolidColorBrush(Color.FromRgb(154, 161, 165)), FontSize = 12, Margin = new Thickness(0, 12, 0, 0) });
            card.Child = content; container.Children.Add(card);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var restoreButton = new Button { Tag = map.Id, Style = (Style)FindResource("PrimaryButton"), Content = "恢复", Padding = new Thickness(12, 6, 12, 6) };
            restoreButton.Click += RestoreMindMapButton_Click;
            actions.Children.Add(restoreButton);
            var deleteButton = new Button { Tag = map.Id, Style = (Style)FindResource("SecondaryButton"), Content = "永久删除", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
            deleteButton.Click += PermanentlyDeleteMindMapButton_Click;
            actions.Children.Add(deleteButton);
            container.Children.Add(actions); TrashCardsPanel.Children.Add(container);
        }
        Dispatcher.BeginInvoke(ApplyThemeToVisualTree);
    }

    private void SaveCurrentNote()
    {
        if (_currentNote is null || _loadingNote) return;
        _currentNote.Title = string.IsNullOrWhiteSpace(NoteTitleBox.Text) ? "未命名记录" : NoteTitleBox.Text.Trim();
        _currentNote.ContentXaml = XamlWriter.Save(NoteEditor.Document);
        _currentNote.Preview = GetDocumentText(NoteEditor.Document);
        _currentNote.UpdatedAt = DateTimeOffset.Now;
        try { SaveNotes(); AppendLog($"[{DateTime.Now:HH:mm:ss}] 已保存记录：{_currentNote.Title}"); }
        catch (Exception ex) { AppLog.Error("Failed to save current note.", ex); AppendLog($"[{DateTime.Now:HH:mm:ss}] 保存记录失败：{ex.Message}"); }
    }

    private void LoadNotes()
    {
        try
        {
            if (File.Exists(_notesPath)) _notes = JsonSerializer.Deserialize<List<NoteRecord>>(File.ReadAllText(_notesPath), JsonOptions) ?? [];
            foreach (var note in _notes) NormalizeNoteTypography(note);
        }
        catch (Exception ex) { AppLog.Error("Failed to load notes; using empty note list.", ex); _notes = []; }
    }

    private void LoadDeletedNotes()
    {
        try
        {
            if (File.Exists(_recycleBinNotesPath)) _deletedNotes = JsonSerializer.Deserialize<List<NoteRecord>>(File.ReadAllText(_recycleBinNotesPath), JsonOptions) ?? [];
            foreach (var note in _deletedNotes) NormalizeNoteTypography(note);
        }
        catch (Exception ex) { AppLog.Error("Failed to load recycle bin notes; using empty recycle bin.", ex); _deletedNotes = []; }
    }

    private void SaveNotes()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_notesPath)!);
        File.WriteAllText(_notesPath, JsonSerializer.Serialize(_notes, JsonOptions));
    }

    private void SaveDeletedNotes()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_recycleBinNotesPath)!);
        File.WriteAllText(_recycleBinNotesPath, JsonSerializer.Serialize(_deletedNotes, JsonOptions));
    }

    private void LoadTasks()
    {
        try
        {
            _tasks = File.Exists(_tasksPath)
                ? JsonSerializer.Deserialize<List<WorkoutTask>>(File.ReadAllText(_tasksPath), JsonOptions) ?? []
                : [];
            foreach (var task in _tasks) NormalizeTask(task);
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to load workout tasks; using empty task list.", ex);
            _tasks = [];
        }
    }

    private void SaveTasks()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_tasksPath)!);
        File.WriteAllText(_tasksPath, JsonSerializer.Serialize(_tasks, JsonOptions));
    }

    private static void NormalizeTask(WorkoutTask task)
    {
        task.Effort = EmptyToNull(task.Effort ?? "");
        task.Discomfort = EmptyToNull(task.Discomfort ?? "");
        task.DiscomfortNotes = EmptyToNull(task.DiscomfortNotes ?? "");
        task.FeedbackNotes = EmptyToNull(task.FeedbackNotes ?? "");
        task.Title = string.IsNullOrWhiteSpace(task.Title) ? "未命名训练" : task.Title.Trim();
        task.Status = task.Status is "active" or "completed" or "archived" or "deleted" or "purged" ? task.Status : "pending";
        task.Exercises ??= [];
        foreach (var exercise in task.Exercises)
        {
            exercise.SkipReason = EmptyToNull(exercise.SkipReason ?? "");
            exercise.Id = string.IsNullOrWhiteSpace(exercise.Id) ? Guid.NewGuid().ToString("N") : exercise.Id;
            exercise.Name = string.IsNullOrWhiteSpace(exercise.Name) ? "未命名动作" : exercise.Name.Trim();
            exercise.Sets ??= [];
            for (var index = 0; index < exercise.Sets.Count; index++)
            {
                var set = exercise.Sets[index];
                set.Rir = EmptyToNull(set.Rir ?? "");
                set.MovementQuality = EmptyToNull(set.MovementQuality ?? "");
                set.Discomfort = EmptyToNull(set.Discomfort ?? "");
                set.DiscomfortNotes = EmptyToNull(set.DiscomfortNotes ?? "");
                if (set.Index <= 0) set.Index = index + 1;
                if (set.ActualReps <= 0) set.ActualReps = set.TargetReps;
                if (set.ActualWeight <= 0) set.ActualWeight = set.TargetWeight;
            }
        }
        if (task.CreatedAt <= 0) task.CreatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        if (task.UpdatedAt <= 0) task.UpdatedAt = task.CreatedAt;
    }

    private static FlowDocument ParseDocument(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return new FlowDocument(new Paragraph());
        try { return XamlReader.Parse(content) as FlowDocument ?? new FlowDocument(new Paragraph()); }
        catch (Exception ex) { AppLog.Error("Failed to parse note document XAML; using plain text fallback.", ex); return new FlowDocument(new Paragraph(new Run(content))); }
    }

    private static string EmptyDocumentXaml() => XamlWriter.Save(new FlowDocument(new Paragraph()));

    private static string PlainDocumentXaml(string content)
    {
        var document = new FlowDocument();
        var lines = (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var line in lines) document.Blocks.Add(new Paragraph(new Run(line)));
        if (document.Blocks.Count == 0) document.Blocks.Add(new Paragraph());
        return XamlWriter.Save(document);
    }

    private static void ApplyIncomingTypography(NoteRecord note, SyncNote incoming)
    {
        if (incoming.FontSize.HasValue) note.FontSize = NormalizeNoteFontSize(incoming.FontSize.Value);
        if (incoming.LineSpacing.HasValue) note.LineSpacing = NormalizeNoteLineSpacing(incoming.LineSpacing.Value);
        if (incoming.LetterSpacing.HasValue) note.LetterSpacing = NormalizeNoteLetterSpacing(incoming.LetterSpacing.Value);
        NormalizeNoteTypography(note);
    }

    private static void NormalizeNoteTypography(NoteRecord note)
    {
        note.FontSize = NormalizeNoteFontSize(note.FontSize);
        note.LineSpacing = NormalizeNoteLineSpacing(note.LineSpacing);
        note.LetterSpacing = NormalizeNoteLetterSpacing(note.LetterSpacing);
    }

    private static int NormalizeNoteFontSize(int value) => value is >= 12 and <= 32 ? value : DefaultNoteFontSize;

    private static double NormalizeNoteLineSpacing(double value) =>
        double.IsFinite(value) && value is >= 1.0 and <= 2.0 ? Math.Round(value, 1) : DefaultNoteLineSpacing;

    private static double NormalizeNoteLetterSpacing(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 0.16 ? Math.Round(value, 2) : DefaultNoteLetterSpacing;

    private static string SyncTitle(string content)
    {
        var firstLine = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "未命名记录";
        return firstLine.Length > 36 ? firstLine[..36] + "…" : firstLine;
    }

    private void AttachInteractiveNoteElements(FlowDocument document)
    {
        foreach (var block in document.Blocks.OfType<BlockUIContainer>())
        {
            if (block.Child is Image image)
                block.Child = CreateImageFrame(image, image.Width > 0 ? image.Width : Math.Max(120, image.ActualWidth));
            AttachInteractiveElement(block.Child);
        }
        foreach (var paragraph in document.Blocks.OfType<Paragraph>())
            foreach (var container in paragraph.Inlines.OfType<InlineUIContainer>())
            {
                if (container.Child is Image image)
                    container.Child = CreateImageFrame(image, image.Width > 0 ? image.Width : Math.Max(120, image.ActualWidth));
                AttachInteractiveElement(container.Child);
            }
    }

    private void AttachInteractiveElement(UIElement element)
    {
        if (element is Border { Tag: "NoteImageFrame" } frame)
        {
            AttachImageSelection(frame);
            return;
        }

    }

    private void AttachImageSelection(Border frame)
    {
        frame.Cursor = Cursors.Arrow;
        frame.PreviewMouseLeftButtonDown -= NoteImage_MouseLeftButtonDown;
        frame.MouseLeftButtonDown -= NoteImage_MouseLeftButtonDown;
        frame.MouseMove -= NoteImage_MouseMove;
        frame.MouseLeftButtonUp -= NoteImage_MouseLeftButtonUp;
        frame.PreviewMouseLeftButtonDown += NoteImage_MouseLeftButtonDown;
        frame.MouseLeftButtonDown += NoteImage_MouseLeftButtonDown;
        frame.MouseMove += NoteImage_MouseMove;
        frame.MouseLeftButtonUp += NoteImage_MouseLeftButtonUp;
        if (frame.Child is Image image)
        {
            image.Tag = "NoteImage";
            image.Cursor = Cursors.Arrow;
            image.PreviewMouseLeftButtonDown -= NoteImage_MouseLeftButtonDown;
            image.MouseLeftButtonDown -= NoteImage_MouseLeftButtonDown;
            image.MouseMove -= NoteImage_MouseMove;
            image.MouseLeftButtonUp -= NoteImage_MouseLeftButtonUp;
            image.PreviewMouseLeftButtonDown += NoteImage_MouseLeftButtonDown;
            image.MouseLeftButtonDown += NoteImage_MouseLeftButtonDown;
            image.MouseMove += NoteImage_MouseMove;
            image.MouseLeftButtonUp += NoteImage_MouseLeftButtonUp;
        }
    }

    private static T? FindParent<T>(DependencyObject? current, string tag) where T : FrameworkElement
    {
        while (current is not null)
        {
            current = GetVisualOrLogicalParent(current);
            if (current is T element && Equals(element.Tag, tag)) return element;
        }
        return null;
    }

    private static T? FindSelfOrParent<T>(DependencyObject? current, string tag) where T : FrameworkElement
    {
        while (current is not null)
        {
            if (current is T element && Equals(element.Tag, tag)) return element;
            current = GetVisualOrLogicalParent(current);
        }
        return null;
    }

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject current)
    {
        if (current is Visual or Visual3D)
        {
            var visualParent = VisualTreeHelper.GetParent(current);
            if (visualParent is not null) return visualParent;
        }

        if (current is FrameworkContentElement contentElement)
            return contentElement.Parent;

        if (current is ContentElement content)
            return ContentOperations.GetParent(content);

        return LogicalTreeHelper.GetParent(current);
    }

    private static string GetDocumentText(FlowDocument document)
    {
        var text = GetFullDocumentText(document);
        return text.Length > 160 ? text[..160] + "…" : text;
    }

    private static string GetFullDocumentText(FlowDocument document) =>
        new TextRange(document.ContentStart, document.ContentEnd).Text.Trim();

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions);
            if (!string.IsNullOrWhiteSpace(settings?.ReceiveDirectory))
                _receiveDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.ReceiveDirectory));
            if (settings is not null)
            {
                if (!string.IsNullOrWhiteSpace(settings.DeviceId)) _deviceId = settings.DeviceId;
                _trustedDeviceKeys = settings.TrustedDeviceKeys ?? [];
                _themeChoice = settings.ThemeChoice switch
                {
                    "Silver" or "Japanese" or "DeathStranding" => settings.ThemeChoice,
                    "Mist" => "Japanese",
                    "Forest" => "DeathStranding",
                    _ => "Silver"
                };
                _edgeOverlaySettings = settings.EdgeOverlay ?? new EdgeOverlaySettings();
                _crosshairSettings = settings.Crosshair ?? new CrosshairSettings();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to load settings; using defaults.", ex);
            // 配置损坏时回退至默认目录，避免阻止应用启动。
        }
    }

    private void SaveSettings()
    {
        var parent = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(parent);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new AppSettings(
            _receiveDirectory,
            _themeChoice,
            _deviceId,
            _trustedDeviceKeys,
            _edgeOverlaySettings,
            _crosshairSettings), JsonOptions));
    }

    private void OpenConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_client is { Connected: true })
        {
            OpenDeviceView();
            return;
        }
        ShowHome();
        ConnectionOverlay.Visibility = Visibility.Visible;
    }

    private void BackToHomeButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectionOverlay.Visibility = Visibility.Collapsed;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void BackHomeTopButton_Click(object sender, RoutedEventArgs e)
    {
        ShowHome();
    }

    private void OpenPreferencesButton_Click(object sender, RoutedEventArgs e)
    {
        HideViews();
        PreferencesView.Visibility = Visibility.Visible;
        ShowPreferencesHome();
        SetActiveNavigation(PreferencesNavButton);
    }

    private void OpenThemeSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        PreferencesHomePanel.Visibility = Visibility.Collapsed;
        ThemeSettingsPanel.Visibility = Visibility.Visible;
        PreferencesSubtitleText.Text = "选择一款主题，整个工作区会立即更新。";
    }

    private void BackToPreferencesHomeButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPreferencesHome();
    }

    private void OpenEdgeOverlaySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        HideViews();
        EdgeOverlayView.Visibility = Visibility.Visible;
        RefreshEdgeOverlayControls();
        SetActiveNavigation(EdgeOverlayNavButton);
    }

    private void RefreshEdgeOverlayControls()
    {
        _updatingEdgeOverlayControls = true;
        EdgeOverlayEnabledBox.IsChecked = _edgeOverlaySettings.Enabled;
        EdgeOverlayShapeBox.SelectedIndex = Math.Clamp((int)_edgeOverlaySettings.Shape, 0, 2);
        EdgeOverlaySizeBox.SelectedIndex = Math.Clamp(_edgeOverlaySettings.Size, 0, 6);
        EdgeOverlayLengthBox.SelectedIndex = Math.Clamp(_edgeOverlaySettings.Length, 0, 6);
        EdgeOverlayDisplayModeBox.SelectedIndex = Math.Clamp((int)_edgeOverlaySettings.DisplayMode, 0, 1);
        EdgeOverlayAspectRatioBox.SelectedIndex = Math.Clamp((int)_edgeOverlaySettings.AspectRatio, 0, 3);
        EdgeOverlaySplitBox.SelectedIndex = Math.Clamp((int)_edgeOverlaySettings.Split, 0, 2);
        EdgeOverlayColorBox.Text = _edgeOverlaySettings.ColorHex;
        EdgeOverlayTopVisibleBox.IsChecked = _edgeOverlaySettings.TopVisible;
        EdgeOverlayBottomVisibleBox.IsChecked = _edgeOverlaySettings.BottomVisible;
        EdgeOverlayLeftVisibleBox.IsChecked = _edgeOverlaySettings.LeftVisible;
        EdgeOverlayRightVisibleBox.IsChecked = _edgeOverlaySettings.RightVisible;
        EdgeOverlayOpacitySlider.Value = Math.Clamp(_edgeOverlaySettings.Opacity, 0, 100);
        EdgeOverlayPerEdgeBox.IsChecked = _edgeOverlaySettings.UsePerEdgeOpacity;
        EdgeOverlayTopOpacitySlider.Value = Math.Clamp(_edgeOverlaySettings.TopOpacity, 0, 100);
        EdgeOverlayBottomOpacitySlider.Value = Math.Clamp(_edgeOverlaySettings.BottomOpacity, 0, 100);
        EdgeOverlayLeftOpacitySlider.Value = Math.Clamp(_edgeOverlaySettings.LeftOpacity, 0, 100);
        EdgeOverlayRightOpacitySlider.Value = Math.Clamp(_edgeOverlaySettings.RightOpacity, 0, 100);
        EdgeOverlayPerEdgePanel.IsEnabled = _edgeOverlaySettings.UsePerEdgeOpacity;
        EdgeOverlayPerEdgePanel.Visibility = _edgeOverlaySettings.UsePerEdgeOpacity ? Visibility.Visible : Visibility.Collapsed;
        EdgeOverlayOpacitySlider.IsEnabled = !_edgeOverlaySettings.UsePerEdgeOpacity;
        EdgeOverlayAspectRatioBox.IsEnabled = _edgeOverlaySettings.DisplayMode == EdgeOverlayDisplayMode.Stretch;
        EdgeOverlaySummaryText.Text = _edgeOverlaySettings.Enabled ? "已开启" : "已关闭";
        if (TryGetEdgeOverlayColor(EdgeOverlayColorBox.Text, out var color))
            EdgeOverlayColorPreview.Background = new SolidColorBrush(ParseColor(color));
        _updatingEdgeOverlayControls = false;
    }

    private void EdgeOverlayControls_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingEdgeOverlayControls) return;
        _edgeOverlaySettings.Enabled = EdgeOverlayEnabledBox.IsChecked == true;
        _edgeOverlaySettings.Shape = (EdgeOverlayShape)Math.Max(0, EdgeOverlayShapeBox.SelectedIndex);
        _edgeOverlaySettings.Size = Math.Max(0, EdgeOverlaySizeBox.SelectedIndex);
        _edgeOverlaySettings.Length = Math.Max(0, EdgeOverlayLengthBox.SelectedIndex);
        _edgeOverlaySettings.DisplayMode = (EdgeOverlayDisplayMode)Math.Max(0, EdgeOverlayDisplayModeBox.SelectedIndex);
        _edgeOverlaySettings.AspectRatio = (EdgeOverlayAspectRatio)Math.Max(0, EdgeOverlayAspectRatioBox.SelectedIndex);
        _edgeOverlaySettings.Split = (EdgeOverlaySplit)Math.Max(0, EdgeOverlaySplitBox.SelectedIndex);
        _edgeOverlaySettings.TopVisible = EdgeOverlayTopVisibleBox.IsChecked == true;
        _edgeOverlaySettings.BottomVisible = EdgeOverlayBottomVisibleBox.IsChecked == true;
        _edgeOverlaySettings.LeftVisible = EdgeOverlayLeftVisibleBox.IsChecked == true;
        _edgeOverlaySettings.RightVisible = EdgeOverlayRightVisibleBox.IsChecked == true;
        _edgeOverlaySettings.Opacity = (int)EdgeOverlayOpacitySlider.Value;
        _edgeOverlaySettings.UsePerEdgeOpacity = EdgeOverlayPerEdgeBox.IsChecked == true;
        _edgeOverlaySettings.TopOpacity = (int)EdgeOverlayTopOpacitySlider.Value;
        _edgeOverlaySettings.BottomOpacity = (int)EdgeOverlayBottomOpacitySlider.Value;
        _edgeOverlaySettings.LeftOpacity = (int)EdgeOverlayLeftOpacitySlider.Value;
        _edgeOverlaySettings.RightOpacity = (int)EdgeOverlayRightOpacitySlider.Value;
        EdgeOverlayPerEdgePanel.IsEnabled = _edgeOverlaySettings.UsePerEdgeOpacity;
        EdgeOverlayPerEdgePanel.Visibility = _edgeOverlaySettings.UsePerEdgeOpacity ? Visibility.Visible : Visibility.Collapsed;
        EdgeOverlayOpacitySlider.IsEnabled = !_edgeOverlaySettings.UsePerEdgeOpacity;
        EdgeOverlayAspectRatioBox.IsEnabled = _edgeOverlaySettings.DisplayMode == EdgeOverlayDisplayMode.Stretch;
        EdgeOverlaySummaryText.Text = _edgeOverlaySettings.Enabled ? "已开启" : "已关闭";
        ApplyEdgeOverlaySettings();
        try
        {
            SaveSettings();
            EdgeOverlayStatusText.Text = "更改已自动保存。启用后立即生效，叠加层不影响鼠标点击。";
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to save edge overlay settings.", ex);
            EdgeOverlayStatusText.Text = "设置暂未保存，请检查 data 目录是否可写。";
        }
    }

    private static bool TryGetEdgeOverlayColor(string value, out string color)
    {
        color = value.Trim().ToUpperInvariant();
        return color.Length == 7 && color[0] == '#' &&
            uint.TryParse(color.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);
    }

    private void EdgeOverlayColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingEdgeOverlayControls) return;
        if (!TryGetEdgeOverlayColor(EdgeOverlayColorBox.Text, out var color))
        {
            EdgeOverlayColorHint.Text = "请输入有效的六位十六进制颜色，例如 #00FF00。";
            return;
        }
        _edgeOverlaySettings.ColorHex = color;
        EdgeOverlayColorPreview.Background = new SolidColorBrush(ParseColor(color));
        EdgeOverlayColorHint.Text = "输入 #RRGGBB 格式的六位颜色值。";
        EdgeOverlayControls_Changed(sender, e);
    }

    private void EdgeOverlayColorPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color }) EdgeOverlayColorBox.Text = color;
    }

    private void ApplyEdgeOverlaySettings()
    {
        if (!_edgeOverlaySettings.Enabled)
        {
            _edgeOverlayWindow?.Close();
            _edgeOverlayWindow = null;
            return;
        }
        _edgeOverlayWindow ??= new EdgeOverlayWindow();
        _edgeOverlayWindow.ApplySettings(_edgeOverlaySettings);
    }

    private void ShowPreferencesHome()
    {
        PreferencesHomePanel.Visibility = Visibility.Visible;
        ThemeSettingsPanel.Visibility = Visibility.Collapsed;
        PreferencesSubtitleText.Text = "选择应用的外观主题。";
        CurrentThemeText.Text = $"当前：{ThemeDisplayName(_themeChoice)}  ›";
    }

    private void ThemeChoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string choice }) return;
        ApplyTheme(choice, true);
    }

    private void ConfigureThemeSelector()
    {
        foreach (var text in FindLogicalChildren<TextBlock>(PreferencesView))
        {
            if (text.Text == "切换夜航、晨雾或森林三款主题，立即应用到整个工作区。")
                text.Text = "切换银白、日式或死亡搁浅三款主题，立即应用到整个工作区。";
            else if (text.Text == "当前：夜航  ›") text.Text = "当前：银白  ›";
        }

        foreach (var button in FindLogicalChildren<Button>(ThemeSettingsPanel))
        {
            if (button.Tag is not string oldTag) continue;
            var stack = FindLogicalChildren<StackPanel>(button).FirstOrDefault();
            var preview = stack is null ? null : FindLogicalChildren<Border>(stack).FirstOrDefault();
            var labels = stack is null ? new List<TextBlock>() : FindLogicalChildren<TextBlock>(stack).ToList();
            if (oldTag == "Night") ConfigureThemeCard(button, preview, labels, "Silver", "银白", "中性白底，钛银与石墨灰", "#F3F4F6", "#545967");
            if (oldTag == "Mist") ConfigureThemeCard(button, preview, labels, "Japanese", "日式", "和纸暖白，朱红点睛", "#FFF9EE", "#A5282A");
            if (oldTag == "Forest") ConfigureThemeCard(button, preview, labels, "DeathStranding", "死亡搁浅", "雾灰地貌，电蓝连结", "#D9DEE0", "#276B8C");
        }
    }

    private static void ConfigureThemeCard(Button button, Border? preview, IReadOnlyList<TextBlock> labels, string tag, string title, string description, string background, string border)
    {
        button.Tag = tag;
        if (preview is not null) { preview.Tag = "ThemePreview"; preview.Background = new SolidColorBrush(ParseColor(background)); preview.BorderBrush = new SolidColorBrush(ParseColor(border)); }
        if (labels.Count > 0) labels[0].Text = title;
        if (labels.Count > 1) labels[1].Text = description;
    }

    private static IEnumerable<T> FindLogicalChildren<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in FindLogicalChildren<T>(child)) yield return descendant;
        }
    }

    private static string ThemeDisplayName(string choice) => choice switch
    {
        "Japanese" => "日式",
        "DeathStranding" => "死亡搁浅",
        _ => "银白"
    };

    private void ApplyTheme(string choice, bool save)
    {
        _themeChoice = choice is "Japanese" or "DeathStranding" ? choice : "Silver";
        var colors = _themeChoice switch
        {
            "Japanese" => new Dictionary<string, string>
            {
                ["WindowBackgroundBrush"] = "#F2EBDD", ["TopBarBrush"] = "#FBF7EE", ["SurfaceBrush"] = "#FFF9EE", ["SurfaceElevatedBrush"] = "#EEE2D0", ["InputBrush"] = "#FFFCF5", ["CanvasBrush"] = "#F7F0E4", ["BorderBrush"] = "#C9B9A4", ["StrongBorderBrush"] = "#9D8770", ["HoverBrush"] = "#E8D9C3", ["AccentBrush"] = "#A5282A", ["AccentHoverBrush"] = "#7E1C20", ["AccentTextBrush"] = "#FFF9EE", ["PrimaryTextBrush"] = "#2A2420", ["SecondaryTextBrush"] = "#57483E", ["MutedTextBrush"] = "#806F60", ["SubtleTextBrush"] = "#9A8877", ["OverlayBrush"] = "#8A241812", ["CanvasBorderBrush"] = "#A5282A", ["CloseHoverBrush"] = "#7E1C20", ["CloseHoverTextBrush"] = "#FFF4E7"
            },
            "DeathStranding" => new Dictionary<string, string>
            {
                ["WindowBackgroundBrush"] = "#D9DEE0", ["TopBarBrush"] = "#E7EAEB", ["SurfaceBrush"] = "#F0F1F0", ["SurfaceElevatedBrush"] = "#D1D6D8", ["InputBrush"] = "#F8F8F5", ["CanvasBrush"] = "#C8CED0", ["BorderBrush"] = "#909B9E", ["StrongBorderBrush"] = "#657377", ["HoverBrush"] = "#BEC7CA", ["AccentBrush"] = "#276B8C", ["AccentHoverBrush"] = "#174E69", ["AccentTextBrush"] = "#F7FBFC", ["PrimaryTextBrush"] = "#18242A", ["SecondaryTextBrush"] = "#36464D", ["MutedTextBrush"] = "#617177", ["SubtleTextBrush"] = "#7B898E", ["OverlayBrush"] = "#8A152027", ["CanvasBorderBrush"] = "#276B8C", ["CloseHoverBrush"] = "#A24B45", ["CloseHoverTextBrush"] = "#FFF7F5"
            },
            _ => new Dictionary<string, string>
            {
                ["WindowBackgroundBrush"] = "#F3F4F6", ["TopBarBrush"] = "#F7F8FA", ["SurfaceBrush"] = "#FFFFFF", ["SurfaceElevatedBrush"] = "#ECEEF2", ["InputBrush"] = "#FDFDFE", ["CanvasBrush"] = "#FAFAFC", ["BorderBrush"] = "#DEE1E7", ["StrongBorderBrush"] = "#C7CBD4", ["HoverBrush"] = "#E4E7EC", ["AccentBrush"] = "#545967", ["AccentHoverBrush"] = "#404654", ["AccentTextBrush"] = "#F8F9FC", ["PrimaryTextBrush"] = "#262A33", ["SecondaryTextBrush"] = "#596170", ["MutedTextBrush"] = "#6C7280", ["SubtleTextBrush"] = "#848B98", ["OverlayBrush"] = "#800C1018", ["CanvasBorderBrush"] = "#687183", ["CloseHoverBrush"] = "#FBE4E6", ["CloseHoverTextBrush"] = "#9F2534"
            }
        };

        var oldRoles = CurrentThemeRoles();
        foreach (var (key, value) in colors)
            Application.Current.Resources[key] = new SolidColorBrush(ParseColor(value));
        Background = (Brush)Application.Current.Resources["WindowBackgroundBrush"];
        Application.Current.Resources[SystemColors.WindowBrushKey] = Application.Current.Resources["InputBrush"];
        Application.Current.Resources[SystemColors.WindowTextBrushKey] = Application.Current.Resources["PrimaryTextBrush"];
        if (IsLoaded) RethemeVisualTree(this, oldRoles, colors);
        if (_currentNote is not null) ApplyNoteTextTheme();
        CurrentThemeText?.SetCurrentValue(TextBlock.TextProperty, $"当前：{ThemeDisplayName(_themeChoice)}  ›");
        if (save)
        {
            try { SaveSettings(); }
            catch (Exception ex) { AppLog.Error("Failed to save theme setting.", ex); }
        }
    }

    private static Color ParseColor(string value) => ColorConverter.ConvertFromString(value) is Color color
        ? color : throw new InvalidOperationException($"Invalid theme color: {value}");

    private void ApplyThemeToVisualTree()
    {
        var colors = Application.Current.Resources.OfType<DictionaryEntry>()
            .Where(item => item.Key is string && item.Value is SolidColorBrush)
            .ToDictionary(item => (string)item.Key, item => ((SolidColorBrush)item.Value).Color.ToString());
        RethemeVisualTree(this, CurrentThemeRoles(), colors);
    }

    private static Dictionary<Color, string> CurrentThemeRoles()
    {
        var roles = Application.Current.Resources.OfType<DictionaryEntry>()
            .Where(item => item.Key is string && item.Value is SolidColorBrush)
            .GroupBy(item => ((SolidColorBrush)item.Value).Color)
            .ToDictionary(group => group.Key, group => (string)group.First().Key);
        foreach (var (hex, role) in new[]
        {
            ("#101113", "WindowBackgroundBrush"), ("#151719", "SurfaceBrush"), ("#1C1F21", "SurfaceElevatedBrush"), ("#111315", "CanvasBrush"),
            ("#292D30", "BorderBrush"), ("#34383C", "StrongBorderBrush"), ("#25282B", "HoverBrush"), ("#F4F5F5", "PrimaryTextBrush"),
            ("#F0F1F1", "PrimaryTextBrush"), ("#F5F6F6", "PrimaryTextBrush"), ("#E8EAEB", "PrimaryTextBrush"), ("#E7E9EA", "PrimaryTextBrush"), ("#E4E7E8", "PrimaryTextBrush"),
            ("#E1E4E5", "PrimaryTextBrush"), ("#D9DCDE", "PrimaryTextBrush"), ("#A8AEB2", "SecondaryTextBrush"), ("#A7ACB0", "SecondaryTextBrush"),
            ("#A1A7AB", "SecondaryTextBrush"), ("#9EA5A9", "SecondaryTextBrush"), ("#838A8F", "MutedTextBrush"), ("#858C91", "MutedTextBrush"),
            ("#7C8388", "MutedTextBrush"), ("#767D82", "MutedTextBrush"), ("#73787D", "MutedTextBrush"), ("#6D7479", "MutedTextBrush"),
            ("#626A70", "SubtleTextBrush"), ("#DDE1E3", "AccentBrush"), ("#5AB2FF", "CanvasBorderBrush"), ("#3A4248", "CanvasBorderBrush"),
            ("#9AA1A5", "SecondaryTextBrush"), ("#747C81", "MutedTextBrush"), ("#181A1C", "SurfaceBrush"), ("#1E2123", "SurfaceElevatedBrush"),
            ("#121416", "WindowBackgroundBrush"), ("#CC1C1F21", "SurfaceElevatedBrush"), ("#2A2D30", "BorderBrush"), ("#303438", "BorderBrush"), ("#363A3E", "StrongBorderBrush"),
            ("#F3F4F4", "PrimaryTextBrush"), ("#F5F6F5", "PrimaryTextBrush"), ("#A4AAAE", "SecondaryTextBrush"), ("#A7ADB1", "SecondaryTextBrush"), ("#A9B5C0", "SecondaryTextBrush"),
            ("#454B50", "MutedTextBrush"), ("#727A7F", "MutedTextBrush"), ("#747A7F", "MutedTextBrush"), ("#777E83", "MutedTextBrush"), ("#858B90", "MutedTextBrush"), ("#8A9196", "MutedTextBrush")
        }) roles[ParseColor(hex)] = role;
        return roles;
    }

    private static void RethemeVisualTree(DependencyObject root, IReadOnlyDictionary<Color, string> roles, IReadOnlyDictionary<string, string> colors)
    {
        if (root is FrameworkElement { Tag: "ThemePreview" }) return;
        Recolor(root, roles, colors);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) RethemeVisualTree(VisualTreeHelper.GetChild(root, i), roles, colors);
    }

    private static Brush Recolor(Brush brush, IReadOnlyDictionary<Color, string> roles, IReadOnlyDictionary<string, string> colors) => brush is SolidColorBrush solid && roles.TryGetValue(solid.Color, out var role) && colors.TryGetValue(role, out var next) ? new SolidColorBrush(ParseColor(next)) : brush;

    private static void Recolor(DependencyObject element, IReadOnlyDictionary<Color, string> roles, IReadOnlyDictionary<string, string> colors)
    {
        if (element is Panel panel) panel.Background = Recolor(panel.Background, roles, colors);
        if (element is Border border) { border.Background = Recolor(border.Background, roles, colors); border.BorderBrush = Recolor(border.BorderBrush, roles, colors); }
        if (element is Control control) { control.Background = Recolor(control.Background, roles, colors); control.Foreground = Recolor(control.Foreground, roles, colors); control.BorderBrush = Recolor(control.BorderBrush, roles, colors); }
        if (element is TextBlock text) text.Foreground = Recolor(text.Foreground, roles, colors);
        if (element is System.Windows.Shapes.Shape shape) { shape.Fill = Recolor(shape.Fill, roles, colors); shape.Stroke = Recolor(shape.Stroke, roles, colors); }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (CrosshairView.Visibility == Visibility.Visible && CloseCrosshairDropdown())
        {
            e.Handled = true;
            return;
        }

        if (ConnectionOverlay.Visibility == Visibility.Visible)
        {
            ConnectionOverlay.Visibility = Visibility.Collapsed;
        }
        else if (EditorView.Visibility == Visibility.Visible)
        {
            SaveCurrentNote();
            EditorView.Visibility = Visibility.Collapsed;
            NotesView.Visibility = Visibility.Visible;
            NotesEmptyView.Visibility = Visibility.Visible;
            SetActiveNavigation(ArchiveButton);
            RefreshNoteCards();
        }
        else if (TaskDetailView.Visibility == Visibility.Visible)
        {
            TaskDetailView.Visibility = Visibility.Collapsed;
            TasksView.Visibility = Visibility.Visible;
            SetActiveNavigation(MindMapNavButton);
            RefreshTaskCards();
        }
        else if (MindMapEditorView.Visibility == Visibility.Visible)
        {
            SaveMindMaps();
            MindMapEditorView.Visibility = Visibility.Collapsed;
            MindMapView.Visibility = Visibility.Visible;
            SetActiveNavigation(MindMapNavButton);
            RefreshMindMapCards();
        }
        else if (NotesView.Visibility == Visibility.Visible ||
                 TasksView.Visibility == Visibility.Visible ||
                 TrashView.Visibility == Visibility.Visible ||
                 PreferencesView.Visibility == Visibility.Visible ||
                 EdgeOverlayView.Visibility == Visibility.Visible ||
                 CrosshairView.Visibility == Visibility.Visible ||
                 DeviceView.Visibility == Visibility.Visible)
        {
            ShowHome();
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AddressComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded) UpdateQr();
    }

    private void UpdateQr()
    {
        if (AddressComboBox.SelectedItem is not LocalEndpoint endpoint) return;
        RenderQr($"landrop://pair?host={Uri.EscapeDataString(endpoint.Address)}&port={_port}&token={Uri.EscapeDataString(_token)}&device={Uri.EscapeDataString(Environment.MachineName)}&deviceId={Uri.EscapeDataString(_deviceId)}");
    }

    private async Task SendFrameAsync(FrameHeader frame, Stream? payload, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new IOException("设备尚未连接");
        var json = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
        if (json.Length > 1024 * 1024) throw new InvalidDataException("消息头过大");

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            var prefix = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(prefix, json.Length);
            await stream.WriteAsync(prefix, cancellationToken);
            await stream.WriteAsync(json, cancellationToken);
            if (payload != null) await payload.CopyToAsync(stream, 81920, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<FrameHeader> ReadFrameHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await ReadExactlyAsync(stream, prefix, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length <= 0 || length > 1024 * 1024) throw new InvalidDataException("消息头长度无效");
        var json = new byte[length];
        await ReadExactlyAsync(stream, json, cancellationToken);
        return JsonSerializer.Deserialize<FrameHeader>(json, JsonOptions)
            ?? throw new InvalidDataException("无法解析消息头");
    }

    private static async Task ReadExactlyAsync(Stream input, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await input.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new EndOfStreamException("连接已断开");
            offset += read;
        }
    }

    private async Task CopyFileWithProgressAsync(
        Stream input, Stream output, string fileName, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var remaining = length;
        var received = 0L;
        var lastUpdate = Stopwatch.GetTimestamp();
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) throw new EndOfStreamException("文件传输中断");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
            received += read;

            var now = Stopwatch.GetTimestamp();
            if (received == length || Stopwatch.GetElapsedTime(lastUpdate, now) >= TimeSpan.FromMilliseconds(80))
            {
                UpdateReceiveProgress(fileName, received, length, "正在接收");
                lastUpdate = now;
            }
        }

        if (length == 0) UpdateReceiveProgress(fileName, 0, 0, "正在接收");
    }

    private void UpdateReceiveProgress(string fileName, long received, long total, string status) => Dispatcher.Invoke(() =>
    {
        ProgressFileText.Text = fileName;
        ProgressBytesText.Text = $"{FormatBytes(received)} / {FormatBytes(total)}";
        ReceiveProgressBar.Value = total == 0 ? (status == "接收完成" ? 100 : 0) : Math.Clamp(received * 100d / total, 0, 100);
        ProgressStatusText.Text = status;
        ProgressStatusText.Foreground = status == "接收失败" ? new SolidColorBrush(Color.FromRgb(159, 37, 52)) : (Brush)FindResource(status == "接收完成" ? "AccentBrush" : "SecondaryTextBrush");
    });

    private void RenderQr(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        using var memory = new MemoryStream(png);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = memory;
        image.EndInit();
        image.Freeze();
        QrImage.Source = image;
    }

    private static List<LocalEndpoint> GetLocalAddresses(int port)
    {
        var results = new List<LocalEndpoint>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                 n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel))
        {
            var properties = network.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var address in properties.UnicastAddresses
                         .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)))
            {
                var value = address.Address.ToString();
                results.Add(new LocalEndpoint(value, $"{network.Name}  ·  {value}:{port}", hasGateway,
                    network.NetworkInterfaceType == NetworkInterfaceType.Wireless80211));
            }
        }

        var ordered = results
            .OrderByDescending(x => x.IsPrivate)
            .ThenByDescending(x => x.HasGateway)
            .ThenByDescending(x => x.IsWireless)
            .ThenBy(x => x.DisplayName)
            .DistinctBy(x => x.Address)
            .ToList();
        return ordered.Count > 0
            ? ordered
            : [new LocalEndpoint("127.0.0.1", $"未找到局域网地址  ·  127.0.0.1:{port}", false, false)];
    }

    private static bool IsPrivateAddress(string value) =>
        value.StartsWith("10.") || value.StartsWith("192.168.") ||
        (value.StartsWith("172.") && int.TryParse(value.Split('.')[1], out var part) && part is >= 16 and <= 31);

    private static string GetUniquePath(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            path = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(path)) return path;
        }
    }

    private static bool CryptographicEquals(string? left, string right)
    {
        if (left is null) return false;
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string ComputeDiscoveryProof(string pairKey, string nonce)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(Convert.FromHexString(pairKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(nonce)));
    }

    private void SetStatus(string text, bool connected, bool error = false) => Dispatcher.Invoke(() =>
    {
        StatusText.Text = text;
        HomeStatusText.Text = text;
        var brush = error ? new SolidColorBrush(Color.FromRgb(159, 37, 52)) : (Brush)FindResource(connected ? "AccentBrush" : "SecondaryTextBrush");
        StatusText.Foreground = brush;
        StatusDot.Fill = brush;
        HomeStatusText.Foreground = brush;
        HomeFooterDot.Fill = brush;
        StatusCard.SetResourceReference(Border.BackgroundProperty, "SurfaceElevatedBrush");
        StatusCard.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
    });

    private void AppendLog(string text) => Dispatcher.Invoke(() =>
    {
        LogText.Text = string.IsNullOrWhiteSpace(LogText.Text) ? text : $"{LogText.Text}\n{text}";
        DeviceLogText.Text = LogText.Text;
        OverlayRecentText.Text = text;
        RecentStatusText.Text = text.Contains("已连接") ? "设备已连接" : "正在等待设备";
    });

    private void HideViews()
    {
        HomeView.Visibility = Visibility.Collapsed;
        NotesView.Visibility = Visibility.Collapsed;
        TasksView.Visibility = Visibility.Collapsed;
        TaskDetailView.Visibility = Visibility.Collapsed;
        TrashView.Visibility = Visibility.Collapsed;
        MindMapView.Visibility = Visibility.Collapsed;
        MindMapEditorView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Collapsed;
        NotesEmptyView.Visibility = Visibility.Collapsed;
        DeviceView.Visibility = Visibility.Collapsed;
        PreferencesView.Visibility = Visibility.Collapsed;
        EdgeOverlayView.Visibility = Visibility.Collapsed;
        CrosshairView.Visibility = Visibility.Collapsed;
        ConnectionOverlay.Visibility = Visibility.Collapsed;
    }

    private void ShowHome()
    {
        HideViews();
        HomeView.Visibility = Visibility.Visible;
        SetActiveNavigation(RecentNavButton);
    }

    private void OpenDeviceView()
    {
        HideViews();
        DeviceView.Visibility = Visibility.Visible;
        DeviceLogText.Text = LogText.Text;
        SetActiveNavigation(RecentNavButton);
    }

    private void ConfigureTitleBar()
    {
        foreach (var button in FindLogicalChildren<Button>(this))
        {
            if (button.Content?.ToString() == "返回首页") button.Visibility = Visibility.Collapsed;
            if (button.Content?.ToString() == "V") button.Click += (_, _) => ShowHome();
        }
    }

    private void SetActiveNavigation(Button active)
    {
        WorkspaceTitleText.Text = System.Windows.Automation.AutomationProperties.GetName(active);
        foreach (var button in new[] { RecentNavButton, ArchiveButton, MindMapNavButton, EdgeOverlayNavButton, CrosshairNavButton, TrashNavButton, PreferencesNavButton })
        {
            button.Style = (Style)FindResource("NavButton");
            button.ClearValue(Control.BackgroundProperty);
            button.ClearValue(Control.ForegroundProperty);
            button.ClearValue(Control.FontWeightProperty);
        }
        active.Style = (Style)FindResource("ActiveNavButton");
        active.ClearValue(Control.BackgroundProperty);
        active.ClearValue(Control.ForegroundProperty);
        active.ClearValue(Control.FontWeightProperty);
    }

    private void HideSidebarBranding()
    {
        foreach (var text in FindLogicalChildren<TextBlock>(this))
            if (text.Text is "局 域 投 送" or "本地网络传输") text.Visibility = Visibility.Collapsed;
    }

    private void StopEverything()
    {
        _edgeOverlayWindow?.Close();
        _edgeOverlayWindow = null;
        _crosshairWindow?.Close();
        _crosshairWindow = null;
        _shutdown.Cancel();
        _discoveryClient?.Dispose();
        _client?.Dispose();
        _listener?.Stop();
        _shutdown.Dispose();
        _sendLock.Dispose();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:F1} 吉字节",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:F1} 兆字节",
        >= 1024 => $"{bytes / 1024d:F1} 千字节",
        _ => $"{bytes} 字节"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed record FrameHeader(
        string Type,
        string? Token,
        string? Name,
        long PayloadLength,
        string? Message,
        string? DeviceId,
        string? DeviceName = null);

    private sealed record AppSettings(
        string? ReceiveDirectory,
        string ThemeChoice = "Silver",
        string? DeviceId = null,
        Dictionary<string, string>? TrustedDeviceKeys = null,
        EdgeOverlaySettings? EdgeOverlay = null,
        CrosshairSettings? Crosshair = null);

    private sealed record DiscoveryPacket(string Type, string? DeviceId, string? DeviceName, int Port, string? Nonce);

    private sealed record PendingDiscovery(string Nonce, DateTimeOffset ExpiresAt);

    private sealed record SyncNote(
        string Id,
        string? Title,
        string? Content,
        long UpdatedAt,
        int? FontSize = null,
        double? LineSpacing = null,
        double? LetterSpacing = null);

    private sealed record SyncEnvelope(List<SyncNote>? Notes, List<WorkoutTask>? Tasks);

    private sealed class NoteRecord
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "未命名记录";
        public string ContentXaml { get; set; } = "";
        public string Preview { get; set; } = "";
        public int FontSize { get; set; } = DefaultNoteFontSize;
        public double LineSpacing { get; set; } = DefaultNoteLineSpacing;
        public double LetterSpacing { get; set; } = DefaultNoteLetterSpacing;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
    }

    private sealed class WorkoutTask
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "未命名训练";
        public string SourceProject { get; set; } = "D";
        public string Status { get; set; } = "pending";
        public string PreviousStatus { get; set; } = "";
        public string ScheduledDate { get; set; } = "";
        public int EstimatedMinutes { get; set; }
        public string Notes { get; set; } = "";
        public string? Effort { get; set; }
        public string? Discomfort { get; set; }
        public string? DiscomfortNotes { get; set; }
        public string? FeedbackNotes { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }
        public long StartedAt { get; set; }
        public long CompletedAt { get; set; }
        public long DeletedAt { get; set; }
        public List<WorkoutExercise> Exercises { get; set; } = [];
    }

    private sealed class WorkoutExercise
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "未命名动作";
        public int RestSeconds { get; set; } = 90;
        public string Notes { get; set; } = "";
        public bool IsAdditional { get; set; }
        public string? SkipReason { get; set; }
        public List<WorkoutSet> Sets { get; set; } = [];
    }

    private sealed class WorkoutSet
    {
        public int Index { get; set; }
        public double TargetWeight { get; set; }
        public int TargetReps { get; set; }
        public double ActualWeight { get; set; }
        public int ActualReps { get; set; }
        public bool Completed { get; set; }
        public long CompletedAt { get; set; }
        public string? Rir { get; set; }
        public string? MovementQuality { get; set; }
        public string? Discomfort { get; set; }
        public string? DiscomfortNotes { get; set; }
    }

    private sealed class MindMapRecord
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "未命名导图";
        public DateTimeOffset UpdatedAt { get; set; }
        public double CanvasWidth { get; set; } = 1200;
        public double CanvasHeight { get; set; } = 800;
        public double CanvasLeft { get; set; } = 260;
        public double CanvasTop { get; set; } = 180;
        public DateTimeOffset? DeletedAt { get; set; }
        public List<MindMapNode> Nodes { get; set; } = [];
    }

    private sealed class MindMapNode
    {
        public string Id { get; set; } = "";
        public string? ParentId { get; set; }
        public string Text { get; set; } = "新节点";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 150;
        public double Height { get; set; } = 44;
        public string Color { get; set; } = "#313A44";
    }

    private sealed record LocalEndpoint(string Address, string DisplayName, bool HasGateway, bool IsWireless)
    {
        public bool IsPrivate => IsPrivateAddress(Address);
    }
}
