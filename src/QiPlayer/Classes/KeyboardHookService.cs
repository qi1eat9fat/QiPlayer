using System.Diagnostics;
using System.Runtime.InteropServices;
using static QiPlayer.Classes.NativeMethods;

namespace QiPlayer.Classes;

/// <summary>
/// 全局媒体键钩子。
///
/// 只保留媒体键（播放/暂停、上一曲、下一曲、停止）；
/// FluentFlyout 中的音量键与锁键分支已按确认范围删除。
///
/// 行为：媒体键直接驱动本地播放器并弹出 Flyout。
/// 因为没有外部播放器会话，这里吞掉媒体键（返回 1），
/// 避免浏览器等应用同时响应。
///
/// 关键约束：
///   · 只允许吞掉媒体键。普通按键（字母、Shift/Ctrl 等）必须原样放行——
///     全局钩子里误吞普通按键，会让所有程序出现间歇性丢字。
///   · 钩子装在独立线程上。低层钩子的回调由「安装它的线程」处理，
///     若装在 UI 线程，播放时歌词/频谱绘制会拖慢每一次系统按键投递。
/// </summary>
public sealed class KeyboardHookService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;

    private const int VK_MEDIA_NEXT_TRACK = 0xB0;
    private const int VK_MEDIA_PREV_TRACK = 0xB1;
    private const int VK_MEDIA_STOP = 0xB2;
    private const int VK_MEDIA_PLAY_PAUSE = 0xB3;

    private readonly LowLevelKeyboardProc _proc;
    private readonly Thread _hookThread;
    private readonly ManualResetEventSlim _hookReady = new(false);

    // 当前按住的媒体键：仅用于抑制长按产生的自动重复。
    // 不能退化成全局「有键按下」标志——那样会把修饰键组合与快速连打中
    // 重叠的普通字母一起吞掉，这正是此前输入框丢字的根因。
    private readonly HashSet<int> _mediaKeysDown = new();

    private IntPtr _hookId;
    private uint _hookThreadId;
    private bool _disposed;

    private readonly Action _onPlayPause;
    private readonly Action _onNext;
    private readonly Action _onPrevious;
    private readonly Action _onStop;

    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    public KeyboardHookService(
        Action onPlayPause,
        Action onNext,
        Action onPrevious,
        Action onStop)
    {
        _onPlayPause = onPlayPause;
        _onNext = onNext;
        _onPrevious = onPrevious;
        _onStop = onStop;

        _proc = HookCallback;

        // 独立的钩子线程：自带消息循环，不受 UI 线程忙碌程度影响
        _hookThread = new Thread(HookThreadProc)
        {
            IsBackground = true,
            Name = "QiPlayer 键盘钩子",
        };
        _hookThread.Start();

        // 等待安装结果，避免构造函数返回时钩子尚未就绪
        _hookReady.Wait(TimeSpan.FromSeconds(2));
    }

    private void HookThreadProc()
    {
        _hookThreadId = GetCurrentThreadId();

        // 先创建线程消息队列，保证 Dispose 的 PostThreadMessage 能送达
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);

        _hookId = SetHook(_proc);
        _hookReady.Set();

        if (_hookId == IntPtr.Zero)
        {
            App.Log($"安装全局键盘钩子失败（错误码 {Marshal.GetLastWin32Error()}）");
            return;
        }

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        // 收到 WM_QUIT：在线程内卸载钩子
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;

        if (module is null) return IntPtr.Zero;

        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(module.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hookId, nCode, wParam, lParam);

        bool isKeyDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
        bool isKeyUp = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;

        if (!isKeyDown && !isKeyUp) return CallNextHookEx(_hookId, nCode, wParam, lParam);

        int vkCode = Marshal.ReadInt32(lParam);

        // 非媒体键一律放行。普通按键一旦在这里被吞，
        // 所有程序的输入都会间歇性丢字（含中文输入法与组合键）。
        if (!IsMediaKey(vkCode)) return CallNextHookEx(_hookId, nCode, wParam, lParam);

        if (isKeyUp)
        {
            _mediaKeysDown.Remove(vkCode);

            // 连按下一起吞掉，避免其他程序收到「只有抬起」的残缺按键
            return 1;
        }

        // 长按的自动重复只触发一次
        if (_mediaKeysDown.Add(vkCode)) DispatchMediaKey(vkCode);

        return 1;
    }

    private static bool IsMediaKey(int vkCode) => vkCode
        is VK_MEDIA_PLAY_PAUSE or VK_MEDIA_NEXT_TRACK or VK_MEDIA_PREV_TRACK or VK_MEDIA_STOP;

    private void DispatchMediaKey(int vkCode)
    {
        switch (vkCode)
        {
            case VK_MEDIA_PLAY_PAUSE:
                _onPlayPause();
                break;

            case VK_MEDIA_NEXT_TRACK:
                _onNext();
                break;

            case VK_MEDIA_PREV_TRACK:
                _onPrevious();
                break;

            case VK_MEDIA_STOP:
                _onStop();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 通知钩子线程退出（其消息循环收到 WM_QUIT 后自行卸载钩子）
        if (_hookThreadId != 0)
            PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        // 超时兜底：线程未能退出时直接卸载，避免残留全局钩子
        if (!_hookThread.Join(TimeSpan.FromSeconds(2)) && _hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        _hookReady.Dispose();
    }
}
