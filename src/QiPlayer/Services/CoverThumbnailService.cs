using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QiPlayer.Models;

namespace QiPlayer.Services;

/// <summary>
/// 播放列表行的封面缩略图。
///
/// 与主封面（<see cref="CoverResolver"/> + 完整解码）不同，这里只解码到 96px：
/// 480 首曲目全部解码原图会占用数百 MB 内存，缩略图则不到 10 MB。
/// 解码放在后台线程，界面用异步绑定取值，滚动列表时不会卡顿。
///
/// 缓存按「文件路径 + 修改时间」作键：重打标签后封面会变，
/// 只用路径会让旧缩略图一直留在缓存里。
/// </summary>
public static class CoverThumbnailService
{
    /// <summary>缩略图解码宽度。列表行高约 44，2 倍屏下留足余量。</summary>
    private const int DecodePixelWidth = 96;

    /// <summary>缓存上限。超过后清空重建——缩略图每张约 20 KB，600 张也才十余 MB。</summary>
    private const int CacheLimit = 600;

    private static readonly ConcurrentDictionary<string, BitmapImage?> Cache = new();

    /// <summary>
    /// 取指定曲目的封面缩略图（后台线程解码，可 await）。
    /// 无封面时返回 null。
    /// </summary>
    public static Task<BitmapImage?> GetAsync(SongItem song)
        => Task.Run(() => Get(song));

    /// <summary>同步取值（已在后台线程时使用）。</summary>
    public static BitmapImage? Get(SongItem song)
    {
        string key = BuildKey(song);

        if (Cache.TryGetValue(key, out var cached)) return cached;

        var image = Decode(song);

        if (Cache.Count >= CacheLimit) Cache.Clear();
        Cache[key] = image;

        return image;
    }

    private static string BuildKey(SongItem song)
    {
        // 文件不存在时直接按路径缓存，避免每行都去访问磁盘
        try
        {
            var info = new FileInfo(song.FilePath);
            return info.Exists
                ? $"{song.FilePath}|{info.LastWriteTimeUtc.Ticks}"
                : song.FilePath;
        }
        catch
        {
            return song.FilePath;
        }
    }

    private static BitmapImage? Decode(SongItem song)
    {
        try
        {
            var bytes = CoverResolver.Resolve(song);
            if (bytes is null || bytes.Length == 0) return null;

            using var stream = new MemoryStream(bytes);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = DecodePixelWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();   // 冻结后可跨线程使用
            return image;
        }
        catch
        {
            return null;   // 图片损坏或格式不支持
        }
    }

    /// <summary>清空缓存（重新扫描目录后调用，避免残留已移除文件的缩略图）。</summary>
    public static void Clear() => Cache.Clear();
}
