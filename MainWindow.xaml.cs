using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudePetOverlay.Models;
using ClaudePetOverlay.Services;

namespace ClaudePetOverlay;

public partial class MainWindow : System.Windows.Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const double CellWidth = 192;
    private const double CellHeight = 208;
    private const double NormalSourceWidth = 576;
    private const double NormalSourceHeight = 624;
    private const int RecentTaskLimit = 5;

    // ユーザーがアニメ差し替え (CustomFrames) と通知音 (Sounds) を置く場所。
    private static readonly string ConfigRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudePetOverlay");
    private static readonly string CustomFramesRoot = Path.Combine(ConfigRoot, "CustomFrames");
    private static readonly string SoundsRoot = Path.Combine(ConfigRoot, "Sounds");
    private static readonly string[] SoundExtensions = [".wav", ".mp3", ".wma"];

    private readonly AnimationPlayer _animation = new();
    private readonly bool _qaMode;
    private readonly PetSettings _settings = PetSettings.Load();
    private readonly PetEventBusWatcher _eventBusWatcher = new();
    private readonly DispatcherTimer _badgeTimer;
    private readonly DispatcherTimer _temporaryStateTimer;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.ContextMenuStrip _trayMenu;
    private readonly List<TaskHistoryEntry> _recentTasks = [];
    private readonly Dictionary<string, int> _activeTasksBySource = new(StringComparer.OrdinalIgnoreCase);
    private SpeechBubbleWindow? _speechBubble;
    private System.Windows.Forms.ToolStripMenuItem? _recentTasksMenu;
    private System.Windows.Forms.ToolStripMenuItem? _replayReportMenuItem;
    private ActivityUpdate? _lastBubbleUpdate;
    private readonly MediaPlayer _soundPlayer = new();
    private PetState? _lastSoundState;
    private DateTime _lastSoundAtUtc = DateTime.MinValue;
    private TimeSpan _lastRenderTime;
    private PetState _activityState = PetState.Idle;
    private bool _dragging;
    private System.Drawing.Point _dragStartCursor;
    private System.Drawing.Point _pendingDragCursor;
    private double _dragStartLeft;
    private double _dragStartTop;
    private PetState _dragDirection = PetState.RunningRight;

    public MainWindow(bool qaMode = false)
    {
        _qaMode = qaMode;
        InitializeComponent();
        if (_qaMode)
        {
            ShowInTaskbar = true;
        }

        _badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _badgeTimer.Tick += (_, _) =>
        {
            _badgeTimer.Stop();
            StatusBadge.Visibility = System.Windows.Visibility.Collapsed;
        };

        _temporaryStateTimer = new DispatcherTimer();
        _temporaryStateTimer.Tick += (_, _) =>
        {
            _temporaryStateTimer.Stop();
            SetDisplayedState(_activityState, _activityState == PetState.Idle ? "待機中" : null);
        };

        _trayMenu = BuildTrayMenu();
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Claude Pet Overlay",
            Visible = true,
            ContextMenuStrip = _trayMenu,
        };
        _trayIcon.DoubleClick += (_, _) => ToggleClickThrough(false);

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonUp += (_, _) => _trayMenu.Show(System.Windows.Forms.Cursor.Position);
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CustomFramesRoot);
            Directory.CreateDirectory(SoundsRoot);
            var framesRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Frames");
            _animation.Load(framesRoot, CustomFramesRoot);
            _ = PreloadDragAnimationsAsync();
            ApplyScale(_settings.Scale, save: false);
            Topmost = _settings.Topmost;
            RestorePosition();
            ToggleClickThrough(_settings.ClickThrough, save: false);
            _speechBubble = new SpeechBubbleWindow(this, _qaMode)
            {
                Enabled = _settings.ShowSpeechBubble,
                Topmost = Topmost,
            };
            SetDisplayedState(PetState.Idle, $"待機中 · {_settings.TargetFps}fps");

            _eventBusWatcher.ActivityChanged += OnActivityChanged;
            _eventBusWatcher.Start();
        }
        catch (Exception exception)
        {
            SetDisplayedState(PetState.Failed, $"起動エラー: {exception.Message}");
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (_qaMode)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExToolWindow));
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs rendering || !IsLoaded)
        {
            return;
        }

        var minInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _settings.TargetFps));
        if (_lastRenderTime != TimeSpan.Zero && rendering.RenderingTime - _lastRenderTime < minInterval)
        {
            return;
        }
        _lastRenderTime = rendering.RenderingTime;

        if (_dragging)
        {
            ApplyPendingDragPosition();
        }

        var frame = _animation.GetFrame(rendering.RenderingTime, smoothInterpolation: false);
        FrameA.Source = frame.Current;
        FrameB.Source = null;
        FrameA.Opacity = 1;
        FrameB.Opacity = 0;
    }

    private void OnActivityChanged(ActivityUpdate update)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _temporaryStateTimer.Stop();
            var totalActiveTasks = UpdateActiveTaskCounts(update);
            var displayUpdate = update with { ActiveTaskCount = totalActiveTasks };
            var transient = update.State is PetState.Jumping or PetState.Failed or PetState.Waving;
            _activityState = transient
                ? totalActiveTasks > 0 ? PetState.Running : PetState.Idle
                : update.State == PetState.Idle && totalActiveTasks > 0 ? PetState.Running : update.State;
            var displayedState = transient ? update.State : _activityState;
            SetDisplayedState(displayedState, update.Message ?? $"{update.Source}: {update.State.DisplayName()}");
            _speechBubble?.ShowActivity(displayUpdate);
            PlayStateSound(update.State);

            if (update.State is PetState.Jumping or PetState.Failed
                && update.ShowInSpeechBubble
                && !string.IsNullOrWhiteSpace(update.Message))
            {
                _lastBubbleUpdate = displayUpdate;
                if (_replayReportMenuItem is not null)
                {
                    _replayReportMenuItem.Enabled = true;
                }
            }

            if (update.State is PetState.Jumping or PetState.Failed)
            {
                AddRecentTask(displayUpdate);
            }

            if (update.State == PetState.Jumping)
            {
                StartTemporaryState(_animation.DurationSeconds(PetState.Jumping));
            }
            else if (update.State == PetState.Failed)
            {
                StartTemporaryState(Math.Max(5, _animation.DurationSeconds(PetState.Failed)));
            }
            else if (update.State == PetState.Waving)
            {
                StartTemporaryState(_animation.DurationSeconds(PetState.Waving));
            }
        });
    }

    private int UpdateActiveTaskCounts(ActivityUpdate update)
    {
        var source = string.IsNullOrWhiteSpace(update.Source) ? "Unknown" : update.Source.Trim();
        if (update.State is PetState.Running or PetState.Review or PetState.Waiting)
        {
            _activeTasksBySource[source] = Math.Max(1, update.ActiveTaskCount);
        }
        else if (update.State is PetState.Idle or PetState.Jumping or PetState.Failed)
        {
            if (update.ActiveTaskCount > 0)
            {
                _activeTasksBySource[source] = update.ActiveTaskCount;
            }
            else
            {
                _activeTasksBySource.Remove(source);
            }
        }

        return _activeTasksBySource.Values.Sum();
    }

    private void StartTemporaryState(double seconds)
    {
        _temporaryStateTimer.Interval = TimeSpan.FromSeconds(seconds);
        _temporaryStateTimer.Start();
    }

    private void PlayStateSound(PetState state)
    {
        if (!_settings.SoundEnabled)
        {
            return;
        }
        var file = FindSoundFile(state);
        if (file is null)
        {
            return;
        }
        // running 等の高頻度イベントで連打しない: 同一状態は 3 秒のクールダウン。
        var now = DateTime.UtcNow;
        if (_lastSoundState == state && now - _lastSoundAtUtc < TimeSpan.FromSeconds(3))
        {
            return;
        }
        _lastSoundState = state;
        _lastSoundAtUtc = now;
        try
        {
            _soundPlayer.Open(new Uri(file));
            _soundPlayer.Volume = 1.0;
            _soundPlayer.Play();
        }
        catch (Exception)
        {
            // 壊れた音声ファイルでペットを止めない。
        }
    }

    private static string? FindSoundFile(PetState state)
    {
        foreach (var extension in SoundExtensions)
        {
            var path = Path.Combine(SoundsRoot, state.AssetFolder() + extension);
            if (File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }

    private void ReloadAssets()
    {
        try
        {
            var framesRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Frames");
            _animation.Load(framesRoot, CustomFramesRoot);
            _ = PreloadDragAnimationsAsync();
            SetDisplayedState(_activityState, "素材を再読み込みしました");
        }
        catch (Exception exception)
        {
            SetDisplayedState(PetState.Failed, $"再読み込み失敗: {exception.Message}");
        }
    }

    private void OpenConfigFolder()
    {
        try
        {
            Directory.CreateDirectory(ConfigRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", ConfigRoot) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetDisplayedState(_activityState, $"フォルダを開けません: {exception.Message}");
        }
    }

    private void SetDisplayedState(PetState state, string? message = null)
    {
        _animation.SetState(state);
        StatusText.Text = string.IsNullOrWhiteSpace(message) ? state.DisplayName() : message;
        StatusBadge.Visibility = System.Windows.Visibility.Visible;
        _badgeTimer.Stop();
        _badgeTimer.Start();
        _trayIcon.Text = TruncateTrayText($"Claude Pet · {state.DisplayName()}");
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ReplayReport(_lastBubbleUpdate);
            return;
        }

        _dragging = true;
        _dragStartCursor = System.Windows.Forms.Cursor.Position;
        _pendingDragCursor = _dragStartCursor;
        _dragStartLeft = Left;
        _dragStartTop = Top;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _pendingDragCursor = System.Windows.Forms.Cursor.Position;
        e.Handled = true;
    }

    private void ApplyPendingDragPosition()
    {
        var cursor = _pendingDragCursor;
        var dpi = VisualTreeHelper.GetDpi(this);
        var dx = (cursor.X - _dragStartCursor.X) / dpi.DpiScaleX;
        var dy = (cursor.Y - _dragStartCursor.Y) / dpi.DpiScaleY;
        Left = _dragStartLeft + dx;
        Top = _dragStartTop + dy;

        var direction = dx < 0 ? PetState.RunningLeft : PetState.RunningRight;
        if (_animation.IsCached(direction)
            && (direction != _dragDirection
                || _animation.State is not (PetState.RunningLeft or PetState.RunningRight)))
        {
            _dragDirection = direction;
            SetDisplayedState(direction);
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _pendingDragCursor = System.Windows.Forms.Cursor.Position;
        ApplyPendingDragPosition();
        _dragging = false;
        ReleaseMouseCapture();
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
        SetDisplayedState(_activityState);
        e.Handled = true;
    }

    private async Task PreloadDragAnimationsAsync()
    {
        try
        {
            await _animation.PreloadAsync(PetState.RunningRight, PetState.RunningLeft);
        }
        catch
        {
            // Dragging remains responsive with the current frame if preloading fails.
        }
    }

    private System.Windows.Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        _replayReportMenuItem = new System.Windows.Forms.ToolStripMenuItem("最新の報告を再表示")
        {
            Enabled = false,
        };
        _replayReportMenuItem.Click += (_, _) => ReplayReport(_lastBubbleUpdate);
        menu.Items.Add(_replayReportMenuItem);

        _recentTasksMenu = new System.Windows.Forms.ToolStripMenuItem("最近の完了報告");
        _recentTasksMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("まだ報告はありません")
        {
            Enabled = false,
        });
        menu.Items.Add(_recentTasksMenu);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var states = new System.Windows.Forms.ToolStripMenuItem("モーション");
        AddStateItem(states, "待機", PetState.Idle);
        AddStateItem(states, "作業中", PetState.Running);
        AddStateItem(states, "入力待ち", PetState.Waiting);
        AddStateItem(states, "確認中", PetState.Review);
        AddStateItem(states, "完了", PetState.Jumping);
        AddStateItem(states, "失敗", PetState.Failed);
        AddStateItem(states, "手を振る", PetState.Waving);
        menu.Items.Add(states);

        var fps = new System.Windows.Forms.ToolStripMenuItem("表示FPS");
        foreach (var value in new[] { 8, 16, 30, 32, 60, 120 })
        {
            var item = new System.Windows.Forms.ToolStripMenuItem($"{value} fps") { Tag = value };
            item.Click += (_, _) =>
            {
                _settings.TargetFps = value;
                _settings.Save();
                UpdateMenuChecks();
                SetDisplayedState(_activityState, $"表示 {value}fps");
            };
            fps.DropDownItems.Add(item);
        }
        menu.Items.Add(fps);

        var scale = new System.Windows.Forms.ToolStripMenuItem("表示倍率（元素材基準）");
        foreach (var value in new[] { 1.5, 2.0, 2.5, 3.0 })
        {
            var sourceScale = CellWidth * value / NormalSourceWidth;
            var item = new System.Windows.Forms.ToolStripMenuItem($"{sourceScale:0.00}×")
            {
                Tag = value,
                ToolTipText = $"通常元素材 {NormalSourceWidth:0}×{NormalSourceHeight:0}px に対する表示倍率",
            };
            item.Click += (_, _) => ApplyScale(value);
            scale.DropDownItems.Add(item);
        }
        menu.Items.Add(scale);

        var topmost = new System.Windows.Forms.ToolStripMenuItem("常に最前面") { CheckOnClick = true };
        topmost.Click += (_, _) =>
        {
            Topmost = topmost.Checked;
            if (_speechBubble is not null)
            {
                _speechBubble.Topmost = Topmost;
            }
            _settings.Topmost = Topmost;
            _settings.Save();
        };
        menu.Items.Add(topmost);

        var speechBubble = new System.Windows.Forms.ToolStripMenuItem("吹き出しを表示") { CheckOnClick = true };
        speechBubble.Click += (_, _) =>
        {
            _settings.ShowSpeechBubble = speechBubble.Checked;
            if (_speechBubble is not null)
            {
                _speechBubble.Enabled = speechBubble.Checked;
            }
            _settings.Save();
        };
        menu.Items.Add(speechBubble);

        var clickThrough = new System.Windows.Forms.ToolStripMenuItem("クリックを背面へ通す") { CheckOnClick = true };
        clickThrough.Click += (_, _) => ToggleClickThrough(clickThrough.Checked);
        menu.Items.Add(clickThrough);

        var sound = new System.Windows.Forms.ToolStripMenuItem("通知音を鳴らす") { CheckOnClick = true };
        sound.Click += (_, _) =>
        {
            _settings.SoundEnabled = sound.Checked;
            _settings.Save();
        };
        menu.Items.Add(sound);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("設定フォルダを開く (素材/音)", null, (_, _) => OpenConfigFolder());
        menu.Items.Add("素材を再読み込み", null, (_, _) => ReloadAssets());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => Close());
        menu.Opening += (_, _) =>
        {
            UpdateRecentTasksMenu();
            UpdateMenuChecks();
        };
        return menu;
    }

    private void AddRecentTask(ActivityUpdate update)
    {
        var completedAt = update.EndedAt ?? DateTimeOffset.Now;
        if (_recentTasks.FirstOrDefault() is { } latest
            && latest.Update.ThreadId == update.ThreadId
            && latest.Update.TaskName == update.TaskName
            && Math.Abs((latest.CompletedAt - completedAt).TotalSeconds) < 1)
        {
            return;
        }

        _recentTasks.Insert(0, new TaskHistoryEntry(update, completedAt));
        if (_recentTasks.Count > RecentTaskLimit)
        {
            _recentTasks.RemoveRange(RecentTaskLimit, _recentTasks.Count - RecentTaskLimit);
        }
    }

    private void UpdateRecentTasksMenu()
    {
        if (_recentTasksMenu is null)
        {
            return;
        }

        _recentTasksMenu.DropDownItems.Clear();
        if (_recentTasks.Count == 0)
        {
            _recentTasksMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("まだ報告はありません")
            {
                Enabled = false,
            });
            return;
        }

        foreach (var entry in _recentTasks)
        {
            var mark = entry.Update.State == PetState.Failed ? "!" : "✓";
            var taskName = string.IsNullOrWhiteSpace(entry.Update.TaskName)
                ? entry.Update.Source
                : entry.Update.TaskName;
            var item = new System.Windows.Forms.ToolStripMenuItem(
                $"{mark} {entry.CompletedAt.ToLocalTime():HH:mm} {TruncateMenuText(taskName, 34)}")
            {
                ToolTipText = entry.Update.Message ?? string.Empty,
            };
            var report = entry.Update;
            item.Click += (_, _) => ReplayReport(report);
            _recentTasksMenu.DropDownItems.Add(item);
        }
    }

    private void ReplayReport(ActivityUpdate? update)
    {
        if (update is null)
        {
            SetDisplayedState(_activityState, "再表示できる報告はまだありません");
            return;
        }
        if (!_settings.ShowSpeechBubble)
        {
            SetDisplayedState(_activityState, "吹き出し表示がオフです");
            return;
        }

        _speechBubble?.ShowActivity(update with { ShowInSpeechBubble = true });
    }

    private void AddStateItem(System.Windows.Forms.ToolStripMenuItem parent, string label, PetState state)
    {
        parent.DropDownItems.Add(label, null, (_, _) =>
        {
            _temporaryStateTimer.Stop();
            _activityState = state;
            SetDisplayedState(state);
        });
    }

    private void UpdateMenuChecks()
    {
        foreach (System.Windows.Forms.ToolStripItem item in _trayMenu.Items)
        {
            if (item is not System.Windows.Forms.ToolStripMenuItem menuItem)
            {
                continue;
            }

            if (menuItem.Text == "常に最前面") menuItem.Checked = Topmost;
            if (menuItem.Text == "吹き出しを表示") menuItem.Checked = _settings.ShowSpeechBubble;
            if (menuItem.Text == "クリックを背面へ通す") menuItem.Checked = _settings.ClickThrough;
            if (menuItem.Text == "通知音を鳴らす") menuItem.Checked = _settings.SoundEnabled;

            if (menuItem.Text == "表示FPS")
            {
                foreach (System.Windows.Forms.ToolStripMenuItem child in menuItem.DropDownItems)
                {
                    child.Checked = child.Tag is int value && value == _settings.TargetFps;
                }
            }
            else if (menuItem.Text == "表示倍率（元素材基準）")
            {
                foreach (System.Windows.Forms.ToolStripMenuItem child in menuItem.DropDownItems)
                {
                    child.Checked = child.Tag is double value && Math.Abs(value - _settings.Scale) < 0.01;
                }
            }
        }
    }

    private void ApplyScale(double scale, bool save = true)
    {
        _settings.Scale = scale;
        Width = CellWidth * scale;
        Height = CellHeight * scale;
        if (!double.IsNaN(Left) && !double.IsNaN(Top))
        {
            ClampToWorkArea();
        }
        if (save)
        {
            _settings.Save();
            UpdateMenuChecks();
        }
    }

    private void RestorePosition()
    {
        if (_settings.Left is double left && _settings.Top is double top)
        {
            Left = left;
            Top = top;
            ClampToWorkArea();
            return;
        }

        // Codex ペット (既定で右下 24px マージン・幅 480px) の左隣を初期位置にして
        // 並行起動時に重ならないようにする。
        var workArea = System.Windows.SystemParameters.WorkArea;
        Left = workArea.Right - Width - 528;
        Top = workArea.Bottom - Height - 24;
        ClampToWorkArea();
    }

    private void ClampToWorkArea()
    {
        var workArea = System.Windows.SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        Left = Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        Top = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        _speechBubble?.PositionNearPet();
    }

    private void ToggleClickThrough(bool enabled, bool save = true)
    {
        if (!IsSourceInitialized)
        {
            _settings.ClickThrough = enabled;
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style = enabled ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
        _settings.ClickThrough = enabled;
        if (save)
        {
            _settings.Save();
        }
        UpdateMenuChecks();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
        _eventBusWatcher.Dispose();
        if (_speechBubble is not null)
        {
            _speechBubble.Detach();
            _speechBubble.Close();
        }
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
    }

    private static string TruncateTrayText(string text) => text.Length <= 63 ? text : text[..63];

    private static string TruncateMenuText(string text, int maxLength) =>
        text.Length <= maxLength ? text : $"{text[..(maxLength - 1)]}…";

    private bool IsSourceInitialized => new WindowInteropHelper(this).Handle != IntPtr.Zero;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, value)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, value.ToInt32()));

    private sealed record TaskHistoryEntry(ActivityUpdate Update, DateTimeOffset CompletedAt);
}
