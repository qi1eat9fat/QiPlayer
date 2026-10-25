using System.IO;
using ManagedBass;

namespace QiPlayer.Audio;

/// <summary>
/// 播放状态。
/// </summary>
public enum EngineState
{
    Stopped,
    Paused,
    Playing,
}

/// <summary>
/// 基于 BASS 的音频引擎封装。
///
/// 只做「打开 → 播放 → 暂停 → 跳转 → 音量」这条最小链路，
/// 不包含均衡器/混响/变速变调（那些已从迁移范围中删去）。
///
/// 生命周期：Init() 一次 → 反复 Load/Play → Dispose() 一次。
/// </summary>
public sealed class BassAudioEngine : IDisposable
{
    // 需要按顺序加载的格式插件。文件名须与输出目录中的 DLL 一致。
    // 注意：un4seen 的 APE 插件文件名是 bassape.dll（无下划线）。
    private static readonly string[] PluginFiles =
    {
        "bassflac.dll",
        "bass_aac.dll",
        "bassape.dll",
        "basswma.dll",
    };

    private int _stream;                 // 当前流句柄，0 表示无
    private bool _initialized;
    private readonly List<int> _pluginHandles = new();

    // 目标音量：新流创建时 BASS 默认音量为 1.0，需要主动套用，
    // 否则切歌后音量会被重置（用户调好的音量失效）。
    private double _volume = 1.0;

    /// <summary>曲目自然播放结束时触发。</summary>
    public event EventHandler? TrackEnded;

    /// <summary>流状态变化时触发（播放/暂停/停止）。</summary>
    public event EventHandler? StateChanged;

    public EngineState State { get; private set; } = EngineState.Stopped;

    /// <summary>已成功加载的插件名。</summary>
    public IReadOnlyList<string> LoadedPlugins { get; private set; } = Array.Empty<string>();

    /// <summary>初始化输出设备并加载格式插件。返回是否成功。</summary>
    public bool Init()
    {
        if (_initialized) return true;

        // 默认输出设备，44100Hz。采样率会在打开流时按文件实际值处理。
        if (!Bass.Init(-1, 44100))
        {
            LastError = $"Bass.Init 失败：{Bass.LastError}";
            return false;
        }

        _initialized = true;
        SyncPlugins();

        return true;
    }

    /// <summary>最近一次错误信息。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 加载文件。
    /// </summary>
    /// <param name="autoPlay">
    /// true：加载后立即播放；
    /// false：加载后停在 0 秒（可再 SeekTo 到指定位置，用于「恢复上次播放位置」）。
    /// </param>
    public bool Load(string filePath, bool autoPlay)
    {
        if (!_initialized)
        {
            LastError = "引擎未初始化";
            return false;
        }

        if (!File.Exists(filePath))
        {
            LastError = $"文件不存在：{filePath}";
            return false;
        }

        CloseStream();

        // BassFlags.Default：解码为 32 位浮点，交由 BASS 选定输出格式
        int stream = Bass.CreateStream(filePath, 0, 0, BassFlags.Default);
        if (stream == 0)
        {
            LastError = $"无法打开文件（{Bass.LastError}）。若是 FLAC/APE 等格式，请确认对应插件已加载。";
            return false;
        }

        _stream = stream;

        // 新流默认音量 1.0，套用当前音量，避免切歌后音量被重置
        Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, _volume);

        // 注册结束同步：曲目播完自动触发 TrackEnded
        Bass.ChannelSetSync(_stream, SyncFlags.End, 0, (_, _, _, _) =>
        {
            State = EngineState.Stopped;
            TrackEnded?.Invoke(this, EventArgs.Empty);
        });

        if (autoPlay)
        {
            if (!Bass.ChannelPlay(_stream))
            {
                LastError = $"播放失败：{Bass.LastError}";
                return false;
            }

            State = EngineState.Playing;
        }
        else
        {
            // 只加载不播放：流已就绪，可读取时长、封面、歌词，也可 SeekTo
            State = EngineState.Paused;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Play()
    {
        if (_stream == 0) return;
        if (Bass.ChannelPlay(_stream))
        {
            State = EngineState.Playing;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Pause()
    {
        if (_stream == 0) return;
        if (Bass.ChannelPause(_stream))
        {
            State = EngineState.Paused;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>在播放与暂停之间切换。</summary>
    public void TogglePlayPause()
    {
        if (State == EngineState.Playing) Pause();
        else Play();
    }

    public void Stop()
    {
        if (_stream == 0) return;
        Bass.ChannelStop(_stream);
        Bass.ChannelSetPosition(_stream, 0);
        State = EngineState.Stopped;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>跳转到指定秒数。</summary>
    public void SeekTo(double seconds)
    {
        if (_stream == 0) return;
        long pos = Bass.ChannelSeconds2Bytes(_stream, Math.Max(0, seconds));
        Bass.ChannelSetPosition(_stream, pos);
    }

    /// <summary>当前播放位置（秒）。</summary>
    public double Position
    {
        get
        {
            if (_stream == 0) return 0;
            return Bass.ChannelBytes2Seconds(_stream, Bass.ChannelGetPosition(_stream));
        }
    }

    /// <summary>当前曲目总时长（秒）。无流时返回 0。</summary>
    public double Duration
    {
        get
        {
            if (_stream == 0) return 0;
            long len = Bass.ChannelGetLength(_stream);
            return len <= 0 ? 0 : Bass.ChannelBytes2Seconds(_stream, len);
        }
    }

    /// <summary>音量，0.0–1.0。</summary>
    public double Volume
    {
        get
        {
            if (_stream == 0) return _volume;
            return Bass.ChannelGetAttribute(_stream, ChannelAttribute.Volume);
        }
        set
        {
            _volume = Math.Clamp(value, 0.0, 1.0);
            if (_stream == 0) return;
            Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, _volume);
        }
    }

    /// <summary>是否仍有活动流。</summary>
    public bool HasStream => _stream != 0;

    /// <summary>当前 BASS 流句柄（供 FFT 频谱读取）。0 表示无。</summary>
    public int StreamHandle => _stream;

    // ---------- 内部 ----------

    private void CloseStream()
    {
        if (_stream == 0) return;
        Bass.ChannelStop(_stream);
        Bass.StreamFree(_stream);
        _stream = 0;
        State = EngineState.Stopped;
    }

    /// <summary>
    /// 加载格式插件。加载失败不视为致命错误——
    /// 例如缺少 bassflac.dll 时仍可播放 BASS 内建的 MP3/WAV/OGG。
    /// </summary>
    private void SyncPlugins()
    {
        var loaded = new List<string>();

        foreach (var name in PluginFiles)
        {
            string path = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(path)) continue;

            int handle = Bass.PluginLoad(name);
            if (handle != 0)
            {
                _pluginHandles.Add(handle);
                loaded.Add(name);
            }
        }

        LoadedPlugins = loaded;
    }

    public void Dispose()
    {
        CloseStream();

        foreach (var h in _pluginHandles)
        {
            try { Bass.PluginFree(h); } catch { /* 忽略清理异常 */ }
        }
        _pluginHandles.Clear();

        if (_initialized)
        {
            Bass.Free();
            _initialized = false;
        }
    }
}