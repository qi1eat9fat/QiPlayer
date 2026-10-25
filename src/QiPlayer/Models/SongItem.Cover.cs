using System.Windows.Media;
using System.Windows.Media.Imaging;
using QiPlayer.Services;

namespace QiPlayer.Models;

/// <summary>
/// SongItem 的界面派生部分：播放列表行左侧的封面缩略图。
///
/// 单独成文件的原因见 SongItem.cs 的类注释 —— 控制台工具（PipeProbe）
/// 只链接纯逻辑文件，不引用 WPF，因此这里把 WPF 依赖隔离出来。
/// </summary>
public sealed partial class SongItem
{
    private ImageSource? _coverThumbnail;
    private bool _coverThumbnailRequested;

    /// <summary>
    /// 列表行使用的封面缩略图。首次访问时在后台解码，完成前返回 null，
    /// 解码完成后触发 PropertyChanged，界面上的 Image 随即淡入。
    ///
    /// 无封面时保持 null，界面显示音符占位图。
    /// </summary>
    public ImageSource? CoverThumbnail
    {
        get
        {
            if (!_coverThumbnailRequested)
            {
                _coverThumbnailRequested = true;
                _ = LoadCoverThumbnailAsync();
            }
            return _coverThumbnail;
        }
    }

    /// <summary>是否已拿到缩略图（用于占位图与图片的互斥显示）。</summary>
    public bool HasCoverThumbnail => _coverThumbnail is not null;

    private async Task LoadCoverThumbnailAsync()
    {
        BitmapImage? image;
        try
        {
            image = await CoverThumbnailService.GetAsync(this);
        }
        catch
        {
            return;
        }

        if (image is null) return;

        // 绑定通常在 UI 线程求值，await 会自然回到 UI 线程；
        // 若被后台代码提前访问，这里兜底切回，避免跨线程改属性。
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ApplyCoverThumbnail(image));
            return;
        }

        ApplyCoverThumbnail(image);
    }

    private void ApplyCoverThumbnail(BitmapImage image)
    {
        _coverThumbnail = image;
        OnPropertyChanged(nameof(CoverThumbnail));
        OnPropertyChanged(nameof(HasCoverThumbnail));
    }
}
