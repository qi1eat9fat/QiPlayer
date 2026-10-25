using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using QiPlayer.ViewModels;

namespace QiPlayer.Classes;

/// <summary>
/// 当前播放状态的统一视图，供 Media Flyout / 任务栏挂件 / Next Up 共用。
///
/// 设计目的：副窗口不直接依赖 <see cref="PlayerViewModel"/> 的完整接口，
/// 只订阅这里暴露的少量属性；日后若要接入其他数据源，只需改这一层。
/// </summary>
public sealed partial class NowPlayingState : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _artist = string.Empty;

    [ObservableProperty]
    private string _album = string.Empty;

    /// <summary>封面缩略图（已冻结，可跨线程）。</summary>
    [ObservableProperty]
    private ImageSource? _cover;

    /// <summary>封面原始位图，供模糊背景裁方使用。</summary>
    [ObservableProperty]
    private BitmapSource? _coverBitmap;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _durationSeconds;

    /// <summary>当前歌词行原文（任务栏单行歌词用）。</summary>
    [ObservableProperty]
    private string _lyricText = string.Empty;

    /// <summary>当前歌词行的翻译，可为空。</summary>
    [ObservableProperty]
    private string _lyricTranslation = string.Empty;

    /// <summary>
    /// 当前歌词行（含逐字时间表）。任务栏单行歌词的卡拉OK效果按它取每个字的起止时间。
    /// </summary>
    [ObservableProperty]
    private LyricLineItem? _lyricLine;

    /// <summary>是否有可显示的曲目。</summary>
    public bool HasTrack => !string.IsNullOrEmpty(Title);

    public string PositionText => TimeSpan.FromSeconds(Math.Max(0, PositionSeconds)).ToString(@"mm\:ss");

    public string DurationText => DurationSeconds <= 0
        ? "--:--"
        : TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");

    partial void OnPositionSecondsChanged(double value) => OnPropertyChanged(nameof(PositionText));
    partial void OnDurationSecondsChanged(double value) => OnPropertyChanged(nameof(DurationText));
    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(HasTrack));

    /// <summary>
    /// 从播放器视图模型同步一次。
    /// 由 <see cref="FlyoutService"/> 在播放器属性变化时调用（已在 UI 线程）。
    /// </summary>
    public void SyncFrom(PlayerViewModel vm)
    {
        Title = vm.CurrentSong?.DisplayTitle ?? string.Empty;
        Artist = vm.CurrentSong?.DisplayArtist ?? string.Empty;
        Album = vm.CurrentSong?.DisplayAlbum ?? string.Empty;
        Cover = vm.AlbumCover;
        CoverBitmap = vm.AlbumCover as BitmapSource;
        IsPlaying = vm.IsPlaying;
        PositionSeconds = vm.PositionSeconds;
        DurationSeconds = vm.DurationSeconds;
        SyncLyric(vm);
    }

    /// <summary>同步当前歌词行（文本 + 逐字时间表）。</summary>
    public void SyncLyric(PlayerViewModel vm)
    {
        LyricText = vm.CurrentLyricText;
        LyricTranslation = vm.CurrentLyricTranslation;

        int index = vm.CurrentLyricIndex;
        LyricLine = index >= 0 && index < vm.LyricLines.Count
            ? vm.LyricLines[index]
            : null;
    }
}
