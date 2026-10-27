using QiPlayer.Models;

namespace QiPlayer.Services;

/// <summary>
/// 用 taglib-sharp 读取音频标签与内嵌歌词（只读，不写回）。
///
/// 实测结论（480 个 FLAC 样本）：FLAC 与 MP3 的内嵌歌词均由
/// Tag.Lyrics 标准映射命中 —— FLAC → Vorbis LYRICS，MP3 → ID3v2 USLT，
/// 因此无需格式专有适配代码。
/// </summary>
public static class TagReader
{
    /// <summary>
    /// 读取标签并填充到 SongItem。任何异常都被吞掉——
    /// 标签读取失败不应阻断曲目加入列表。
    /// </summary>
    /// <returns>
    /// true 表示标签读取成功；false 表示文件损坏、被占用或无权限。
    /// 扫描器据此决定是否把结果写进缓存——读取失败的文件要留到下次重试，
    /// 不能把空标签永久钉在缓存里。
    /// </returns>
    public static bool FillTags(SongItem song)
    {
        try
        {
            using var file = TagLib.File.Create(song.FilePath);
            var tag = file.Tag;

            song.Title = tag.Title ?? string.Empty;
            song.Artist = tag.FirstPerformer ?? tag.FirstAlbumArtist ?? string.Empty;
            song.Album = tag.Album ?? string.Empty;
            song.Track = tag.Track;
            song.Year = tag.Year;
            song.Genre = tag.FirstGenre ?? string.Empty;

            // 时长：优先用 taglib（不占 BASS 通道），失败则由 BASS 补充
            if (file.Properties?.Duration is { TotalSeconds: > 0 } d)
                song.Duration = d.TotalSeconds;

            // 内嵌歌词：标准映射即可覆盖 FLAC/MP3
            song.EmbeddedLyrics = tag.Lyrics ?? string.Empty;

            return true;
        }
        catch
        {
            // 文件损坏、格式不支持、权限不足等：保留默认值
            return false;
        }
    }

    /// <summary>
    /// 读取内嵌专辑封面原始字节。无封面或读取失败时返回 null。
    /// 封面通常较大（数百 KB），故不随扫描批量读入，而是在切歌时按需读取。
    /// </summary>
    public static byte[]? GetEmbeddedCover(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var pictures = file.Tag.Pictures;
            if (pictures is null || pictures.Length == 0) return null;

            // 优先取「正面封面」，否则取第一张
            var pic = pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover)
                      ?? pictures[0];

            var data = pic.Data?.Data;
            return data is { Length: > 0 } ? data : null;
        }
        catch
        {
            return null;
        }
    }
}