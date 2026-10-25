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

    /// <summary>判断扩展名是否为支持的音频格式。</summary>
    public static bool IsAudioFile(string path)
        => AudioExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// 枚举文件夹中的音频文件并读取标签。
    /// 返回的列表已按文件名排序。
    /// </summary>
    /// <param name="folder">要扫描的目录。</param>
    /// <param name="includeSubFolders">是否包含子目录。</param>
    /// <param name="onProgress">每处理一个文件回调一次，用于显示进度。</param>
    public static List<SongItem> Scan(
        string folder,
        bool includeSubFolders,
        Action<int, int>? onProgress = null)
    {
        if (!Directory.Exists(folder))
            return new List<SongItem>();

        var searchOption = includeSubFolders
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        string[] files;
        try
        {
            files = Directory.GetFiles(folder, "*.*", searchOption)
                             .Where(IsAudioFile)
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                             .ToArray();
        }
        catch
        {
            return new List<SongItem>();
        }

        var result = new List<SongItem>(files.Length);
        for (int i = 0; i < files.Length; i++)
        {
            var song = new SongItem { FilePath = files[i] };
            TagReader.FillTags(song);
            result.Add(song);
            onProgress?.Invoke(i + 1, files.Length);
        }

        return result;
    }
}