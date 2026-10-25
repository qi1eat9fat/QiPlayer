using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Xml.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QiPlayer.Classes.Settings;

/// <summary>
/// 全局设置模型（唯一数据源）。
///
/// 来源：FluentFlyout 的 UserSettings，按已确认范围裁剪：
///   · 删除：Premium/付费、遥测、实验、更新检查、语言选择、外部播放器、
///           音量 Flyout/混音器、锁键 Flyout、应用过滤。
///   · 保留：Media Flyout、任务栏挂件、任务栏频谱、Next Up、外观/系统。
///   · 新增：播放器与歌词（来自原 7Player 的 SettingsStore）。
///
/// 持久化：程序目录下的 QiPlayer.settings.xml（便携模式），见 <see cref="SettingsManager"/>。
/// </summary>
public partial class UserSettings : ObservableObject
{
    // 可持久化属性名（排除 XmlIgnore）：属性变更时据此决定是否落盘
    private static readonly HashSet<string> PersistedPropertyNames =
    [
        .. typeof(UserSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanWrite && property.GetCustomAttribute<XmlIgnoreAttribute>() is null)
            .Select(property => property.Name)
    ];

    // ---------- 主窗口状态 ----------

    [ObservableProperty]
    public partial double PlayerWindowWidth { get; set; }

    [ObservableProperty]
    public partial double PlayerWindowHeight { get; set; }

    [ObservableProperty]
    public partial double PlayerWindowLeft { get; set; }

    [ObservableProperty]
    public partial double PlayerWindowTop { get; set; }

    [ObservableProperty]
    public partial WindowState PlayerWindowState { get; set; }

    // ---------- 三栏布局 ----------

    /// <summary>左栏（歌词）面板是否显示；下次启动沿用上次的状态。</summary>
    [ObservableProperty]
    public partial bool ShowLyricsPane { get; set; }

    /// <summary>右栏（播放列表）面板是否显示；下次启动沿用上次的状态。</summary>
    [ObservableProperty]
    public partial bool ShowPlaylistPane { get; set; }

    // ---------- 播放器（原 7Player SettingsStore） ----------

    /// <summary>上次打开的文件夹。</summary>
    [ObservableProperty]
    public partial string LastFolder { get; set; }

    /// <summary>播放模式，存字符串以便阅读与向前兼容。</summary>
    [ObservableProperty]
    public partial string RepeatMode { get; set; }

    /// <summary>上次播放的曲目路径。</summary>
    [ObservableProperty]
    public partial string LastFilePath { get; set; }

    /// <summary>上次播放到的位置（秒）。</summary>
    [ObservableProperty]
    public partial double LastPositionSeconds { get; set; }

    /// <summary>启动时是否恢复上次播放的曲目与位置。</summary>
    [ObservableProperty]
    public partial bool ResumeLastPlayback { get; set; }

    // ---------- 歌词 ----------

    /// <summary>当前行字号（其余行按比例缩小）。</summary>
    [ObservableProperty]
    public partial double LyricFontSize { get; set; }

    /// <summary>歌词行间距（像素）。</summary>
    [ObservableProperty]
    public partial double LyricLineSpacing { get; set; }

    /// <summary>左栏歌词窗口的字体；空字符串表示跟随应用字体。</summary>
    [ObservableProperty]
    public partial string LyricFontFamily { get; set; }

    // ---------- Media Flyout ----------

    /// <summary>Flyout 目标显示器（0 = 主显示器）。</summary>
    [ObservableProperty]
    public partial int FlyoutSelectedMonitor { get; set; }

    /// <summary>动画速度倍率。</summary>
    [ObservableProperty]
    public partial int FlyoutAnimationSpeed { get; set; }

    /// <summary>动画缓动样式。</summary>
    [ObservableProperty]
    public partial int FlyoutAnimationEasingStyle { get; set; }

    /// <summary>是否启用 Media Flyout。</summary>
    [ObservableProperty]
    public partial bool MediaFlyoutEnabled { get; set; }

    /// <summary>Flyout 停留时长（毫秒）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    public partial int Duration { get; set; }

    [XmlIgnore]
    public string DurationText
    {
        get => Duration.ToString();
        set
        {
            Duration = int.TryParse(value, out var result)
                ? result switch { > 10000 => 10000, < 0 => 0, _ => result }
                : 3000;
            OnPropertyChanged();
        }
    }

    /// <summary>标题与艺术家居中。</summary>
    [ObservableProperty]
    public partial bool CenterTitleArtist { get; set; }

    /// <summary>是否显示进度条。</summary>
    [ObservableProperty]
    public partial bool SeekbarEnabled { get; set; }

    /// <summary>背景模糊样式（0 = 关闭，1–3 见页面）。</summary>
    [ObservableProperty]
    public partial int MediaFlyoutBackgroundBlur { get; set; }

    /// <summary>是否启用亚克力背景。</summary>
    [ObservableProperty]
    public partial bool MediaFlyoutAcrylicWindowEnabled { get; set; }

    // ---------- 任务栏挂件 ----------

    [ObservableProperty]
    public partial bool TaskbarWidgetEnabled { get; set; }

    [ObservableProperty]
    public partial int TaskbarWidgetSelectedMonitor { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetManualPaddingText))]
    public partial int TaskbarWidgetManualPadding { get; set; }

    [XmlIgnore]
    public string TaskbarWidgetManualPaddingText
    {
        get => TaskbarWidgetManualPadding.ToString();
        set
        {
            TaskbarWidgetManualPadding = int.TryParse(value, out var result)
                ? result switch { > 9999 => 9999, < -9999 => -9999, _ => result }
                : 0;
            OnPropertyChanged();
        }
    }

    /// <summary>无媒体时完全隐藏。</summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetHideCompletely { get; set; }

    [ObservableProperty]
    public partial bool TaskbarWidgetControlsEnabled { get; set; }

    [ObservableProperty]
    public partial bool TaskbarWidgetScrollingEnabled { get; set; }

    [ObservableProperty]
    public partial bool TaskbarWidgetScrollingTextLoopForever { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetScrollingTextSpeedText))]
    public partial int TaskbarWidgetScrollingTextSpeed { get; set; }

    [XmlIgnore]
    public string TaskbarWidgetScrollingTextSpeedText
    {
        get => TaskbarWidgetScrollingTextSpeed.ToString();
        set
        {
            TaskbarWidgetScrollingTextSpeed = int.TryParse(value, out var result)
                ? result switch { > 100 => 100, < 1 => 1, _ => result }
                : 20;
            OnPropertyChanged();
        }
    }

    // ---------- 任务栏歌词（单行） ----------

    /// <summary>是否在挂件右侧显示当前歌词行。</summary>
    [ObservableProperty]
    public partial bool TaskbarLyricsEnabled { get; set; }

    /// <summary>歌词区最大宽度（DIP），超出的部分省略。</summary>
    [ObservableProperty]
    public partial double TaskbarLyricsMaxWidth { get; set; }

    /// <summary>歌词字号。</summary>
    [ObservableProperty]
    public partial double TaskbarLyricsFontSize { get; set; }

    /// <summary>歌词字体；空字符串表示跟随应用字体。</summary>
    [ObservableProperty]
    public partial string TaskbarLyricsFontFamily { get; set; }

    /// <summary>任务栏歌词是否使用卡拉OK逐字填充。</summary>
    [ObservableProperty]
    public partial bool TaskbarLyricsKaraokeEnabled { get; set; }

    // ---------- 任务栏频谱 ----------

    [ObservableProperty]
    public partial bool TaskbarVisualizerEnabled { get; set; }

    /// <summary>运行时状态，不持久化。</summary>
    [XmlIgnore]
    [ObservableProperty]
    public partial bool TaskbarVisualizerHasContent { get; set; }

    [ObservableProperty]
    public partial int TaskbarVisualizerBarCount { get; set; }

    /// <summary>频谱显示宽度（DIP）。</summary>
    [ObservableProperty]
    public partial double TaskbarVisualizerWidth { get; set; }

    [ObservableProperty]
    public partial bool TaskbarVisualizerCenteredBars { get; set; }

    [ObservableProperty]
    public partial bool TaskbarVisualizerBaseline { get; set; }

    [ObservableProperty]
    public partial int TaskbarVisualizerAudioSensitivity { get; set; }

    [ObservableProperty]
    public partial int TaskbarVisualizerAudioPeakLevel { get; set; }

    // ---------- 外观 / 系统 ----------

    /// <summary>主题：0 跟随系统，1 浅色，2 深色。</summary>
    [ObservableProperty]
    public partial int AppTheme { get; set; }

    /// <summary>亚克力模糊不透明度。</summary>
    [ObservableProperty]
    public partial uint AcrylicBlurOpacity { get; set; }

    /// <summary>用专辑封面作为主界面左侧（封面 + 歌词）的背景。</summary>
    [ObservableProperty]
    public partial bool AlbumArtBackground { get; set; }

    /// <summary>背景是否做高斯模糊。</summary>
    [ObservableProperty]
    public partial bool AlbumArtBackgroundBlur { get; set; }

    /// <summary>背景高斯模糊半径（DIP）。</summary>
    [ObservableProperty]
    public partial double AlbumArtBackgroundBlurRadius { get; set; }

    [ObservableProperty]
    public partial bool Startup { get; set; }

    /// <summary>托盘左键行为：0 弹 Flyout，1 显示主窗口，2 播放/暂停。</summary>
    [ObservableProperty]
    public partial int TrayLeftClick { get; set; }

    [ObservableProperty]
    public partial bool TrayIconHide { get; set; }

    /// <summary>全屏应用运行时抑制 Flyout。</summary>
    [ObservableProperty]
    public partial bool DisableIfFullscreen { get; set; }

    [ObservableProperty]
    public partial string FontFamily { get; set; }

    [ObservableProperty]
    public partial Guid Uuid { get; set; }

    // ---------- 初始化 ----------

    [XmlIgnore]
    private bool _initializing = true;

    public UserSettings()
    {
        // 窗口
        PlayerWindowWidth = 600;
        PlayerWindowHeight = 900;
        PlayerWindowLeft = double.NaN;
        PlayerWindowTop = double.NaN;
        PlayerWindowState = WindowState.Normal;

        // 左右两栏默认都打开（与首次运行时的界面一致）
        ShowLyricsPane = true;
        ShowPlaylistPane = true;

        // 播放器
        LastFolder = string.Empty;
        RepeatMode = "LoopPlaylist";
        LastFilePath = string.Empty;
        LastPositionSeconds = 0;
        ResumeLastPlayback = true;

        // 歌词
        LyricFontSize = 18;
        LyricLineSpacing = 14;
        LyricFontFamily = string.Empty;

        // Media Flyout
        FlyoutSelectedMonitor = 0;
        FlyoutAnimationSpeed = 2;
        FlyoutAnimationEasingStyle = 2;
        MediaFlyoutEnabled = true;
        Duration = 3000;
        CenterTitleArtist = false;
        SeekbarEnabled = true;
        MediaFlyoutBackgroundBlur = 0;
        MediaFlyoutAcrylicWindowEnabled = true;

        // 任务栏挂件
        TaskbarWidgetEnabled = false;
        TaskbarWidgetSelectedMonitor = 0;
        TaskbarWidgetManualPadding = 0;
        TaskbarWidgetHideCompletely = false;
        TaskbarWidgetControlsEnabled = false;
        TaskbarWidgetScrollingEnabled = false;
        TaskbarWidgetScrollingTextLoopForever = false;
        TaskbarWidgetScrollingTextSpeed = 20;

        // 任务栏歌词
        TaskbarLyricsEnabled = true;
        TaskbarLyricsMaxWidth = 420;
        TaskbarLyricsFontSize = 12;
        TaskbarLyricsFontFamily = string.Empty;
        TaskbarLyricsKaraokeEnabled = true;

        // 任务栏频谱
        TaskbarVisualizerEnabled = false;
        TaskbarVisualizerHasContent = false;
        TaskbarVisualizerBarCount = 10;
        TaskbarVisualizerWidth = 120;
        TaskbarVisualizerCenteredBars = false;
        TaskbarVisualizerBaseline = false;
        TaskbarVisualizerAudioSensitivity = 2;
        TaskbarVisualizerAudioPeakLevel = 3;

        // 外观 / 系统
        AppTheme = 0;
        AcrylicBlurOpacity = 175;
        AlbumArtBackground = true;
        AlbumArtBackgroundBlur = true;
        AlbumArtBackgroundBlurRadius = 40;
        Startup = false;
        TrayLeftClick = 1;
        TrayIconHide = false;
        DisableIfFullscreen = true;
        FontFamily = "Segoe UI Variable, Microsoft YaHei UI";
        Uuid = Guid.NewGuid();

        PropertyChanged += OnPropertyChangedSaveSettings;
    }

    /// <summary>反序列化完成后调用，开始自动保存。</summary>
    internal void CompleteInitialization() => _initializing = false;

    // ---------- 自动保存（防抖 500ms） ----------

    [XmlIgnore]
    private CancellationTokenSource? _saveSettingsCts;

    private async void OnPropertyChangedSaveSettings(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_initializing) return;
        if (string.IsNullOrEmpty(e.PropertyName) || !PersistedPropertyNames.Contains(e.PropertyName))
            return;

        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _saveSettingsCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();

        try
        {
            await Task.Delay(500, newCts.Token);
            if (ReferenceEquals(_saveSettingsCts, newCts))
                SettingsManager.SaveSettings();
        }
        catch (OperationCanceledException)
        {
            // 被下一次变更取代，正常
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _saveSettingsCts, null, newCts) == newCts)
                newCts.Dispose();
        }
    }

    // ---------- 属性联动 ----------

    partial void OnAppThemeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        Classes.ThemeManager.ApplyAndSaveTheme(newValue);
    }

    partial void OnAcrylicBlurOpacityChanged(uint oldValue, uint newValue)
    {
        if (oldValue == newValue || _initializing) return;
        Classes.WindowBlurHelper.AdjustBlurOpacityForAllWindows(newValue);
    }

    /// <summary>
    /// 记录挂件开关的变更来源。
    ///
    /// 曾出现过「任务栏挂件莫名消失、设置里这一项变成关闭」的情况，
    /// 而配置文件里只有结果、没有来源。这里把调用栈一并写进 crash.log，
    /// 下次再发生时可以直接看出是界面绑定、默认值还是迁移逻辑写进去的。
    /// </summary>
    partial void OnTaskbarWidgetEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;

        var frames = new System.Diagnostics.StackTrace(1, false).GetFrames();
        string who = frames is null
            ? "unknown"
            : string.Join(" | ", frames.Take(5).Select(frame =>
                frame.GetMethod() is { } method
                    ? $"{method.DeclaringType?.Name}.{method.Name}"
                    : "?"));

        QiPlayer.App.Log($"[设置] 任务栏挂件 {oldValue} → {newValue}（来源：{who}）");
    }
}
