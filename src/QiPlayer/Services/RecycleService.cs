using System.IO;
using System.Runtime.InteropServices;

namespace QiPlayer.Services;

/// <summary>
/// 把文件移入回收站（而非永久删除）。
///
/// 用 Win32 的 SHFileOperation + FOF_ALLOWUNDO 实现：
/// 文件进入回收站，用户可随时还原，比 File.Delete 安全得多。
/// 不支持恢复的永久删除不在此提供——破坏性操作不应成为默认路径。
/// </summary>
public static class RecycleService
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;   // 移入回收站而非直接删除
    private const ushort FOF_NOCONFIRMATION = 0x0010;  // 不再弹系统确认框（由我们自己确认）
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOERRORUI = 0x0400;   // 出错不弹系统框，由返回值处理

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>
    /// 把一个或多个文件移入回收站。
    /// </summary>
    /// <param name="paths">文件路径集合。</param>
    /// <param name="ownerWindow">所属窗口句柄，用于系统对话框归属（可为 IntPtr.Zero）。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>是否全部成功。</returns>
    public static bool MoveToRecycleBin(
        IEnumerable<string> paths,
        IntPtr ownerWindow,
        out string? error)
    {
        error = null;

        var list = paths.Where(File.Exists).ToList();
        if (list.Count == 0)
        {
            error = "没有可删除的文件";
            return false;
        }

        // pFrom 需要「每个路径以 \0 结尾，整个列表再以 \0 结尾」
        string from = string.Join('\0', list) + "\0\0";

        var op = new SHFILEOPSTRUCT
        {
            hwnd = ownerWindow,
            wFunc = FO_DELETE,
            pFrom = from,
            pTo = null,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            fAnyOperationsAborted = false,
        };

        try
        {
            int result = SHFileOperation(ref op);

            if (result != 0)
            {
                error = $"系统返回错误码 {result}";
                return false;
            }

            if (op.fAnyOperationsAborted)
            {
                error = "操作被中止";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}