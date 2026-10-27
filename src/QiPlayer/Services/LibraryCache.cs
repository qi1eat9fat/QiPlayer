using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using QiPlayer.Models;

namespace QiPlayer.Services;

/// <summary>
/// 扫描结果的磁盘缓存。
///
/// 启动时重新扫描音乐目录的耗时几乎都花在 TagLib 解析标签上：
/// 每个文件都要打开、读取元数据，内嵌封面大的文件还会把整张图片读进内存。
/// 缓存以「完整路径 + 文件大小 + 最后修改时间」判断文件是否变过，
/// 没变就直接复用上次的标签，只有新增或改动过的文件才真正读盘。
///
/// 缓存文件用 gzip 压缩的 JSON，优先放在程序目录（与便携模式的设置文件一致），
/// 程序目录不可写时回落到 LocalAppData。缓存只是加速手段：
/// 读取失败、损坏或版本不符都会静默退回全量扫描，不影响任何功能。
/// </summary>
public static class LibraryCache
{
    /// <summary>缓存格式版本。字段变化时递增，旧缓存会被直接丢弃。</summary>
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private static string PreferredPath =>
        Path.Combine(AppContext.BaseDirectory, "QiPlayer.library.cache.json.gz");

    private static string FallbackPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QiPlayer",
        "library.cache.json.gz");

    /// <summary>
    /// 一次扫描可用的缓存快照。只在目录匹配时由 <see cref="Load"/> 返回。
    /// </summary>
    public sealed class Snapshot
    {
        private readonly Dictionary<string, Entry> _entries;

        internal Snapshot(Dictionary<string, Entry> entries) => _entries = entries;

        /// <summary>
        /// 文件未变化时把缓存中的标签写入 <paramref name="song"/> 并返回 true。
        /// </summary>
        public bool TryApply(string filePath, long size, long ticks, SongItem song)
        {
            if (!_entries.TryGetValue(filePath, out var entry)) return false;
            if (entry.Size != size || entry.Ticks != ticks) return false;

            song.Title = entry.Title ?? string.Empty;
            song.Artist = entry.Artist ?? string.Empty;
            song.Album = entry.Album ?? string.Empty;
            song.Track = entry.Track;
            song.Year = entry.Year;
            song.Genre = entry.Genre ?? string.Empty;
            song.Duration = entry.Duration;
            song.EmbeddedLyrics = entry.Lyrics ?? string.Empty;
            return true;
        }
    }

    /// <summary>
    /// 载入指定目录的缓存。目录不匹配、文件损坏或版本不符时返回 null。
    /// </summary>
    public static Snapshot? Load(string folder)
    {
        string normalized = NormalizeFolder(folder);

        foreach (string path in CandidatePaths())
        {
            try
            {
                if (!File.Exists(path)) continue;

                using var stream = File.OpenRead(path);
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                var data = JsonSerializer.Deserialize<CacheFile>(gzip, JsonOptions);

                if (data?.Tracks is null) continue;
                if (data.Version != CurrentVersion) continue;
                if (!string.Equals(data.Folder, normalized, StringComparison.OrdinalIgnoreCase)) continue;

                // 反序列化出来的字典不带比较器，这里换成忽略大小写的
                var entries = new Dictionary<string, Entry>(
                    data.Tracks, StringComparer.OrdinalIgnoreCase);

                return new Snapshot(entries);
            }
            catch (Exception ex)
            {
                // 缓存损坏不影响使用：丢掉它，本次全量扫描后会重新写一份
                App.Log($"读取音乐库缓存失败：{path}", ex);
            }
        }

        return null;
    }

    /// <summary>
    /// 后台写入一份新的缓存。调用方不必等待落盘。
    /// </summary>
    /// <param name="tracks">本次扫描成功读到标签（或直接命中缓存）的曲目及其文件标识。</param>
    public static void SaveAsync(
        string folder,
        IReadOnlyList<(SongItem Song, long Size, long Ticks)> tracks)
    {
        if (tracks.Count == 0) return;

        // 先在当前线程把数据抄成轻量结构：后台写盘期间 SongItem 仍可能被播放流程访问
        var data = new CacheFile
        {
            Version = CurrentVersion,
            Folder = NormalizeFolder(folder),
            Tracks = new Dictionary<string, Entry>(tracks.Count, StringComparer.OrdinalIgnoreCase),
        };

        foreach (var (song, size, ticks) in tracks)
        {
            data.Tracks[song.FilePath] = new Entry
            {
                Size = size,
                Ticks = ticks,
                Title = song.Title,
                Artist = song.Artist,
                Album = song.Album,
                Track = song.Track,
                Year = song.Year,
                Genre = song.Genre,
                Duration = song.Duration,
                Lyrics = song.EmbeddedLyrics,
            };
        }

        Task.Run(() => Write(data));
    }

    private static void Write(CacheFile data)
    {
        foreach (string path in CandidatePaths())
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string temp = path + ".tmp";
                using (var stream = File.Create(temp))
                using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
                {
                    JsonSerializer.Serialize(gzip, data, JsonOptions);
                }

                // 原子替换：写到一半被打断也不会留下半截缓存
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex)
            {
                App.Log($"写入音乐库缓存失败：{path}", ex);
            }
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return PreferredPath;
        yield return FallbackPath;
    }

    /// <summary>
    /// 统一目录写法：同一目录经命令行、文件夹选择框传入时可能差一个结尾分隔符
    /// 或大小写，不归一化会导致缓存永远对不上、每次启动都整库重扫。
    /// </summary>
    private static string NormalizeFolder(string folder)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch
        {
            return folder;
        }
    }

    private sealed class CacheFile
    {
        public int Version { get; set; }
        public string Folder { get; set; } = string.Empty;
        public Dictionary<string, Entry> Tracks { get; set; } = new();
    }

    /// <summary>单曲缓存项。</summary>
    internal sealed class Entry
    {
        /// <summary>文件字节数，用于判断文件是否被替换。</summary>
        public long Size { get; set; }

        /// <summary>最后写入时间（UTC ticks），用于判断文件是否被重新打标签。</summary>
        public long Ticks { get; set; }

        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public uint Track { get; set; }
        public uint Year { get; set; }
        public string Genre { get; set; } = string.Empty;
        public double Duration { get; set; }
        public string Lyrics { get; set; } = string.Empty;
    }
}
