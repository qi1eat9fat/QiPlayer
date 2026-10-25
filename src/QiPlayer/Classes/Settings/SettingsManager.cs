using System.IO;
using System.Xml.Serialization;

namespace QiPlayer.Classes.Settings;

/// <summary>
/// 设置持久化。
///
/// 与 FluentFlyout 的差异：文件放在**程序目录**（便携模式），
/// 拷贝整个程序目录即可连同设置一起迁移。
///
/// 注意：便携模式与 MSIX 不兼容（安装目录只读）。本项目走非打包分发；
/// 若日后上架，改为 %AppData% 即可（只需改 SettingsFilePath）。
/// </summary>
public static class SettingsManager
{
    private static readonly object SettingsFileLock = new();

    private static string SettingsFilePath => Path.Combine(
        AppContext.BaseDirectory,
        "QiPlayer.settings.xml");

    /// <summary>设置文件完整路径（供设置面板显示）。</summary>
    public static string FilePath => SettingsFilePath;

    private static UserSettings? _current;
    private static XmlSerializer? _exportSerializer;

    private static XmlSerializer GetExportSerializer()
    {
        if (_exportSerializer == null)
        {
            var overrides = new XmlAttributeOverrides();
            var ignoreAttrs = new XmlAttributes { XmlIgnore = true };
            overrides.Add(typeof(UserSettings), nameof(UserSettings.Uuid), ignoreAttrs);
            _exportSerializer = new XmlSerializer(typeof(UserSettings), overrides);
        }
        return _exportSerializer;
    }

    /// <summary>当前设置（首次访问时创建默认值）。</summary>
    public static UserSettings Current
    {
        get => _current ??= new UserSettings();
        set => _current = value;
    }

    private static bool Deserialize(string filePath, out UserSettings? settings)
    {
        settings = null;
        if (!File.Exists(filePath)) return false;

        using var reader = new StreamReader(filePath);
        var serializer = new XmlSerializer(typeof(UserSettings));
        settings = (UserSettings?)serializer.Deserialize(reader);
        return settings != null;
    }

    /// <summary>
    /// 从设置文件恢复。filePath 非空表示导入。
    /// 主文件损坏时回退 .bak；都失败则使用默认值。
    /// </summary>
    public static UserSettings RestoreSettings(string? filePath = null)
    {
        bool isImport = filePath != null;
        filePath ??= SettingsFilePath;
        string backupPath = filePath + ".bak";

        try
        {
            if (Deserialize(filePath, out var loaded) && loaded != null)
            {
                if (isImport && _current != null)
                    loaded.Uuid = _current.Uuid;

                _current = loaded;
                _current.CompleteInitialization();
                return _current;
            }
        }
        catch
        {
            // 主文件不可读，尝试备份
        }

        try
        {
            if (Deserialize(backupPath, out var backup) && backup != null)
            {
                _current = backup;
                _current.CompleteInitialization();
                return _current;
            }
        }
        catch
        {
            // 备份也不可用
        }

        _current = new UserSettings();
        _current.CompleteInitialization();
        return _current;
    }

    /// <summary>保存到设置文件（原子替换 + 保留上一版备份）。</summary>
    public static void SaveSettings(string? filePath = null)
    {
        bool isExport = filePath != null;
        filePath ??= SettingsFilePath;
        string tempPath = filePath + ".tmp";
        string backupPath = filePath + ".bak";

        try
        {
            lock (SettingsFileLock)
            {
                string? directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                _current ??= new UserSettings();

                using (var writer = new StreamWriter(tempPath, false))
                {
                    var serializer = isExport ? GetExportSerializer() : new XmlSerializer(typeof(UserSettings));
                    serializer.Serialize(writer, _current);
                }

                if (File.Exists(filePath))
                {
                    try
                    {
                        File.Replace(tempPath, filePath, backupPath, ignoreMetadataErrors: true);
                    }
                    catch (IOException)
                    {
                        // 文件被占用时手动替换
                        ManualReplace(filePath, tempPath, backupPath);
                    }
                }
                else
                {
                    File.Move(tempPath, filePath, true);
                }
            }
        }
        catch (Exception ex)
        {
            // 无写权限时静默忽略，不阻断播放；调试期写入日志
            App.Log($"保存设置失败：{filePath}", ex);
            TryDeleteFileIfExists(tempPath);
        }
    }

    private static void ManualReplace(string filePath, string tempPath, string backupPath)
    {
        TryDeleteFileIfExists(backupPath);
        File.Copy(filePath, backupPath);
        TryDeleteFileIfExists(filePath);
        File.Move(tempPath, filePath);
    }

    private static void TryDeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略清理失败
        }
    }
}
