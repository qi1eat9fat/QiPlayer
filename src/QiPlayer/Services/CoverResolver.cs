using System.IO;
using QiPlayer.Models;

namespace QiPlayer.Services;

/// <summary>
/// 专辑封面解析。移植自 MusicPlayer2 的 CMusicPlayerCmdHelper::SearchAlbumCover。
///
/// 查找优先级：
///   1. 音频文件的内嵌封面
///   2. 与歌曲文件同名的图片（如 "Song.flac" → "Song.jpg"）
///   3. 以专辑名为文件名的图片（如 "Album Name.jpg"）
///   4. 目录下的默认封面文件名（cover / folder / front / album 等）
/// </summary>
public static class CoverResolver
{
    /// <summary>外部封面候选扩展名。</summary>
    private static readonly string[] ImageExtensions =
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp",
    };

    /// <summary>
    /// 默认封面文件名（不含扩展名），对应 MusicPlayer2 的 default_album_name。
    /// 按顺序查找，先命中者优先。
    /// </summary>
    private static readonly string[] DefaultCoverNames =
    {
        "cover", "folder", "front", "album", "albumart", "封面",
    };

    /// <summary>
    /// 解析封面图片数据（原始字节）。
    /// 返回 null 表示未找到任何封面。
    /// </summary>
    public static byte[]? Resolve(SongItem song)
    {
        // 1. 内嵌封面
        var embedded = TagReader.GetEmbeddedCover(song.FilePath);
        if (embedded is not null) return embedded;

        string dir = song.Directory;
        if (!Directory.Exists(dir)) return null;

        // 2. 与歌曲同名的图片
        string baseName = Path.GetFileNameWithoutExtension(song.FilePath);
        var hit = FindImage(dir, baseName);
        if (hit is not null) return hit;

        // 3. 以专辑名为文件名的图片
        if (!string.IsNullOrWhiteSpace(song.Album))
        {
            hit = FindImage(dir, SanitizeFileName(song.Album));
            if (hit is not null) return hit;
        }

        // 4. 默认封面文件名
        foreach (var name in DefaultCoverNames)
        {
            hit = FindImage(dir, name);
            if (hit is not null) return hit;
        }

        return null;
    }

    /// <summary>
    /// 在目录中查找指定基名的图片文件（忽略扩展名大小写），返回文件内容。
    /// </summary>
    private static byte[]? FindImage(string dir, string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName)) return null;

        foreach (var ext in ImageExtensions)
        {
            string path = Path.Combine(dir, baseName + ext);
            if (!File.Exists(path)) continue;

            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length > 0) return bytes;
            }
            catch
            {
                // 文件被占用或无权限：跳到下一个候选
            }
        }
        return null;
    }

    /// <summary>
    /// 把专辑名规整为可用作文件名的形式（对应 MusicPlayer2 的 FileNameNormalize）。
    /// 去掉路径分隔符等非法字符。
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }
}