using System.IO;
using QiPlayer.Models;

namespace QiPlayer.Services;

/// <summary>
/// 扫描文件夹中的音频文件。
/// 对应 MusicPlayer2 的 CAudioCommon::GetAudioFiles。
/// </summary>
public static class FolderScanner
{
    /// <summary>
    /// 支持的扩展名。与 MusicPlayer2 的格式集对齐，
    /// 但去掉已舍弃的 cue / midi / cd 等。
    /// </summary>
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".mp2", ".mp1",
        ".flac", ".fla",
        ".m4a", ".mp4", ".aac",
        ".wma", ".asf",
        ".ogg", ".oga", ".opus",
        ".ape", ".mac",
        ".wav", ".aif", ".aiff",
        ".wv",
    };

    /// <summary>进度回调的粒度：大曲库下每 25 个文件报一次，避免回调本身成为开销。</summary>
    private const int ProgressInterval = 25;

    /// <summary>判断扩展名是否为支持的音频格式。</summary>
    public static bool IsAudioFile(string path)
        => AudioExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// 枚举文件夹中的音频文件并读取标签。
    /// 返回的列表已按文件名（本地化自然排序）排好，可直接填入播放列表。
    ///
    /// 两个耗时点在这里一起处理：
    ///   · 未变化的文件走 <see cref="LibraryCache"/>，不再解析标签；
    ///   · 新增/变化的文件并行读取标签（标签解析是逐文件的磁盘 IO）。
    /// </summary>
    /// <param name="folder">要扫描的目录。</param>
    /// <param name="includeSubFolders">是否包含子目录。</param>
    /// <param name="onProgress">每处理一批文件回调一次，用于显示进度。可能来自工作线程。</param>
    public static List<SongItem> Scan(
        string folder,
        bool includeSubFolders,
        Action<int, int>? onProgress = null)
    {
        if (!Directory.Exists(folder))
            return new List<SongItem>();

        // 用 EnumerationOptions 而不是 Directory.GetFiles：
        // 后者碰到任意一个无权限子目录就整体抛异常，整个音乐库都会扫不出来。
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubFolders,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            MatchType = MatchType.Simple,
        };

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(folder)
                .EnumerateFiles("*", options)
                .Where(f => IsAudioFile(f.Name))
                .ToList();
        }
        catch
        {
            return new List<SongItem>();
        }

        if (files.Count == 0)
            return new List<SongItem>();

        // 只复用同一目录的缓存：换了目录就整库重读，避免不同曲库的条目混在一起
        var cache = LibraryCache.Load(folder);

        var songs = new SongItem[files.Count];
        var stamps = new (long Size, long Ticks, bool Cacheable)[files.Count];
        int done = 0;

        // 只有真正读过盘才需要重写缓存：全部命中时缓存本来就完整，
        // 每次启动再压缩写一遍只是白白占用磁盘和 CPU。
        int cacheDirty = 0;

        var parallelOptions = new ParallelOptions
        {
            // 上限 8：机械硬盘或网络盘上并发太多随机读反而更慢
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
        };

        Parallel.For(0, files.Count, parallelOptions, i =>
        {
            var file = files[i];
            var song = new SongItem { FilePath = file.FullName };

            long size = -1;
            long ticks = 0;
            bool cacheable = false;

            try
            {
                // 枚举阶段已经把大小与时间带回来了，这里不额外访问磁盘
                size = file.Length;
                ticks = file.LastWriteTimeUtc.Ticks;

                if (cache is not null && cache.TryApply(file.FullName, size, ticks, song))
                {
                    // 命中缓存的条目原样写回，保证缓存始终包含当前全库
                    cacheable = true;
                }
                else
                {
                    // 读不到标签（损坏 / 被占用 / 无权限）时不写缓存：
                    // 留到下次启动重试，避免把空标签永久钉在缓存里
                    cacheable = TagReader.FillTags(song);
                    Volatile.Write(ref cacheDirty, 1);
                }
            }
            catch
            {
                cacheable = false;
            }

            songs[i] = song;
            stamps[i] = (size, ticks, cacheable);

            int n = Interlocked.Increment(ref done);
            if (onProgress is not null && (n == files.Count || n % ProgressInterval == 0))
                onProgress(n, files.Count);
        });

        // 排序放在填充列表之前：界面直接整体替换，不必先无序遍历再重排
        var result = songs
            .OrderBy(s => s.FileName, NaturalStringComparer.Instance)
            .ToList();

        // 缓存留到后台写：本次扫描结果立即可用，不必等落盘
        if (Volatile.Read(ref cacheDirty) != 0)
        {
            var cacheEntries = new List<(SongItem Song, long Size, long Ticks)>(result.Count);
            for (int i = 0; i < songs.Length; i++)
            {
                if (!stamps[i].Cacheable || stamps[i].Size < 0) continue;
                cacheEntries.Add((songs[i], stamps[i].Size, stamps[i].Ticks));
            }

            LibraryCache.SaveAsync(folder, cacheEntries);
        }

        return result;
    }
}
