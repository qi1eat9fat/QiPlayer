using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace QiPlayer.Models;

/// <summary>
/// 播放列表中的一首曲目。
/// 标签信息在扫描时一次性读入，之后排序/显示都直接用内存值，不再访问磁盘。
///
/// 手写 INotifyPropertyChanged（而非继承 ObservableObject），
/// 是为了让控制台工具（PipeProbe）无需引用 MVVM 包即可复用本类型。
///
/// 本文件保持「不依赖 WPF」：界面专用的派生属性（封面缩略图）拆在
/// SongItem.Cover.cs，控制台工具只链接本文件，不会把 WPF 依赖带进去。
/// </summary>
public sealed partial class SongItem : INotifyPropertyChanged
{
    /// <summary>文件完整路径。</summary>
    public required string FilePath { get; init; }

    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public uint Track { get; set; }
    public uint Year { get; set; }
    public string Genre { get; set; } = string.Empty;

    /// <summary>时长（秒）。为 0 表示未知。</summary>
    public double Duration { get; set; }

    /// <summary>内嵌歌词。空字符串表示无歌词。</summary>
    public string EmbeddedLyrics { get; set; } = string.Empty;

    private bool _isCurrent;

    /// <summary>是否为当前正在播放的曲目（用于列表高亮）。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    // ---------- 派生显示属性 ----------

    public string FileName => Path.GetFileName(FilePath);

    public string FileNameWithoutExtension => Path.GetFileNameWithoutExtension(FilePath);

    public string Directory => Path.GetDirectoryName(FilePath) ?? string.Empty;

    /// <summary>标题：标签缺失时回退文件名。</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? FileNameWithoutExtension : Title;

    /// <summary>艺术家：标签缺失时显示「未知艺术家」。</summary>
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "未知艺术家" : Artist;

    /// <summary>专辑：缺失时留空（避免列表中整列都是「未知专辑」的噪音）。</summary>
    public string DisplayAlbum => Album;

    /// <summary>时长文本 mm:ss。</summary>
    public string DurationText => Duration <= 0
        ? "--:--"
        : TimeSpan.FromSeconds(Duration).ToString(@"mm\:ss");

    /// <summary>文件扩展名（大写，不含点）。</summary>
    public string FormatText => Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant();

    private string? _fileSizeText;

    /// <summary>
    /// 文件大小文本。惰性读取并缓存——
    /// 悬停提示只在需要时创建，不会在扫描阶段对全部文件做 IO。
    /// </summary>
    public string FileSizeText
    {
        get
        {
            if (_fileSizeText is not null) return _fileSizeText;

            try
            {
                long bytes = new FileInfo(FilePath).Length;
                _fileSizeText = FormatSize(bytes);
            }
            catch
            {
                _fileSizeText = "—";
            }
            return _fileSizeText;
        }
    }

    /// <summary>悬停时显示的详细信息（多行文本）。</summary>
    public string DetailsText =>
        $"标题：{DisplayTitle}\n" +
        $"艺术家：{DisplayArtist}\n" +
        $"专辑：{(string.IsNullOrWhiteSpace(Album) ? "未知" : Album)}\n" +
        $"时长：{DurationText}\n" +
        $"格式：{FormatText}\n" +
        $"大小：{FileSizeText}\n" +
        $"歌词：{(string.IsNullOrWhiteSpace(EmbeddedLyrics) ? "无" : "有")}\n" +
        $"路径：{FilePath}";

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}