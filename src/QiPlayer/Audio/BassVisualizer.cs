using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManagedBass;
using QiPlayer.Classes.Settings;

namespace QiPlayer.Audio;

/// <summary>
/// 基于 BASS FFT 的频谱数据源。
///
/// 与 FluentFlyout 的 Visualizer 差异：
///   · 数据来自当前播放流的 <see cref="Bass.ChannelGetData(int, float[], int)"/>，
///     而不是 WASAPI 全系统回环 → 更准、更省 CPU，且不需要 NAudio；
///   · 栅格化绘制沿用上游思路（WriteableBitmap + 圆角柱）。
///
/// 调用方按 30fps 调 <see cref="Update"/> 即可。
/// </summary>
public sealed class BassVisualizer : IDisposable
{
    // 与上游一致：2 倍超采样后缩放，WPF 的线性过滤才会得到接近 box 平均的效果
    private const int Supersample = 2;
    private const int DefaultWidth = 76;
    private const int ImageHeight = 32 * Supersample;
    private const int BarSpacing = 2 * Supersample;

    /// <summary>位图宽度（超采样像素），随设置里的频谱宽度变化。</summary>
    private readonly int _imageWidth;

    private const int FftLength = 2048;
    private const float MinFrequency = 40f;
    private const float MaxFrequency = 8000f;

    private readonly float[] _fftBuffer = new float[FftLength];
    private readonly float[] _barValues;
    private readonly WriteableBitmap _bitmap;

    private int _stream;
    private bool _disposed;

    /// <summary>所有柱子是否都已归零（用于暂停时停绘，省 CPU）。</summary>
    public bool IsSilent
    {
        get
        {
            foreach (float value in _barValues)
                if (value > 0.01f) return false;

            return true;
        }
    }

    /// <param name="barCount">柱数。</param>
    /// <param name="width">显示宽度（DIP），决定位图分辨率与柱宽。</param>
    public BassVisualizer(int barCount = 10, double width = DefaultWidth)
    {
        _barValues = new float[Math.Max(1, barCount)];

        int logicalWidth = (int)Math.Round(Math.Max(24, width));
        _imageWidth = logicalWidth * Supersample;

        // 注意：不能 Freeze——冻结后的 WriteableBitmap 无法再 Lock/写入。
        // 位图在 UI 线程创建，之后也只在 UI 线程的渲染计时器里更新。
        _bitmap = new WriteableBitmap(_imageWidth, ImageHeight, 96, 96, PixelFormats.Bgra32, null);
    }

    /// <summary>频谱位图（供 Image.Source 绑定）。</summary>
    public WriteableBitmap Bitmap => _bitmap;

    /// <summary>绑定当前正在播放的 BASS 流；0 表示无。</summary>
    public void SetStream(int stream) => _stream = stream;

    /// <summary>
    /// 采样一次并重绘。
    /// 无流或读取失败时把柱子平滑归零。
    /// </summary>
    public void Update()
    {
        if (_disposed) return;

        bool hasData = false;

        if (_stream != 0)
        {
            // BASS_DATA_FFT2048 | BASS_DATA_FLOAT
            const int flags = (int)(DataFlags.FFT2048 | DataFlags.Float);

            hasData = Bass.ChannelGetData(_stream, _fftBuffer, flags) > 0;
        }

        if (hasData)
            ComputeBars();
        else
            DecayBars();

        Render();
    }

    private void ComputeBars()
    {
        var settings = SettingsManager.Current;

        int sampleRate = _stream != 0
            ? (int)Bass.ChannelGetAttribute(_stream, ChannelAttribute.Frequency)
            : 44100;

        if (sampleRate <= 0) sampleRate = 44100;

        // 只取前半段（实数 FFT 的对称性），幅度 = 模长
        int binCount = FftLength / 2;
        double frequencyPerBin = (double)sampleRate / FftLength;

        float minDb = (settings.TaskbarVisualizerAudioSensitivity * -10f) - 30f;
        float maxDb = (settings.TaskbarVisualizerAudioPeakLevel * 10f) - 30f;
        if (Math.Abs(maxDb - minDb) < 1f) maxDb = minDb + 30f;

        int bars = _barValues.Length;

        for (int i = 0; i < bars; i++)
        {
            // 对数分频：低频窄、高频宽，符合听感
            double startFreq = MinFrequency * Math.Pow(MaxFrequency / MinFrequency, (double)i / bars);
            double endFreq = MinFrequency * Math.Pow(MaxFrequency / MinFrequency, (double)(i + 1) / bars);

            int startBin = Math.Clamp((int)(startFreq / frequencyPerBin), 0, binCount - 1);
            int endBin = Math.Clamp((int)(endFreq / frequencyPerBin), startBin + 1, binCount);

            float maxAmplitude = 0f;

            for (int bin = startBin; bin < endBin; bin++)
            {
                float amplitude = MathF.Abs(_fftBuffer[bin]);
                if (amplitude > maxAmplitude) maxAmplitude = amplitude;
            }

            // 高频能量天然偏低，按位置做线性补偿
            float linearBoost = 1f + ((float)i / bars * 75f);
            maxAmplitude *= linearBoost;

            if (maxAmplitude < 0.001f) maxAmplitude = 0.001f;

            float db = 20f * MathF.Log10(maxAmplitude);
            float intensity = (db - minDb) / (maxDb - minDb);

            float target = Math.Clamp(intensity, 0f, 1f);

            // 上升快、下落慢
            _barValues[i] = target > _barValues[i]
                ? target
                : _barValues[i] * 0.8f + target * 0.2f;
        }
    }

    private void DecayBars()
    {
        for (int i = 0; i < _barValues.Length; i++)
            _barValues[i] *= 0.85f;
    }

    // ---------- 绘制 ----------

    private void Render()
    {
        if (_disposed) return;

        var settings = SettingsManager.Current;
        bool centered = settings.TaskbarVisualizerCenteredBars;
        int baseline = settings.TaskbarVisualizerBaseline ? Supersample * 2 : 0;

        Color color = ResolveColor();

        _bitmap.Lock();

        try
        {
            unsafe
            {
                byte* backBuffer = (byte*)_bitmap.BackBuffer;
                int stride = _bitmap.BackBufferStride;
                int bufferSize = stride * ImageHeight;

                new Span<byte>(backBuffer, bufferSize).Clear();

                int totalSpacing = (_barValues.Length - 1) * BarSpacing;
                int barWidth = Math.Max(1, (_imageWidth - totalSpacing - 1) / _barValues.Length);
                int usedWidth = barWidth * _barValues.Length + totalSpacing;
                int offsetX = (_imageWidth - usedWidth) >> 1;

                float radius = (2f * Supersample) / MathF.Max(1f, _barValues.Length / 10f);
                int centerY = ImageHeight / 2;

                for (int i = 0; i < _barValues.Length; i++)
                {
                    int barX = offsetX + i * (barWidth + BarSpacing);

                    int barHeight = Math.Max(
                        (int)(Math.Clamp(_barValues[i], 0f, 1f) * ImageHeight),
                        baseline);

                    if (barHeight <= 0) continue;

                    int barY, barEndY;
                    if (centered)
                    {
                        int half = barHeight >> 1;
                        barY = centerY - half;
                        barEndY = centerY + half;
                    }
                    else
                    {
                        barY = ImageHeight - barHeight;
                        barEndY = ImageHeight;
                    }

                    float clampedRadius = MathF.Min(radius, MathF.Min(barWidth, barHeight) * 0.5f);
                    DrawBar(backBuffer, stride, barX, barWidth, barY, barEndY,
                        centered, clampedRadius, color);
                }
            }

            _bitmap.AddDirtyRect(new Int32Rect(0, 0, _imageWidth, ImageHeight));
        }
        finally
        {
            _bitmap.Unlock();
        }
    }

    // 频谱条跟随全应用统一的品牌蓝，不再取封面主色或系统强调色。
    private static Color ResolveColor() => Classes.AccentPalette.Create().Accent.Color;

    private unsafe void DrawBar(
        byte* buffer, int stride,
        int barX, int barWidth, int barY, int barEndY,
        bool centered, float radius, Color color)
    {
        float left = barX;
        float right = barX + barWidth;
        float top = barY;
        float bottom = barEndY;

        float innerLeft = left + radius;
        float innerRight = right - radius;
        float innerTop = top + radius;
        float innerBottom = bottom - radius;

        float radiusSq = radius * radius;
        const float aa = 1.25f;
        float invAA = 1f / aa;

        for (int y = barY; y < barEndY && y < ImageHeight && y >= 0; y++)
        {
            int row = y * stride;
            float py = y + 0.5f;

            for (int x = barX; x < barX + barWidth && x < _imageWidth; x++)
            {
                int index = row + (x << 2);
                if (index + 3 >= stride * ImageHeight) continue;

                float px = x + 0.5f;

                // 中间与两侧：直接填充
                if (px >= innerLeft && px <= innerRight)
                {
                    WritePixel(buffer, index, color, 255);
                    continue;
                }

                if (py >= innerTop && py <= innerBottom)
                {
                    WritePixel(buffer, index, color, 255);
                    continue;
                }

                // 非居中模式的平底
                if (!centered && py >= innerBottom)
                {
                    WritePixel(buffer, index, color, 255);
                    continue;
                }

                // 圆角：用有符号距离场做抗锯齿
                float cx = px < innerLeft ? innerLeft : (px > innerRight ? innerRight : px);
                float cy = py < innerTop ? innerTop : (py > innerBottom ? innerBottom : py);

                float dx = px - cx;
                float dy = py - cy;
                float sdf = (dx * dx + dy * dy - radiusSq) / (2f * radius);
                float alpha = 0.5f - sdf * invAA;

                if (alpha <= 0f) continue;
                if (alpha > 1f) alpha = 1f;

                WritePixel(buffer, index, color, (byte)(255 * alpha));
            }
        }
    }

    private static unsafe void WritePixel(byte* buffer, int index, Color color, byte alpha)
    {
        buffer[index] = color.B;
        buffer[index + 1] = color.G;
        buffer[index + 2] = color.R;
        buffer[index + 3] = alpha;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
