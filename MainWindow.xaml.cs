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
    private System.Windows.Forms.ToolStripMenuItem? _replayReportMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayClickThroughItem;
    private System.Windows.Controls.ContextMenu? _petMenu;
    private System.Windows.Controls.MenuItem? _petReplayItem;
    private System.Windows.Controls.MenuItem? _petRecentMenu;
    private System.Windows.Controls.MenuItem? _petFpsMenu;
    private System.Windows.Controls.MenuItem? _petScaleMenu;
    private System.Windows.Controls.MenuItem? _petTopmostItem;
    private System.Windows.Controls.MenuItem? _petBubbleItem;
    private System.Windows.Controls.MenuItem? _petClickThroughItem;
    private System.Windows.Controls.MenuItem? _petSoundItem;
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
        MouseRightButtonUp += (_, e) =>
        {
            _petMenu ??= BuildPetMenu();
            _petMenu.IsOpen = true;
            e.Handled = true;
        };
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CustomFramesRoot);
            Directory.CreateDirectory(SoundsRoot);
            WriteConfigReadmes();
            var framesRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Frames");
            _animation.Load(framesRoot, CustomFramesRoot);
            _ = PreloadDragAnimationsAsync();
            ClampTargetFpsToClips();
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
            _petMenu = BuildPetMenu();

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
            var bubbleHold = _speechBubble?.ShowActivity(displayUpdate);
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

            // 一過性モーションは吹き出しが消えるまでループさせる。吹き出しが
            // 出なかった場合 (サイレント・吹き出しオフ) はアニメ 1 周分だけ。
            if (update.State == PetState.Jumping)
            {
                StartTemporaryState(bubbleHold?.TotalSeconds ?? _animation.DurationSeconds(PetState.Jumping));
            }
            else if (update.State == PetState.Failed)
            {
                StartTemporaryState(bubbleHold?.TotalSeconds ?? Math.Max(5, _animation.DurationSeconds(PetState.Failed)));
            }
            else if (update.State == PetState.Waving)
            {
                StartTemporaryState(bubbleHold?.TotalSeconds ?? _animation.DurationSeconds(PetState.Waving));
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
        _temporaryStateTimer.Stop();
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
            ClampTargetFpsToClips();
            _petMenu = null; // 表示FPS候補が素材レートに依存するため次回開時に再構築
            SetDisplayedState(_activityState, "素材を再読み込みしました");
        }
        catch (Exception exception)
        {
            SetDisplayedState(PetState.Failed, $"再読み込み失敗: {exception.Message}");
        }
    }

    // 設定フォルダを開いただけで使い方が分かるよう、説明ファイルを毎回上書き生成する
    // (アップデートで書式が変わっても常に最新の説明になる)。
    private void WriteConfigReadmes()
    {
        // メモ帳以外の古いツールでも化けないよう BOM 付き UTF-8 で書く。
        var utf8Bom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        try
        {
            File.WriteAllText(Path.Combine(SoundsRoot, "readme.txt"), string.Join(Environment.NewLine,
                "【通知音の設定】",
                "このフォルダに音声ファイル (.wav / .mp3 / .wma) を置くと、状態イベント時に再生されます。",
                "ファイル名 = 状態名。ファイルを置くだけで即反映 (再起動・再読み込み不要)。",
                "",
                "  jumping.wav  … タスク完了の報告時",
                "  failed.wav   … 失敗時",
                "  waiting.wav  … 入力待ち (許可確認など) 時",
                "  waving.wav   … セッション開始時",
                "  running.wav  … 作業開始時",
                "  review.wav   … 確認中",
                "",
                "オン/オフ: ペットを右クリック → 「通知音を鳴らす」",
                "同じ種類の音は 3 秒間隔で間引かれます。"), utf8Bom);
            File.WriteAllText(Path.Combine(CustomFramesRoot, "readme.txt"), string.Join(Environment.NewLine,
                "【アニメの差し替え】",
                "このフォルダに素材を置くと、状態単位で標準アニメを上書きできます。",
                "",
                "形式1: スプライトシート … <状態名>.png (+ 任意の <状態名>.json)",
                "  例: idle.png と idle.json → {\"columns\": 8, \"rows\": 1, \"fps\": 16}",
                "形式2: フレームフォルダ … <状態名>\\frame_000.png, frame_001.png, …",
                "  (+ 任意の timing.yaml: 「fps: 8」か「durations_ms: [5000, 250]」の1行)",
                "",
                "状態名: idle running running-right running-left waving jumping failed waiting review",
                "セルの縦横比は 192:208 (推奨 576x624)、透過 PNG。",
                "",
                "反映: ペットを右クリック → 「素材を再読み込み」"), utf8Bom);
        }
        catch (IOException)
        {
            // 説明ファイルは本体機能に影響しないので書けなくても無視する。
        }
        catch (UnauthorizedAccessException)
        {
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

    // トレイメニューはクリック透過中の救済用に最小限へ縮小。フル機能はペット
    // 右クリックの WPF カスタムメニュー (BuildPetMenu) が持つ。
    private System.Windows.Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        _replayReportMenuItem = new System.Windows.Forms.ToolStripMenuItem("最新の報告を再表示")
        {
            Enabled = false,
        };
        _replayReportMenuItem.Click += (_, _) => ReplayReport(_lastBubbleUpdate);
        menu.Items.Add(_replayReportMenuItem);

        _trayClickThroughItem = new System.Windows.Forms.ToolStripMenuItem("クリックを背面へ通す")
        {
            CheckOnClick = true,
        };
        _trayClickThroughItem.Click += (_, _) => ToggleClickThrough(_trayClickThroughItem.Checked);
        menu.Items.Add(_trayClickThroughItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => Close());
        menu.Opening += (_, _) => _trayClickThroughItem.Checked = _settings.ClickThrough;
        return menu;
    }

    // ------------------------------------------------------------------
    // ペット右クリックの WPF カスタムメニュー (App.xaml の PetContextMenu スタイル)

    private System.Windows.Controls.MenuItem NewMenuItem(
        string header,
        System.Windows.RoutedEventHandler? click = null,
        bool checkable = false)
    {
        var item = new System.Windows.Controls.MenuItem
        {
            Header = header,
            Style = (System.Windows.Style)FindResource("PetMenuItem"),
            IsCheckable = checkable,
            StaysOpenOnClick = false,
        };
        if (click is not null)
        {
            item.Click += click;
        }
        return item;
    }

    private System.Windows.Controls.Separator NewSeparator() => new()
    {
        Style = (System.Windows.Style)FindResource("PetMenuSeparator"),
    };

    private System.Windows.Controls.ContextMenu BuildPetMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            Style = (System.Windows.Style)FindResource("PetContextMenu"),
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };
        menu.Opened += (_, _) => RefreshPetMenu();

        _petReplayItem = NewMenuItem("最新の報告を再表示", (_, _) => ReplayReport(_lastBubbleUpdate));
        menu.Items.Add(_petReplayItem);
        _petRecentMenu = NewMenuItem("最近の完了報告");
        menu.Items.Add(_petRecentMenu);
        menu.Items.Add(NewSeparator());

        var motions = NewMenuItem("モーション");
        foreach (var (label, state) in new[]
                 {
                     ("待機", PetState.Idle),
                     ("作業中", PetState.Running),
                     ("入力待ち", PetState.Waiting),
                     ("確認中", PetState.Review),
                     ("完了", PetState.Jumping),
                     ("失敗", PetState.Failed),
                     ("手を振る", PetState.Waving),
                 })
        {
            var captured = state;
            motions.Items.Add(NewMenuItem(label, (_, _) =>
            {
                _temporaryStateTimer.Stop();
                _activityState = captured;
                SetDisplayedState(captured);
            }));
        }
        menu.Items.Add(motions);

        _petFpsMenu = NewMenuItem("表示FPS");
        foreach (var value in DisplayFpsOptions())
        {
            var captured = value;
            var item = NewMenuItem($"{value} fps", (_, _) =>
            {
                _settings.TargetFps = captured;
                _settings.Save();
                SetDisplayedState(_activityState, $"表示 {captured}fps");
            }, checkable: true);
            item.Tag = value;
            _petFpsMenu.Items.Add(item);
        }
        menu.Items.Add(_petFpsMenu);

        _petScaleMenu = NewMenuItem("表示倍率");
        foreach (var value in new[] { 0.75, 1.0, 1.5, 2.0, 2.5, 3.0 })
        {
            var captured = value;
            var sourceScale = CellWidth * value / NormalSourceWidth;
            var item = NewMenuItem($"{sourceScale:0.00}×", (_, _) => ApplyScale(captured), checkable: true);
            item.Tag = value;
            _petScaleMenu.Items.Add(item);
        }
        menu.Items.Add(_petScaleMenu);
        menu.Items.Add(NewSeparator());

        _petTopmostItem = NewMenuItem("常に最前面", (_, _) =>
        {
            Topmost = _petTopmostItem!.IsChecked;
            if (_speechBubble is not null)
            {
                _speechBubble.Topmost = Topmost;
            }
            _settings.Topmost = Topmost;
            _settings.Save();
        }, checkable: true);
        menu.Items.Add(_petTopmostItem);

        _petBubbleItem = NewMenuItem("吹き出しを表示", (_, _) =>
        {
            _settings.ShowSpeechBubble = _petBubbleItem!.IsChecked;
            if (_speechBubble is not null)
            {
                _speechBubble.Enabled = _settings.ShowSpeechBubble;
            }
            _settings.Save();
        }, checkable: true);
        menu.Items.Add(_petBubbleItem);

        _petClickThroughItem = NewMenuItem("クリックを背面へ通す", (_, _) =>
            ToggleClickThrough(_petClickThroughItem!.IsChecked), checkable: true);
        menu.Items.Add(_petClickThroughItem);

        _petSoundItem = NewMenuItem("通知音を鳴らす", (_, _) =>
        {
            _settings.SoundEnabled = _petSoundItem!.IsChecked;
            _settings.Save();
        }, checkable: true);
        menu.Items.Add(_petSoundItem);
        menu.Items.Add(NewSeparator());

        menu.Items.Add(NewMenuItem("設定フォルダを開く (素材/音)", (_, _) => OpenConfigFolder()));
        menu.Items.Add(NewMenuItem("素材を再読み込み", (_, _) => ReloadAssets()));
        menu.Items.Add(NewSeparator());
        menu.Items.Add(NewMenuItem("終了", (_, _) => Close()));
        return menu;
    }

    private void RefreshPetMenu()
    {
        if (_petReplayItem is not null)
        {
            _petReplayItem.IsEnabled = _lastBubbleUpdate is not null;
        }
        if (_petTopmostItem is not null) _petTopmostItem.IsChecked = Topmost;
        if (_petBubbleItem is not null) _petBubbleItem.IsChecked = _settings.ShowSpeechBubble;
        if (_petClickThroughItem is not null) _petClickThroughItem.IsChecked = _settings.ClickThrough;
        if (_petSoundItem is not null) _petSoundItem.IsChecked = _settings.SoundEnabled;
        if (_petFpsMenu is not null)
        {
            foreach (System.Windows.Controls.MenuItem item in _petFpsMenu.Items)
            {
                item.IsChecked = item.Tag is int value && value == _settings.TargetFps;
            }
        }
        if (_petScaleMenu is not null)
        {
            foreach (System.Windows.Controls.MenuItem item in _petScaleMenu.Items)
            {
                item.IsChecked = item.Tag is double value && Math.Abs(value - _settings.Scale) < 0.01;
            }
        }
        RefreshPetRecentMenu();
    }

    private void RefreshPetRecentMenu()
    {
        if (_petRecentMenu is null)
        {
            return;
        }

        _petRecentMenu.Items.Clear();
        if (_recentTasks.Count == 0)
        {
            var empty = NewMenuItem("まだ報告はありません");
            empty.IsEnabled = false;
            _petRecentMenu.Items.Add(empty);
            return;
        }

        foreach (var entry in _recentTasks)
        {
            var mark = entry.Update.State == PetState.Failed ? "!" : "✓";
            var taskName = string.IsNullOrWhiteSpace(entry.Update.TaskName)
                ? entry.Update.Source
                : entry.Update.TaskName;
            var report = entry.Update;
            var item = NewMenuItem(
                $"{mark} {entry.CompletedAt.ToLocalTime():HH:mm} {TruncateMenuText(taskName!, 34)}",
                (_, _) => ReplayReport(report));
            item.ToolTip = entry.Update.Message ?? string.Empty;
            _petRecentMenu.Items.Add(item);
        }
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

        var hold = _speechBubble?.ShowActivity(update with { ShowInSpeechBubble = true });
        // 再表示でも吹き出しと同期してモーションを再演する
        if (hold is { } duration
            && update.State is PetState.Jumping or PetState.Failed or PetState.Waving)
        {
            SetDisplayedState(update.State);
            StartTemporaryState(duration.TotalSeconds);
        }
    }

    // 表示FPSの選択肢は素材の最大クリップレートまでに限定する
    // (8fps 素材に 60/120fps 表示を出しても意味がなく紛らわしいだけ)。
    private int MaxClipFps() =>
        (int)Math.Ceiling(Enum.GetValues<PetState>().Max(state => _animation.RequiredDisplayFps(state)));

    private int[] DisplayFpsOptions()
    {
        var maxFps = Math.Max(1, MaxClipFps());
        var options = new[] { 8, 16, 30, 32, 60, 120 }.Where(value => value <= maxFps).ToList();
        if (options.Count == 0 || options[^1] < maxFps)
        {
            options.Add(maxFps);
        }
        return [.. options];
    }

    private void ClampTargetFpsToClips()
    {
        var maxOption = DisplayFpsOptions()[^1];
        if (_settings.TargetFps > maxOption)
        {
            _settings.TargetFps = maxOption;
            _settings.Save();
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
        Left = workArea.Right - Width - 24;
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
