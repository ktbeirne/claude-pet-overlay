using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClaudePetOverlay.Models;
using MediaColor = System.Windows.Media.Color;

namespace ClaudePetOverlay;

public partial class SpeechBubbleWindow : System.Windows.Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private readonly System.Windows.Window _petWindow;
    private readonly DispatcherTimer _hideTimer = new();
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly bool _qaMode;
    private ActivityUpdate? _currentActivity;
    private bool _enabled = true;

    public SpeechBubbleWindow(System.Windows.Window petWindow, bool qaMode)
    {
        _petWindow = petWindow;
        _qaMode = qaMode;
        InitializeComponent();
        ShowInTaskbar = qaMode;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => PositionNearPet();
        _petWindow.LocationChanged += OnPetGeometryChanged;
        _petWindow.SizeChanged += OnPetGeometryChanged;
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            _elapsedTimer.Stop();
            Hide();
        };
        _elapsedTimer.Tick += (_, _) => UpdateMetadata();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                _hideTimer.Stop();
                _elapsedTimer.Stop();
                Hide();
            }
        }
    }

    public void ShowActivity(ActivityUpdate update)
    {
        if (!_enabled)
        {
            return;
        }

        if (!update.ShowInSpeechBubble
            || update.State == PetState.Idle
            || string.IsNullOrWhiteSpace(update.Message))
        {
            // 完了報告等のホールド中 (_hideTimer 稼働中) は、並走セッションの
            // サイレントな running や SessionEnd の idle で報告を握りつぶさない。
            // タイマー満了で自然に閉じる。
            if (_hideTimer.IsEnabled)
            {
                return;
            }
            _elapsedTimer.Stop();
            Hide();
            return;
        }

        _hideTimer.Stop();

        _currentActivity = update;
        BubbleHeader.Text = update.State switch
        {
            PetState.Jumping => "CLAUDE // REPORT",
            PetState.Failed => "CLAUDE // ALERT",
            PetState.Waiting => "CLAUDE // REQUEST",
            _ => "CLAUDE // ACTIVITY",
        };
        StateText.Text = update.State switch
        {
            PetState.Running => "作業中",
            PetState.Review => "確認中",
            PetState.Waiting => "入力待ち",
            PetState.Jumping => "完了",
            PetState.Failed => "エラー",
            _ => "更新",
        };
        ApplyStateColors(update.State);
        var taskName = update.TaskName?.Trim();
        TaskNameText.Text = taskName;
        TaskNamePanel.Visibility = string.IsNullOrWhiteSpace(taskName)
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
        BubbleBody.Text = update.Message;
        UpdateMetadata();

        _elapsedTimer.Stop();
        if (update.StartedAt is not null
            && update.State is PetState.Running or PetState.Review or PetState.Waiting)
        {
            _elapsedTimer.Start();
        }

        var wasVisible = IsVisible;
        if (!IsVisible)
        {
            Show();
        }
        PositionNearPet();
        if (!wasVisible)
        {
            AnimateIn();
        }

        if (update.State is PetState.Jumping or PetState.Failed or PetState.Waving)
        {
            _hideTimer.Interval = TimeSpan.FromSeconds(update.State == PetState.Jumping ? 9 : 8);
            _hideTimer.Start();
        }
    }

    private void UpdateMetadata()
    {
        if (_currentActivity is not { } update)
        {
            MetadataPanel.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        if (update.StartedAt is { } startedAt)
        {
            var end = update.State is PetState.Running or PetState.Review or PetState.Waiting
                ? DateTimeOffset.UtcNow
                : update.EndedAt ?? DateTimeOffset.UtcNow;
            var elapsed = end - startedAt;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }
            var elapsedValue = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
            var label = update.State is PetState.Jumping or PetState.Failed ? "所要" : "経過";
            ElapsedText.Text = $"{label} {elapsedValue}";
            ElapsedText.Visibility = System.Windows.Visibility.Visible;
        }
        else
        {
            ElapsedText.Text = string.Empty;
            ElapsedText.Visibility = System.Windows.Visibility.Collapsed;
        }

        if (update.ActiveTaskCount > 0)
        {
            TaskCountText.Text = update.State is PetState.Jumping or PetState.Failed
                ? $"残り {update.ActiveTaskCount}件"
                : update.ActiveTaskCount > 1
                    ? $"同時実行 {update.ActiveTaskCount}件"
                    : "実行中 1件";
            TaskCountText.Visibility = System.Windows.Visibility.Visible;
        }
        else
        {
            TaskCountText.Text = string.Empty;
            TaskCountText.Visibility = System.Windows.Visibility.Collapsed;
        }

        MetadataPanel.Visibility = ElapsedText.Visibility == System.Windows.Visibility.Visible
                                   || TaskCountText.Visibility == System.Windows.Visibility.Visible
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    public void PositionNearPet()
    {
        if (!IsVisible)
        {
            return;
        }

        var workArea = System.Windows.SystemParameters.WorkArea;
        var bubbleWidth = ActualWidth > 0 ? ActualWidth : Width;
        var bubbleHeight = ActualHeight > 0 ? ActualHeight : 140;
        var centeredLeft = _petWindow.Left + ((_petWindow.ActualWidth - bubbleWidth) / 2);
        Left = Math.Clamp(
            centeredLeft,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - bubbleWidth));
        Top = Math.Max(workArea.Top, _petWindow.Top - bubbleHeight - 3);
    }

    private void ApplyStateColors(PetState state)
    {
        // Codex 版 (シアン基調) と見分けるため、通常/完了はコーラル・アンバー基調。
        // 失敗と入力待ちは意味色として Codex 版と共通。
        var accent = state switch
        {
            PetState.Jumping => MediaColor.FromRgb(255, 176, 112),
            PetState.Failed => MediaColor.FromRgb(255, 116, 157),
            PetState.Waiting => MediaColor.FromRgb(167, 142, 255),
            _ => MediaColor.FromRgb(255, 143, 107),
        };
        AccentBar.Background = new SolidColorBrush(accent);
        StateText.Foreground = new SolidColorBrush(MediaColor.FromRgb(
            (byte)Math.Max(0, accent.R - 45),
            (byte)Math.Max(0, accent.G - 70),
            (byte)Math.Max(0, accent.B - 45)));
        StatePill.Background = new SolidColorBrush(MediaColor.FromArgb(30, accent.R, accent.G, accent.B));
        StatePill.BorderBrush = new SolidColorBrush(MediaColor.FromArgb(105, accent.R, accent.G, accent.B));
    }

    private void AnimateIn()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        Opacity = 0;
        BubbleRoot.RenderTransform = new TranslateTransform(0, 7);
        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)) { EasingFunction = easing });
        ((TranslateTransform)BubbleRoot.RenderTransform).BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = easing });
    }

    public void Detach()
    {
        _petWindow.LocationChanged -= OnPetGeometryChanged;
        _petWindow.SizeChanged -= OnPetGeometryChanged;
        _hideTimer.Stop();
        _elapsedTimer.Stop();
    }

    private void OnPetGeometryChanged(object? sender, EventArgs e) => PositionNearPet();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        var bubbleStyle = WsExTransparent | WsExNoActivate | (_qaMode ? 0 : WsExToolWindow);
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | bubbleStyle));
    }

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
}
