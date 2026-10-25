using System.Windows;
using System.Windows.Media.Imaging;

namespace QiPlayer.Classes;

/// <summary>
/// 封面图像辅助。
///
/// 移植自 FluentFlyout 的 BitmapHelper，保留封面裁方（模糊背景的渐变遮罩）所需的最小逻辑。
/// </summary>
internal static class BitmapHelper
{
    /// <summary>把图片裁成正方形（用于模糊背景的渐变遮罩）。</summary>
    public static CroppedBitmap? CropToSquare(BitmapSource? source)
    {
        if (source is null) return null;

        int size = Math.Min(source.PixelWidth, source.PixelHeight);
        int x = (source.PixelWidth - size) / 2;
        int y = (source.PixelHeight - size) / 2;

        var cropped = new CroppedBitmap(source, new Int32Rect(x, y, size, size));
        cropped.Freeze();
        return cropped;
    }
}
