using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QiPlayer.Audio;
using QiPlayer.Classes;
using QiPlayer.Classes.Settings;
using QiPlayer.Models;
using QiPlayer.Services;

namespace QiPlayer.ViewModels;

/// <summary>
/// 播放模式（三选一）。
///
/// 随机播放不作为独立开关，而是与循环模式并列的一种模式——
/// 这样只用一个按钮即可循环切换，操作路径最短。
/// </summary>
public enum RepeatMode
{
    /// <summary>列表循环：按顺序播放，放完最后一首回到第一首。</summary>
    LoopPlaylist,

    /// <summary>单曲循环：自然播完后重播当前曲目。</summary>
    LoopTrack,

    /// <summary>无序播放：随机排列，每首播放一次后停止。</summary>
    Shuffle,
}

/// <summary>
/// 播放器主视图模型。
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly BassAudioEngine _engine = new();
    private readonly DispatcherTimer _ticker;

    private bool _suppressSeek;

    /// <summary>用户正在拖动进度条：暂停由 ticker 回写位置。</summary>
    public bool IsUserSeeking { get; set; }

    public PlayerViewModel()
    {
        _ticker = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _ticker.Tick += (_, _) => UpdateProgress();

        // 引擎在非 UI 线程触发，切回 UI 线程推进到下一曲
        _engine.TrackEnded += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.Invoke(() => Advance(naturalEnd: true));

        // 列表过滤（搜索）
        PlaylistView = CollectionViewSource.GetDefaultView(Playlist);
        PlaylistView.Filter = FilterSong;

        var settings = SettingsManager.Current;

        // 音量不持久化：每次启动固定为 100%（_volume 的初始值）

        // 恢复播放模式（字符串存储，解析失败回退列表循环）
        RepeatMode = Enum.TryParse<RepeatMode>(settings.RepeatMode, out var mode)
            ? mode
            : RepeatMode.LoopPlaylist;

        if (RepeatMode == RepeatMode.Shuffle) RebuildShuffleOrder();

        // 恢复歌词显示设置
        LyricFontSize = settings.LyricFontSize;
        LyricLineSpacing = settings.LyricLineSpacing;

        // 恢复左右两栏的显隐：上次把某一栏收起来，这次启动就别再弹出来
        ShowLyrics = settings.ShowLyricsPane;
        IsPlaylistVisible = settings.ShowPlaylistPane;

        // 设置面板改的是 UserSettings 上的值，歌词窗口绑的却是这里的两个属性。
        // 不接这一根线的话，拖动「字号 / 行距」滑块的改动要重启才可见。
        settings.PropertyChanged += OnSettingsPropertyChanged;

        // 强调色固定为品牌蓝，与封面无关；启动时先按当前主题应用一次，
        // 避免界面先显示 DesignTokens 里的原色、切歌后才换成压暗后的版本。
        ApplyAccent(AccentPalette.Create());
    }

    // ---------- 循环 / 随机 ----------

    [ObservableProperty]
    private RepeatMode _repeatMode = RepeatMode.LoopPlaylist;

    /// <summary>是否处于无序播放（由 <see cref="RepeatMode"/> 推导，供界面与队列逻辑使用）。</summary>
    public bool Shuffle => RepeatMode == RepeatMode.Shuffle;

    /// <summary>播放模式的下标，供设置面板的下拉框双向绑定。</summary>
    public int RepeatModeIndex
    {
        get => (int)RepeatMode;
        set
        {
            if (value < 0 || value >= Enum.GetValues<RepeatMode>().Length) return;
            RepeatMode = (RepeatMode)value;
        }
    }

    /// <summary>随机播放用的索引排列与游标。</summary>
    private readonly List<int> _shuffleOrder = new();
    private int _shuffleCursor = -1;

    /// <summary>模式按钮的悬停提示。</summary>
    public string RepeatModeTip => RepeatMode switch
    {
        RepeatMode.LoopPlaylist => "列表循环：按顺序播放，结束后回到第一首",
        RepeatMode.LoopTrack => "单曲循环：重复播放当前曲目",
        RepeatMode.Shuffle => "无序播放：随机排列，每首播放一次后停止",
        _ => string.Empty,
    };

    partial void OnRepeatModeChanged(RepeatMode value)
    {
        OnPropertyChanged(nameof(RepeatModeTip));
        OnPropertyChanged(nameof(Shuffle));
        OnPropertyChanged(nameof(RepeatModeIndex));
        OnPropertyChanged(nameof(NextSong));

        SettingsManager.Current.RepeatMode = value.ToString();

        if (value == RepeatMode.Shuffle)
        {
            // 切换到无序：从当前曲目开始，把它放到排列首位
            if (_currentIndex >= 0) AlignShuffleStart(_currentIndex);
            else RebuildShuffleOrder();
        }
        else
        {
            _shuffleOrder.Clear();
            _shuffleCursor = -1;
        }
    }

    /// <summary>点击按钮：在 列表循环 → 单曲循环 → 无序 之间循环。</summary>
    [RelayCommand]
    private void CycleRepeatMode()
    {
        RepeatMode = (RepeatMode)(((int)RepeatMode + 1) % Enum.GetValues<RepeatMode>().Length);
    }

    /// <summary>重建无序播放的随机排列，并把游标定位到当前曲目。</summary>
    private void RebuildShuffleOrder()
    {
        _shuffleOrder.Clear();
        _shuffleCursor = -1;

        int n = Playlist.Count;
        if (n == 0) return;

        for (int i = 0; i < n; i++) _shuffleOrder.Add(i);

        // Fisher–Yates 洗牌
        var rng = Random.Shared;
        for (int i = n - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_shuffleOrder[i], _shuffleOrder[j]) = (_shuffleOrder[j], _shuffleOrder[i]);
        }

        if (_currentIndex >= 0)
            _shuffleCursor = _shuffleOrder.IndexOf(_currentIndex);
    }

    /// <summary>无序播放：前进到排列中的下一首。返回 false 表示已放完。</summary>
    private bool TryAdvanceShuffle()
    {
        if (_shuffleOrder.Count == 0) return false;

        if (_shuffleCursor < 0 || _shuffleCursor >= _shuffleOrder.Count - 1)
            return false;

        _shuffleCursor++;
        // 这是在排列内前移，不能让 PlaySongAt 重新调整排列
        PlaySongAtCore(_shuffleOrder[_shuffleCursor], syncShuffle: false);
        return true;
    }

    /// <summary>
    /// 确保随机排列从指定曲目开始：把该曲目旋转到首位，游标归零。
    ///
    /// 若不做旋转，当前曲目可能恰好排在排列末尾，
    /// 表现为「切到无序播放后放完一首就停了」。
    /// </summary>
    private void AlignShuffleStart(int index)
    {
        if (_shuffleOrder.Count != Playlist.Count)
            RebuildShuffleOrder();

        if (_shuffleOrder.Count == 0) return;

        int pos = _shuffleOrder.IndexOf(index);
        if (pos < 0)
        {
            RebuildShuffleOrder();
            pos = _shuffleOrder.IndexOf(index);
        }

        if (pos <= 0)
        {
            _shuffleCursor = Math.Max(0, pos);
            return;
        }

        var rotated = _shuffleOrder.Skip(pos).Concat(_shuffleOrder.Take(pos)).ToList();
        _shuffleOrder.Clear();
        _shuffleOrder.AddRange(rotated);
        _shuffleCursor = 0;
    }

    // ---------- 界面状态 ----------

    /// <summary>
    /// 全局设置模型。界面直接绑定它，避免为每个设置项写一层代理属性。
    /// </summary>
    public UserSettings Settings => SettingsManager.Current;

    /// <summary>设置文件路径（供设置面板显示）。</summary>
    public string SettingsFilePath => SettingsManager.FilePath;

    /// <summary>当前 BASS 流句柄（供任务栏频谱读取）。</summary>
    public int StreamHandle => _engine.StreamHandle;

    /// <summary>设置面板是否可见（主窗口内覆盖层，不新开窗口）。</summary>
    [ObservableProperty]
    private bool _isSettingsVisible;

    [RelayCommand]
    private void OpenSettings() => IsSettingsVisible = true;

    [RelayCommand]
    private void CloseSettings() => IsSettingsVisible = false;

    /// <summary>播放列表面板是否可见。</summary>
    [ObservableProperty]
    private bool _isPlaylistVisible = true;

    /// <summary>列表当前选中项（供右键菜单使用）。</summary>
    [ObservableProperty]
    private SongItem? _selectedSong;

    /// <summary>列表当前选中数量（含多选）。</summary>
    [ObservableProperty]
    private int _selectionCount;

    // ---------- 响应式布局 ----------
    // 由视图根据窗口宽度设置（见 PlayerWindow.UpdateResponsiveLayout）。
    // 放在 ViewModel 是因为要绑定到 XAML 的列宽/可见性；数值本身由视图决定。

    /// <summary>是否显示中栏歌词。</summary>
    [ObservableProperty]
    private bool _showLyrics = true;

    /// <summary>
    /// 是否显示列表行的时长。窗口变窄时先收掉它，
    /// 让标题与艺术家保住可读宽度（列表已不再按表格列布局）。
    /// </summary>
    [ObservableProperty]
    private bool _showDurationColumn = true;

    // ---------- 引擎信息 ----------

    [ObservableProperty]
    private string _engineStatus = "未初始化";

    // ---------- 播放列表 ----------

    public RangeObservableCollection<SongItem> Playlist { get; } = new();

    /// <summary>带过滤的列表视图，绑定到 ListBox。</summary>
    public ICollectionView PlaylistView { get; }

    [ObservableProperty]
    private SongItem? _currentSong;

    private int _currentIndex = -1;

    [ObservableProperty]
    private string _listStatus = "尚未载入曲目";

    /// <summary>列表底部显示的合计时长（如「共 21 分钟」）。</summary>
    [ObservableProperty]
    private string _listDurationText = string.Empty;

    // ---------- 搜索 ----------

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        PlaylistView.Refresh();
        UpdateListStatus();
    }

    private bool FilterSong(object obj)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        if (obj is not SongItem s) return false;

        return s.DisplayTitle.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || s.DisplayArtist.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || s.Album.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || s.FileName.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>显示/隐藏播放列表面板。</summary>
    [RelayCommand]
    private void TogglePlaylist() => IsPlaylistVisible = !IsPlaylistVisible;

    /// <summary>显示/隐藏歌词面板。</summary>
    [RelayCommand]
    private void ToggleLyrics() => ShowLyrics = !ShowLyrics;

    // 两栏的显隐要跨启动保留：用户收起某一栏通常是长期偏好，不是一次性的临时操作。
    // 属性本身属于视图模型，落盘交给设置对象（它自带 500ms 防抖保存）。
    partial void OnIsPlaylistVisibleChanged(bool value)
        => SettingsManager.Current.ShowPlaylistPane = value;

    partial void OnShowLyricsChanged(bool value)
        => SettingsManager.Current.ShowLyricsPane = value;

    // ---------- 播放状态 ----------

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _durationSeconds;

    /// <summary>音量。不做持久化：每次启动固定为 100%。</summary>
    [ObservableProperty]
    private double _volume = 1.0;

    /// <summary>是否静音（只把输出置零，不清掉滑块上的音量值）。</summary>
    [ObservableProperty]
    private bool _isMuted;

    /// <summary>静音前的音量，用于取消静音时恢复。</summary>
    private double _volumeBeforeMute = 1.0;

    /// <summary>音量百分比，供滑块的悬浮提示使用。</summary>
    public string VolumeText => IsMuted ? "已静音" : $"{Volume * 100:0}%";

    /// <summary>静音按钮的悬浮提示。</summary>
    public string VolumeTip => IsMuted ? "取消静音" : "静音";

    public string PositionText => TimeSpan.FromSeconds(PositionSeconds).ToString(@"mm\:ss");

    public string DurationText => DurationSeconds <= 0
        ? "--:--"
        : TimeSpan.FromSeconds(DurationSeconds).ToString(@"mm\:ss");

    partial void OnPositionSecondsChanged(double value) => OnPropertyChanged(nameof(PositionText));
    partial void OnDurationSecondsChanged(double value) => OnPropertyChanged(nameof(DurationText));

    partial void OnVolumeChanged(double value)
    {
        // 拖动滑块离开 0 即视为取消静音
        if (IsMuted && value > 0.001) IsMuted = false;

        _engine.Volume = IsMuted ? 0 : value;
        OnPropertyChanged(nameof(VolumeText));
    }

    partial void OnIsMutedChanged(bool value)
    {
        _engine.Volume = value ? 0 : Volume;
        OnPropertyChanged(nameof(VolumeText));
        OnPropertyChanged(nameof(VolumeTip));
    }

    /// <summary>点击扬声器图标：静音 / 取消静音。</summary>
    [RelayCommand]
    private void ToggleMute()
    {
        if (!IsMuted)
        {
            _volumeBeforeMute = Volume;
            IsMuted = true;
            return;
        }

        IsMuted = false;

        // 静音前音量本来就是 0：给一个可听的默认值，否则「取消静音」后仍然无声
        if (Volume > 0.001) return;
        Volume = _volumeBeforeMute > 0.001 ? _volumeBeforeMute : 0.5;
    }

    // ---------- 专辑封面 ----------

    /// <summary>当前专辑封面。null 表示无封面，界面显示占位图标。</summary>
    [ObservableProperty]
    private System.Windows.Media.ImageSource? _albumCover;

    /// <summary>是否有封面（用于切换占位图显示）。</summary>
    public bool HasAlbumCover => AlbumCover is not null;

    partial void OnAlbumCoverChanged(System.Windows.Media.ImageSource? value)
        => OnPropertyChanged(nameof(HasAlbumCover));

    // ---------- 强调色 ----------
    //
    // 每首歌取封面的主色作为界面强调色（标题、进度条、当前歌词行、列表高亮）。
    // 画刷由 Classes.AccentPalette 计算，这里只做转发；
    // 界面用 DynamicResource 引用，切歌后自动整体换色。

    /// <summary>当前强调色画刷集合。</summary>
    public AccentColors Accent
    {
        get => _accent;
        private set => SetProperty(ref _accent, value);
    }

    private AccentColors _accent = AccentPalette.Create();

    /// <summary>把强调色写进应用资源，供 DynamicResource 引用。</summary>
    private void ApplyAccent(AccentColors colors)
    {
        Accent = colors;

        var resources = System.Windows.Application.Current?.Resources;
        if (resources is null) return;

        // DynamicResource 在资源字典键值被整体替换后不会刷新，
        // 因此逐键覆盖为新的画刷对象。
        resources["AppAccentBrush"] = colors.Accent;
        resources["AppAccentStrongBrush"] = colors.AccentStrong;
        resources["AppAccentSoftBrush"] = colors.AccentSoft;
        resources["AppAccentSoftStrongBrush"] = colors.AccentSoftStrong;
        resources["AppAccentOnAccentBrush"] = colors.OnAccent;

        // 设置页的开关来自 WPF-UI，默认用系统强调色；
        // 覆盖成同一套颜色，避免与滑杆、进度条出现两种色相。
        resources["ToggleSwitchFillOn"] = colors.Accent;
        resources["ToggleSwitchFillOnPointerOver"] = colors.AccentStrong;
        resources["ToggleSwitchFillOnPressed"] = colors.AccentStrong;
        resources["ToggleSwitchStrokeOn"] = colors.Accent;
    }

    // ---------- 歌词 ----------

    /// <summary>
    /// 当前行字号。其余行按 0.78 倍显示，
    /// 保证「当前行突出、上下文次要」的层次不会因用户调大字号而失效。
    /// </summary>
    [ObservableProperty]
    private double _lyricFontSize = 18;

    /// <summary>普通歌词行的字号（由当前行字号推导）。</summary>
    public double LyricSecondaryFontSize => Math.Round(LyricFontSize * 0.78, 1);

    /// <summary>歌词行之间的垂直间距（像素）。</summary>
    [ObservableProperty]
    private double _lyricLineSpacing = 14;

    partial void OnLyricFontSizeChanged(double value)
    {
        OnPropertyChanged(nameof(LyricSecondaryFontSize));
        SettingsManager.Current.LyricFontSize = value;
    }

    partial void OnLyricLineSpacingChanged(double value)
        => SettingsManager.Current.LyricLineSpacing = value;

    /// <summary>
    /// 把设置面板的改动同步到本对象的歌词属性。
    /// 两边写的是同一个数值，赋值相同时 <see cref="ObservableObject"/> 不会再发通知，
    /// 所以这里不会和上面的回写形成循环。
    /// </summary>
    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(UserSettings.LyricFontSize):
                LyricFontSize = SettingsManager.Current.LyricFontSize;
                break;

            case nameof(UserSettings.LyricLineSpacing):
                LyricLineSpacing = SettingsManager.Current.LyricLineSpacing;
                break;
        }
    }

    [ObservableProperty]
    private Lyrics? _currentLyrics;

    /// <summary>
    /// 供滚动歌词列表绑定的行集合。切歌时重建。
    /// </summary>
    public ObservableCollection<LyricLineItem> LyricLines { get; } = new();

    // ---------- 队列：下一首 ----------

    /// <summary>
    /// 按当前循环/随机模式推算的下一首。
    /// 返回 null 表示放完就停（循环关闭且已到末尾，或列表为空）。
    ///
    /// 注意：这里只做推算，不改变播放状态。
    /// </summary>
    public SongItem? NextSong
    {
        get
        {
            if (Playlist.Count == 0 || _currentIndex < 0) return null;

            // 单曲循环：下一首仍是当前曲目
            if (RepeatMode == RepeatMode.LoopTrack) return Playlist[_currentIndex];

            // 无序：沿随机排列前移，到末尾即结束
            if (RepeatMode == RepeatMode.Shuffle)
            {
                if (_shuffleCursor < 0 || _shuffleOrder.Count == 0) return null;

                return _shuffleCursor < _shuffleOrder.Count - 1
                    ? Playlist[_shuffleOrder[_shuffleCursor + 1]]
                    : null;
            }

            // 列表循环：到末尾回到第一首
            return _currentIndex + 1 < Playlist.Count
                ? Playlist[_currentIndex + 1]
                : Playlist[0];
        }
    }

    /// <summary>当前高亮行在 LyricLines 中的索引，-1 表示无。</summary>
    [ObservableProperty]
    private int _currentLyricIndex = -1;

    [ObservableProperty]
    private string _currentLyricText = string.Empty;

    [ObservableProperty]
    private string _currentLyricTranslation = string.Empty;

    // ---------- 初始化 ----------

    public void Initialize()
    {
        if (_engine.Init())
        {
            var plugins = _engine.LoadedPlugins.Count > 0
                ? string.Join("、", _engine.LoadedPlugins)
                : "（仅 BASS 内建格式）";
            EngineStatus = $"引擎就绪 · {_engine.LoadedPlugins.Count} 个插件：{plugins}";
        }
        else
        {
            EngineStatus = _engine.LastError ?? "引擎初始化失败";
        }
    }

    // ---------- 命令：载入 ----------

    [RelayCommand]
    public async Task LoadFolderAsync(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        ListStatus = "正在扫描…";

        // 扫描在后台线程进行，进度回调来自多个工作线程：
        // 这里节流后转回 UI 线程更新状态文本，避免每读一个文件就排队一次调度。
        long lastProgressTicks = 0;

        var songs = await Task.Run(() => FolderScanner.Scan(
            folder,
            includeSubFolders: true,
            onProgress: (done, total) =>
            {
                // 最后一个进度回调不必再发：扫描完成后紧接着就会更新为「共 N 首」，
                // 迟到的排队回调反而会把状态文本改回「正在扫描」。
                if (done >= total) return;

                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref lastProgressTicks) < 100) return;
                Interlocked.Exchange(ref lastProgressTicks, now);

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                dispatcher?.BeginInvoke(() => ListStatus = $"正在扫描… {done}/{total}");
            }));

        CurrentSong = null;
        _currentIndex = -1;

        // 先清缩略图缓存，再整体替换列表：
        // 扫描结果已按文件名排好序，逐项 Clear + Add 会触发上万次集合通知。
        CoverThumbnailService.Clear();
        Playlist.ReplaceAll(songs);

        // 列表顺序变了，无序播放的排列必须重建
        if (Shuffle) RebuildShuffleOrder();

        UpdateListStatus();

        if (Playlist.Count > 0)
        {
            // 优先恢复上次播放的曲目与位置；没有记录或文件已不存在时从头播放
            if (!TryRestoreLastSession())
                PlaySongAt(0);
        }
    }

    /// <summary>
    /// 恢复上次播放的曲目与位置（不自动播放）。
    /// 返回 false 表示没有可恢复状态。
    /// </summary>
    private bool TryRestoreLastSession()
    {
        if (!SettingsManager.Current.ResumeLastPlayback) return false;

        string path = SettingsManager.Current.LastFilePath;
        double position = SettingsManager.Current.LastPositionSeconds;
        if (string.IsNullOrEmpty(path)) return false;

        var song = Playlist.FirstOrDefault(s => string.Equals(s.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (song is null) return false;

        int index = Playlist.IndexOf(song);
        PlaySongAtCore(index, syncShuffle: true, autoPlay: false, startPosition: position);
        return true;
    }

    // ---------- 命令：播放控制 ----------

    [RelayCommand]
    private void PlayPause()
    {
        if (!_engine.HasStream)
        {
            if (Playlist.Count > 0) PlaySongAt(_currentIndex >= 0 ? _currentIndex : 0);
            return;
        }

        _engine.TogglePlayPause();
        IsPlaying = _engine.State == EngineState.Playing;
    }

    /// <summary>
    /// 下一曲（用户手动点击）。
    /// 与「自然播放结束」的区别：手动切歌在单曲循环/单曲播放下仍应前进，
    /// 否则用户会觉得按钮失灵。
    /// </summary>
    [RelayCommand]
    private void PlayNext() => Advance(naturalEnd: false);

    /// <summary>上一曲。</summary>
    [RelayCommand]
    private void PlayPrevious() => GoBack();

    /// <summary>
    /// 推进到下一曲。播放模式在此统一处理。
    /// </summary>
    /// <param name="naturalEnd">true 表示曲目自然播完；false 表示用户手动切歌。</param>
    private void Advance(bool naturalEnd)
    {
        if (Playlist.Count == 0) return;

        // 单曲循环：自然播完后重播当前曲目。
        // 手动点「下一曲」时仍应前进，否则用户会觉得按钮失灵。
        if (RepeatMode == RepeatMode.LoopTrack && naturalEnd)
        {
            PlaySongAt(_currentIndex);
            return;
        }

        // 无序：沿随机排列前移，放完即停
        if (RepeatMode == RepeatMode.Shuffle)
        {
            if (!TryAdvanceShuffle()) StopInternal();
            return;
        }

        // 列表循环：下一首，到末尾回到第一首
        PlaySongAt(_currentIndex + 1 < Playlist.Count ? _currentIndex + 1 : 0);
    }

    /// <summary>上一曲。随机模式下沿随机排列回退，其余按列表回退。</summary>
    private void GoBack()
    {
        if (Playlist.Count == 0) return;

        if (RepeatMode == RepeatMode.Shuffle && _shuffleCursor > 0)
        {
            _shuffleCursor--;
            PlaySongAtCore(_shuffleOrder[_shuffleCursor], syncShuffle: false);
            return;
        }

        if (_currentIndex > 0)
        {
            PlaySongAt(_currentIndex - 1);
        }
        else
        {
            // 列表循环/单曲循环下，第一首再往前回到末尾
            PlaySongAt(Playlist.Count - 1);
        }
    }

    /// <summary>停止播放并复位状态。</summary>
    private void StopInternal()
    {
        _engine.Stop();
        IsPlaying = false;
        PositionSeconds = 0;
        _ticker.Stop();

        foreach (var s in Playlist) s.IsCurrent = false;
        _currentIndex = -1;
        CurrentSong = null;
        CurrentLyrics = null;
        LyricLines.Clear();
        AlbumCover = null;
        CurrentLyricIndex = -1;
        CurrentLyricText = string.Empty;
        CurrentLyricTranslation = string.Empty;
    }

    [RelayCommand]
    private void Stop()
    {
        _engine.Stop();
        IsPlaying = false;
        PositionSeconds = 0;
    }

    /// <summary>后退 5 秒。</summary>
    [RelayCommand]
    private void SeekBackward() => SeekBy(-5);

    /// <summary>前进 5 秒。</summary>
    [RelayCommand]
    private void SeekForward() => SeekBy(+5);

    private void SeekBy(double deltaSeconds)
    {
        if (!_engine.HasStream) return;

        double target = Math.Clamp(PositionSeconds + deltaSeconds, 0, DurationSeconds);
        _engine.SeekTo(target);

        PositionSeconds = target;
        UpdateCurrentLyric();
    }

    /// <summary>双击列表项时播放。</summary>
    [RelayCommand]
    private void PlaySelected(SongItem? song)
    {
        if (song is null) return;
        PlaySongAt(Playlist.IndexOf(song));
    }

    /// <summary>从列表中移除选中曲目（仅移出列表，不动磁盘文件）。</summary>
    [RelayCommand]
    private void RemoveFromList(SongItem? song)
    {
        if (song is null) return;
        RemoveSongs(new[] { song });
    }

    /// <summary>
    /// 把选中曲目移入回收站并从列表移除。
    /// 实际删除由视图确认后调用，此处只负责执行与状态同步。
    /// </summary>
    public async Task<(int deleted, string? error)> DeleteFromDiskAsync(
        IReadOnlyList<SongItem> songs,
        IntPtr ownerWindow)
    {
        if (songs.Count == 0) return (0, "未选择曲目");

        var paths = songs.Select(s => s.FilePath).ToList();

        var (ok, error) = await Task.Run(() =>
        {
            bool r = RecycleService.MoveToRecycleBin(paths, ownerWindow, out var err);
            return (r, err);
        });

        if (!ok)
            return (0, error ?? "删除失败");

        // 删除成功后再从列表移除（避免删除失败却已从列表消失）
        RemoveSongs(songs);

        return (songs.Count, null);
    }

    /// <summary>
    /// 从播放列表移除若干曲目，并修正当前播放索引。
    /// 不会触碰磁盘文件。
    /// </summary>
    private void RemoveSongs(IReadOnlyCollection<SongItem> songs)
    {
        var current = CurrentSong;
        bool currentRemoved = false;

        // 先判断当前曲目是否在被移除之列
        if (current is not null && songs.Contains(current))
            currentRemoved = true;

        foreach (var s in songs)
            Playlist.Remove(s);

        if (currentRemoved)
        {
            // 当前曲目被移除：停止播放，等待用户重新选择
            _engine.Stop();
            CurrentSong = null;
            _currentIndex = -1;
            IsPlaying = false;
            PositionSeconds = 0;
            DurationSeconds = 0;
            CurrentLyricText = string.Empty;
            CurrentLyricTranslation = string.Empty;
                CurrentLyrics = null;
            AlbumCover = null;

            foreach (var s in Playlist) s.IsCurrent = false;
        }
        else if (current is not null)
        {
            // 当前曲目仍在列表中，重新定位其索引
            _currentIndex = Playlist.IndexOf(current);
        }

        // 删除曲目后索引整体前移，无序排列需重建
        if (Shuffle) RebuildShuffleOrder();

        UpdateListStatus();
    }

    /// <summary>
    /// 播放列表中指定位置的曲目（自动环绕）。
    /// </summary>
    public void PlaySongAt(int index) => PlaySongAtCore(index, syncShuffle: true);

    /// <summary>
    /// 播放指定位置的曲目。
    /// </summary>
    /// <param name="syncShuffle">
    /// true：若处于无序模式，把随机排列旋转到以该曲目开头（用于用户选曲/切模式）。
    /// false：在排列内部前移，不要重新排列（用于「下一曲」）。
    /// </param>
    private void PlaySongAtCore(int index, bool syncShuffle, bool autoPlay = true, double startPosition = 0)
    {
        if (Playlist.Count == 0) return;

        if (index < 0) index = Playlist.Count - 1;
        if (index >= Playlist.Count) index = 0;

        var song = Playlist[index];
        if (!_engine.Load(song.FilePath, autoPlay))
        {
            EngineStatus = _engine.LastError ?? "播放失败";
            return;
        }

        // 高亮标记：先清除旧的，再标记新的
        foreach (var s in Playlist) s.IsCurrent = false;
        song.IsCurrent = true;
        _currentIndex = index;
        CurrentSong = song;

        // 无序播放：把随机排列旋转到以当前曲目开头。
        // 只调整排列起点，不重建（否则每次切歌都会重新洗牌）。
        if (Shuffle && syncShuffle)
            AlignShuffleStart(index);

        DurationSeconds = _engine.Duration;
        IsPlaying = autoPlay;

        // 恢复上次位置：定位后同步刷新歌词高亮
        if (!autoPlay && startPosition > 0)
        {
            double clamped = Math.Clamp(startPosition, 0, DurationSeconds);
            _engine.SeekTo(clamped);
            PositionSeconds = clamped;
        }
        else
        {
            PositionSeconds = 0;
        }

        App.Log($"[播放] #{_currentIndex} {song.DisplayTitle}（循环：{RepeatMode}，随机：{Shuffle}，自动播放={autoPlay}）");

        LoadLyrics(song);
        if (!autoPlay) UpdateCurrentLyric();

        _ = LoadCoverAsync(song);
        _ticker.Start();

        // 切歌后立即记录，避免异常退出时丢失
        SavePlaybackState(song.FilePath, PositionSeconds);

        // 队列变化：通知 Next Up 与任务栏
        OnPropertyChanged(nameof(NextSong));
    }

    /// <summary>
    /// 异步加载封面：文件读取与解码放到后台线程，避免切歌时界面卡顿。
    /// </summary>
    private async Task LoadCoverAsync(SongItem song)
    {
        // 立即清空，避免上一首的封面残留
        AlbumCover = null;

        // 解码放后台线程，避免切歌时卡住界面。
        var image = await Task.Run<System.Windows.Media.ImageSource?>(() =>
        {
            try
            {
                var bytes = CoverResolver.Resolve(song);
                if (bytes is null) return null;

                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                bitmap.StreamSource = new MemoryStream(bytes);
                bitmap.EndInit();
                bitmap.Freeze();   // 冻结后即可跨线程使用
                return bitmap;
            }
            catch
            {
                return null;   // 图片损坏或格式不支持
            }
        });

        // 用户可能在解码期间又切了歌，确认仍是同一首再赋值
        if (!ReferenceEquals(CurrentSong, song)) return;

        // 强调色固定为品牌蓝，换歌不再重算，只更新封面本身。
        AlbumCover = image;
    }

    // ---------- 内部 ----------

    private void UpdateListStatus()
    {
        int total = Playlist.Count;

        // 计数与合计时长一次遍历完成：视图在大曲库下重新枚举两遍是纯浪费
        int shown = 0;
        double seconds = 0;
        foreach (var item in PlaylistView)
        {
            shown++;
            if (item is SongItem song) seconds += song.Duration;
        }

        ListStatus = string.IsNullOrWhiteSpace(SearchText)
            ? $"共 {total} 首"
            : $"共 {total} 首 · 匹配 {shown} 首";

        // 列表底部显示的合计时长：只统计当前可见（过滤后）的曲目
        ListDurationText = seconds <= 0
            ? string.Empty
            : $"共 {FormatTotalDuration(seconds)}";
    }

    /// <summary>把总秒数格式化成「1 小时 23 分钟」或「21 分钟」。</summary>
    private static string FormatTotalDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);

        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours} 小时 {span.Minutes} 分钟";

        return span.TotalMinutes < 1 ? "不足 1 分钟" : $"{(int)span.TotalMinutes} 分钟";
    }

    private void LoadLyrics(SongItem song)
    {
        string raw = song.EmbeddedLyrics;

        if (string.IsNullOrWhiteSpace(raw))
        {
            string lrcPath = Path.ChangeExtension(song.FilePath, ".lrc");
            if (File.Exists(lrcPath))
            {
                try { raw = File.ReadAllText(lrcPath); }
                catch { raw = string.Empty; }
            }
        }

        var lyrics = LyricParser.Parse(raw);
        CurrentLyrics = lyrics.IsEmpty ? null : lyrics;

        // 重建滚动歌词行集合
        LyricLines.Clear();
        if (!lyrics.IsEmpty)
        {
            var lines = lyrics.Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                TimeSpan? nextTime = i + 1 < lines.Count ? lines[i + 1].Time : null;
                LyricLines.Add(new LyricLineItem(line, ComputeLineEnd(line, nextTime)));
            }
        }

        CurrentLyricIndex = -1;

        // 空字符串表示"无歌词"：任务栏歌词据此隐藏，
        // 主界面由 XAML 的「（无歌词）」提示单独负责。
        CurrentLyricText = string.Empty;
        CurrentLyricTranslation = string.Empty;

        // 通知视图滚动回顶部
        LyricLineChanged?.Invoke(this, -1);
    }

    /// <summary>
    /// 推算一行的结束时间，供卡拉OK填充使用。
    ///
    ///   · 有下一行：填到下一行开始（长间奏截到 10 秒，避免填充慢得看不见）；
    ///   · 末行且有逐字标签：以最后一个字的时间为准；
    ///   · 其余：默认 4 秒填完。
    /// </summary>
    private static TimeSpan ComputeLineEnd(LyricLine line, TimeSpan? nextTime)
    {
        if (nextTime is { } next && next > line.Time)
        {
            var span = next - line.Time;
            var maxSpan = TimeSpan.FromSeconds(10);
            return line.Time + (span > maxSpan ? maxSpan : span);
        }

        if (line.WordTimes.Count > 0)
        {
            var last = line.WordTimes[^1];
            if (last < line.Time) last = line.Time;
            return last + TimeSpan.FromSeconds(1.5);
        }

        return line.Time + TimeSpan.FromSeconds(4);
    }

    /// <summary>
    /// 当前高亮行变化时触发，供视图滚动到该行。
    /// 用事件而非绑定，因为「滚动到指定项」属视图行为，ViewModel 不应依赖 ListBox。
    /// </summary>
    public event EventHandler<int>? LyricLineChanged;

    private void UpdateProgress()
    {
        if (!_engine.HasStream) return;

        IsPlaying = _engine.State == EngineState.Playing;

        if (!IsUserSeeking)
        {
            _suppressSeek = true;
            PositionSeconds = _engine.Position;
            _suppressSeek = false;

            UpdateCurrentLyric();
        }

        // 每 5 秒持久化一次播放位置，用于下次启动恢复
        if ((DateTime.UtcNow - _lastPositionSave).TotalSeconds >= 5)
        {
            _lastPositionSave = DateTime.UtcNow;
            if (CurrentSong is not null)
                SavePlaybackState(CurrentSong.FilePath, PositionSeconds);
        }
    }

    /// <summary>
    /// 记录上次播放位置。
    /// 走设置模型的自动保存（防抖 500ms），不必每次手动落盘。
    /// </summary>
    private static void SavePlaybackState(string? filePath, double positionSeconds)
    {
        SettingsManager.Current.LastFilePath = filePath ?? string.Empty;
        SettingsManager.Current.LastPositionSeconds = Math.Max(0, positionSeconds);
    }

    private DateTime _lastPositionSave = DateTime.MinValue;

    private void UpdateCurrentLyric()
    {
        var lyrics = CurrentLyrics;
        if (lyrics is null || lyrics.Lines.Count == 0) return;

        var pos = TimeSpan.FromSeconds(PositionSeconds);

        int idx = -1;
        for (int i = 0; i < lyrics.Lines.Count; i++)
        {
            if (lyrics.Lines[i].Time <= pos) idx = i;
            else break;
        }

        if (idx == CurrentLyricIndex) return;

        // 切换行高亮：清除旧行、标记新行
        if (CurrentLyricIndex >= 0 && CurrentLyricIndex < LyricLines.Count)
            LyricLines[CurrentLyricIndex].IsCurrent = false;
        if (idx >= 0 && idx < LyricLines.Count)
            LyricLines[idx].IsCurrent = true;

        CurrentLyricIndex = idx;
        if (idx >= 0)
        {
            CurrentLyricText = lyrics.Lines[idx].Text;
            CurrentLyricTranslation = lyrics.Lines[idx].Translation;
        }
        else
        {
            CurrentLyricText = string.Empty;
            CurrentLyricTranslation = string.Empty;
            }

        App.Log($"歌词行切换到 #{idx}（共 {LyricLines.Count} 行）");

        // 通知视图滚动到当前行
        LyricLineChanged?.Invoke(this, idx);
    }

    public void SeekTo(double seconds)
    {
        if (_suppressSeek) return;
        _engine.SeekTo(seconds);
        UpdateCurrentLyric();
    }

    public void Dispose()
    {
        // 退出前记录当前位置
        if (CurrentSong is not null)
            SavePlaybackState(CurrentSong.FilePath, PositionSeconds);

        _ticker.Stop();
        _engine.Dispose();
    }
}