using System.Windows;
using System.Windows.Media.Animation;
using QiPlayer.Classes.Settings;
using QiPlayer.ViewModels;
using QiPlayer.Windows;

namespace QiPlayer.Classes;

/// <summary>
/// 副窗口调度：Media Flyout 的打开/关闭、动画、定位与自动隐藏。
///
/// 移植自 FluentFlyout 的 OpenAnimation / CloseAnimation / ShowMediaFlyout，
/// 数据源换成本地播放器，且不再有外部会话与音量 Flyout。
/// </summary>
public sealed class FlyoutService : IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly NowPlayingState _state = new();

    private MediaFlyoutWindow? _flyout;
    private CancellationTokenSource? _autoHideCts;
    private bool _isHiding = true;

    private TaskbarWindow? _taskbar;

    // 进度同步节流：播放器每 100ms 触发一次，Flyout 最多 200ms 刷新一次
    private DateTime _lastPositionSync = DateTime.MinValue;

    public FlyoutService(PlayerViewModel player)
    {
        _player = player;

        // 播放器属性变化时同步状态（播放器已在 UI 线程触发）
        _player.PropertyChanged += OnPlayerPropertyChanged;

        // 设置变化时即时应用（布局、模糊、亚克力等）
        SettingsManager.Current.PropertyChanged += OnSettingsChanged;

        _state.SyncFrom(_player);

        // 启动时按已保存的设置拉起任务栏宿主
        UpdateTaskbar();
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 注意：不能在这里整体 early-return。Media Flyout 是懒创建的，
        // 用户可能从未打开过它，但任务栏挂件的设置仍必须即时生效。
        switch (e.PropertyName)
        {
            // 外观 / 布局
            case nameof(UserSettings.CenterTitleArtist):
            case nameof(UserSettings.SeekbarEnabled):
            case nameof(UserSettings.MediaFlyoutBackgroundBlur):
            case nameof(UserSettings.MediaFlyoutAcrylicWindowEnabled):
                if (_flyout is null) break;
                _flyout.ApplySettings();
                _flyout.UpdateContent();
                break;

            // 这些只在下次弹出时生效，但若当前可见也应立即重排
            case nameof(UserSettings.FlyoutSelectedMonitor):
                if (IsVisible) _flyout?.ApplySettings();
                break;

            // 任务栏相关
            case nameof(UserSettings.TaskbarWidgetEnabled):
            case nameof(UserSettings.TaskbarVisualizerEnabled):
            case nameof(UserSettings.TaskbarWidgetControlsEnabled):
            case nameof(UserSettings.TaskbarWidgetSelectedMonitor):
            case nameof(UserSettings.TaskbarWidgetManualPadding):
            case nameof(UserSettings.TaskbarWidgetHideCompletely):
            case nameof(UserSettings.TaskbarWidgetScrollingEnabled):
            case nameof(UserSettings.TaskbarWidgetScrollingTextLoopForever):
            case nameof(UserSettings.TaskbarWidgetScrollingTextSpeed):
            case nameof(UserSettings.TaskbarVisualizerBarCount):
            case nameof(UserSettings.TaskbarVisualizerWidth):
            case nameof(UserSettings.TaskbarVisualizerCenteredBars):
            case nameof(UserSettings.TaskbarVisualizerBaseline):
            case nameof(UserSettings.TaskbarLyricsEnabled):
            case nameof(UserSettings.TaskbarLyricsMaxWidth):
            case nameof(UserSettings.TaskbarLyricsFontSize):
            case nameof(UserSettings.TaskbarLyricsFontFamily):
            case nameof(UserSettings.TaskbarLyricsKaraokeEnabled):
                UpdateTaskbar();
                _taskbar?.UpdateContent(_state);
                break;
        }
    }

    public NowPlayingState State => _state;

    /// <summary>Flyout 当前是否可见。</summary>
    public bool IsVisible => _flyout is { IsVisible: true } && !_isHiding;

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // 曲目切换 / 封面 / 播放状态：立即同步
            case nameof(PlayerViewModel.CurrentSong):
                _state.SyncFrom(_player);
                _taskbar?.UpdateContent(_state);
                break;

            case nameof(PlayerViewModel.AlbumCover):
            case nameof(PlayerViewModel.IsPlaying):
            case nameof(PlayerViewModel.DurationSeconds):
                _state.SyncFrom(_player);
                _taskbar?.UpdateContent(_state);
                break;

            // 歌词行切换：任务栏单行歌词要即时跟着换（连同逐字时间表）。
            // 同时监听索引：重复副歌的前后两行文本可能完全相同，
            // 只看文本的话第二次不会触发通知，卡拉OK 会停在上一行。
            case nameof(PlayerViewModel.CurrentLyricIndex):
            case nameof(PlayerViewModel.CurrentLyricText):
            case nameof(PlayerViewModel.CurrentLyricTranslation):
                _state.SyncLyric(_player);
                _state.PositionSeconds = _player.PositionSeconds;
                _taskbar?.UpdateContent(_state);
                break;

            // 进度：任务栏歌词的卡拉OK 进度必须持续跟进，不能因 Flyout 不可见而跳过
            case nameof(PlayerViewModel.PositionSeconds):
                _taskbar?.UpdateLyricProgress(_player.PositionSeconds, _player.IsPlaying);

                // Flyout 的进度同步仍节流到 200ms，且不可见时跳过
                if (!IsVisible) return;

                var now = DateTime.UtcNow;
                if ((now - _lastPositionSync).TotalMilliseconds < 200) return;

                _lastPositionSync = now;
                _state.PositionSeconds = _player.PositionSeconds;
                break;
        }
    }

    /// <summary>
    /// 显示媒体 Flyout。
    /// </summary>
    /// <param name="toggleMode">true 时，若已显示则关闭。</param>
    /// <param name="forceShow">忽略"未启用"与全屏抑制设置。</param>
    public async void Show(bool toggleMode = false, bool forceShow = false)
    {
        if (!forceShow)
        {
            if (!SettingsManager.Current.MediaFlyoutEnabled) return;
            if (FullscreenDetector.IsFullscreenApplicationRunning()) return;
        }

        // 没有曲目时不弹出
        if (!_state.HasTrack) return;

        EnsureWindow();

        if (toggleMode && IsVisible)
        {
            Hide();
            return;
        }

        _state.SyncFrom(_player);
        _flyout!.UpdateContent();

        _autoHideCts?.Cancel();
        _autoHideCts = new CancellationTokenSource();
        var token = _autoHideCts.Token;

        if (_isHiding)
        {
            _isHiding = false;
            OpenAnimation(_flyout);
        }

        WindowHelper.SetTopmost(_flyout);

        try
        {
            // 打开动画期间鼠标可能正好在窗口滑入路径上，先给一段宽限
            await Task.Delay(Math.Max(300, GetAnimationDuration()), token);

            while (!token.IsCancellationRequested)
            {
                if (WindowHelper.IsMouseOverWindow(_flyout))
                {
                    await Task.Delay(100, token);
                    continue;
                }

                // 鼠标还停在挂件上（悬停封面/歌名弹出的场景）：同样保持显示，
                // 否则鼠标一离开封面区，Flyout 马上就收起来了
                if (_taskbar?.IsWidgetHovered == true)
                {
                    await Task.Delay(100, token);
                    continue;
                }

                // 鼠标离开：按设置停留，再确认一次
                await Task.Delay(SettingsManager.Current.Duration, token);

                if (WindowHelper.IsMouseOverWindow(_flyout)
                    || _taskbar?.IsWidgetHovered == true)
                {
                    continue;
                }

                Hide();
                break;
            }
        }
        catch (OperationCanceledException)
        {
            // 被新的显示请求取代
        }
    }

    /// <summary>关闭 Flyout（带动画）。</summary>
    public async void Hide()
    {
        if (_flyout is null || _isHiding) return;

        _autoHideCts?.Cancel();
        _isHiding = true;

        CloseAnimation(_flyout);

        await Task.Delay(GetAnimationDuration());
        if (!_isHiding) return;   // 期间又被打开

        WindowHelper.SetVisibility(_flyout, false);
    }

    // ---------- 动画 ----------

    private static int GetAnimationDuration() => SettingsManager.Current.FlyoutAnimationSpeed switch
    {
        0 => 0,
        1 => 150,
        2 => 300,
        3 => 450,
        4 => 600,
        _ => 900,
    };

    private static EasingFunctionBase? GetEasing(bool easeOut)
    {
        var mode = easeOut ? EasingMode.EaseOut : EasingMode.EaseIn;

        return SettingsManager.Current.FlyoutAnimationEasingStyle switch
        {
            0 => null,
            1 => new SineEase { EasingMode = mode },
            2 => new QuadraticEase { EasingMode = mode },
            _ => new CubicEase { EasingMode = mode },
        };
    }

    /// <summary>
    /// 打开动画：先移到目标位置（物理像素，避免 DPI 误差），再滑入并淡入。
    /// </summary>
    private void OpenAnimation(MediaFlyoutWindow window)
    {
        var monitor = MonitorUtil.GetSelectedMonitor(SettingsManager.Current.FlyoutSelectedMonitor);
        var workArea = monitor.workArea;

        // 先隐藏，防止在旧位置闪一下
        WindowHelper.SetVisibility(window, false);

        // 移到目标显示器，让 WPF 先按该显示器的 DPI 完成布局
        WindowHelper.SetPosition(window, workArea.Left, workArea.Top);
        window.UpdateLayout();

        var rect = WindowHelper.GetPlacement(window);
        double width = rect.Width > 0 ? rect.Width : window.Width * monitor.dpiX / 96.0;
        double height = rect.Height > 0 ? rect.Height : window.Height * monitor.dpiY / 96.0;

        // 固定贴在屏幕底部（任务栏上方），与挂件在同一侧
        const double margin = 16;
        double left = workArea.Left + margin;
        double top = workArea.Bottom - height - margin;

        // 水平方向始终与挂件封面区的左边缘对齐：挂件会随歌词宽度左右移动，
        // 每次弹出都重新取一次锚点，保证 Flyout 总在封面正上方。
        // 挂件未显示（只有频谱）时贴在屏幕左侧。
        if (_taskbar?.CoverScreenPosition is { } anchor)
        {
            left = Math.Clamp(
                anchor.X,
                workArea.Left,
                Math.Max(workArea.Left, workArea.Right - width));
        }

        int duration = GetAnimationDuration();
        double offset = duration == 0 ? 0 : 20;

        // 起始位置（物理像素）：从下方滑入
        double fromTopPx = top + offset;
        WindowHelper.SetPosition(window, left, fromTopPx);

        // WPF 的 Top/Left 使用 DIP，需要按 DPI 换算
        double scaleY = 96.0 / (monitor.dpiY > 0 ? monitor.dpiY : 96);

        // 清掉上一次的动画，否则 From 会被旧动画的当前值覆盖
        window.BeginAnimation(Window.TopProperty, null);
        window.BeginAnimation(Window.OpacityProperty, null);

        // 先落到起点，再启动动画
        window.Top = fromTopPx * scaleY;
        window.Opacity = duration == 0 ? 1 : 0;

        if (duration > 0)
        {
            var storyboard = new Storyboard();

            var move = new DoubleAnimation
            {
                From = fromTopPx * scaleY,
                To = top * scaleY,
                Duration = TimeSpan.FromMilliseconds(duration),
                EasingFunction = GetEasing(true),
            };
            Storyboard.SetTarget(move, window);
            Storyboard.SetTargetProperty(move, new PropertyPath(Window.TopProperty));
            storyboard.Children.Add(move);

            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(duration),
                EasingFunction = GetEasing(true),
            };
            Storyboard.SetTarget(fade, window);
            Storyboard.SetTargetProperty(fade, new PropertyPath(Window.OpacityProperty));
            storyboard.Children.Add(fade);

            storyboard.Begin(window, HandoffBehavior.SnapshotAndReplace, true);
        }

        WindowHelper.SetVisibility(window, true);
        WindowHelper.SetTopmost(window);
    }

    /// <summary>关闭动画：淡出并向下轻微滑动。</summary>
    private static void CloseAnimation(MediaFlyoutWindow window)
    {
        int duration = GetAnimationDuration();

        double currentTopDip = window.Top;
        double targetTopDip = currentTopDip + (duration == 0 ? 0 : 20);

        if (duration == 0)
        {
            window.Opacity = 0;
            return;
        }

        var storyboard = new Storyboard();

        var move = new DoubleAnimation
        {
            From = currentTopDip,
            To = targetTopDip,
            Duration = TimeSpan.FromMilliseconds(duration),
            EasingFunction = GetEasing(false),
        };
        Storyboard.SetTarget(move, window);
        Storyboard.SetTargetProperty(move, new PropertyPath(Window.TopProperty));
        storyboard.Children.Add(move);

        var fade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(duration),
            EasingFunction = GetEasing(false),
        };
        Storyboard.SetTarget(fade, window);
        Storyboard.SetTargetProperty(fade, new PropertyPath(Window.OpacityProperty));
        storyboard.Children.Add(fade);

        storyboard.Begin(window, HandoffBehavior.SnapshotAndReplace, true);
    }


    // ---------- 窗口 ----------

    private void EnsureWindow()
    {
        if (_flyout is not null) return;

        _flyout = new MediaFlyoutWindow(_player);
        _flyout.SetState(_state);

        // 单击封面：先收起面板，再把主窗口叫到前台
        _flyout.CoverClicked += OnCoverClicked;

        // 必须真正 Show 一次，WPF 才会创建渲染管线与内容；
        // 随后立刻在原生层隐藏，交由 OpenAnimation 负责出场。
        // 窗口初始 Top=-9999，Show 的瞬间不会出现在屏幕上。
        _flyout.Show();

        _flyout.Closed += (_, _) => _flyout = null;
    }

    /// <summary>单击 Media Flyout 封面：关闭面板并唤起主窗口。</summary>
    private void OnCoverClicked(object? sender, EventArgs e)
    {
        Hide();
        App.MainPlayerWindow?.RestoreFromTray();
    }

    // ---------- 任务栏 ----------

    /// <summary>按设置创建或销毁任务栏宿主。</summary>
    public void UpdateTaskbar()
    {
        var settings = SettingsManager.Current;
        bool needed = settings.TaskbarWidgetEnabled || settings.TaskbarVisualizerEnabled;

        if (!needed)
        {
            _taskbar?.Close();
            _taskbar = null;
            return;
        }

        if (_taskbar is null)
        {
            try
            {
                _taskbar = new TaskbarWindow
                {
                    StreamProvider = () => _player.StreamHandle,
                };

                // 鼠标移到挂件的封面/歌名区域：直接弹出 Flyout（不需要点击）。
                // 这里不用 forceShow：悬停是无意动作，仍应尊重“关闭媒体 Flyout”和全屏抑制
                _taskbar.WidgetCoverHovered += (_, _) => Show();
                _taskbar.PlayPauseRequested += (_, _) => _player.PlayPauseCommand.Execute(null);
                _taskbar.NextRequested += (_, _) => _player.PlayNextCommand.Execute(null);
                _taskbar.PreviousRequested += (_, _) => _player.PlayPreviousCommand.Execute(null);

                _taskbar.Show();

                // 首次创建时 RefreshSettings 早于 Window_Loaded（Visibility 还是 Collapsed），
                // 这里在窗口真正显示后补一次，确保频谱计时器能启动。
                _taskbar.Loaded += (_, _) =>
                {
                    // 恢复上次播放时，曲目与歌词行可能在 FlyoutService 订阅之前就已设好，
                    // 这里补一次整体同步，否则启动后要等到下一次换行才会显示歌词。
                    _state.SyncFrom(_player);

                    _taskbar?.RefreshSettings();
                    _taskbar?.UpdateContent(_state);
                };
            }
            catch (Exception ex)
            {
                // 任务栏增强工具/资源缺失不应拖垮播放器
                App.Log("任务栏挂件创建失败", ex);
                _taskbar = null;
                return;
            }
        }

        _taskbar.RefreshSettings();
        _taskbar.UpdateContent(_state);
    }

    /// <summary>
    /// 重建任务栏宿主窗口。
    /// Explorer 重启会销毁任务栏及其子窗口，此时必须重新创建。
    /// </summary>
    public void RecreateTaskbar()
    {
        var settings = SettingsManager.Current;
        bool needed = settings.TaskbarWidgetEnabled || settings.TaskbarVisualizerEnabled;

        try
        {
            _taskbar?.Close();
        }
        catch
        {
            // 旧窗口可能已随 Explorer 一起销毁
        }

        _taskbar = null;

        if (needed) UpdateTaskbar();
    }

    /// <summary>应用设置变化（主题、模糊、位置等）。</summary>
    public void Refresh()
    {
        _flyout?.ApplySettings();
        _taskbar?.RefreshSettings();
        _taskbar?.ApplyTheme();
    }

    public void Dispose()
    {
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        SettingsManager.Current.PropertyChanged -= OnSettingsChanged;

        _autoHideCts?.Cancel();
        _autoHideCts?.Dispose();
        _flyout?.Close();
        _taskbar?.Close();
    }
}
