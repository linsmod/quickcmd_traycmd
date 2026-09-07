// ============================================================================
// HotKeyKit — GlobalHotKey.cs: 全局快捷键检测库（无 WinForms 依赖）
//
// 原理：RegisterHotKey 需要窗口句柄接收 WM_HOTKEY。本库在自己的后台线程
// 上创建 message-only 窗口（HWND_MESSAGE）并跑消息循环，把检测完全独立于
// 调用方的 UI 框架；命中后在调用方提供的 SynchronizationContext（或直接用
// 监听线程）上触发回调。
//
// Win 长按检测（EnableWinLongPress）：RegisterHotKey 无法表达“按住不放”，
// 用 WH_KEYBOARD_LL 低级键盘钩子实现（钩子装在本库的监听线程上）：
//   - 吞掉物理 Win 按下，起 holdMs 定时器；
//   - 定时到且期间没按其他键 → 触发 WinKeyLongPress，并吞掉后续所有按键与
//     Win 抬起（Start 菜单不弹出，因为物理按下从未放行给系统）；
//   - 定时未到就抬起（短按）→ 补发一对合成的 Win 按下/抬起，Start 菜单照常；
//   - 期间按了别的键 → 判定为 Win+X 组合：立即补发 Win 按下放行整个组合，
//     照常交给 shell 处理，并在 Win 抬起时补发合成抬起。
// 库自身注入的事件带 INJECT_MARK 指纹，钩子据此放行不自我吞食。
// ============================================================================

using System.Runtime.InteropServices;

namespace HotKeyKit;

[Flags]
public enum HotKeyModifiers : uint
{
    None = 0x0000,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000,
}

// 一个实例 = 一个监听线程 + 一个 message-only 窗口；可注册多个热键。
// 线程安全：Register/Unregister/Dispose 可从任意线程调用。
public sealed class GlobalHotKey : IDisposable
{
    // 可选：回调 marshal 回的目标上下文（如 WindowsFormsSynchronizationContext）。
    // 为 null 时直接在监听线程上回调。
    public SynchronizationContext CallbackContext { get; set; }

    private const int WM_HOTKEY = 0x0312;
    private const int WM_TIMER_MSG = 0x0113;
    private const uint WM_APP_REGISTER = 0x8001;   // wParam=id lParam=(mods<<32)|vk
    private const uint WM_APP_UNREGISTER = 0x8002; // wParam=id
    private const uint WM_APP_QUIT = 0x8003;
    private const uint WM_APP_WINLP_ON = 0x8004;   // wParam=holdMs
    private const uint WM_APP_WINLP_OFF = 0x8005;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly object _gate = new();
    private readonly Dictionary<int, Action> _handlers = new();
    private readonly HashSet<int> _registeredIds = new(); // 成功 RegisterHotKey 的 id，线程退出时统一注销
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private IntPtr _hwnd;
    private bool _disposed;

    // ---- Win 长按状态（仅监听线程访问：LL 钩子回调与 WM_TIMER 都在该线程派发）----
    private const uint WH_KEYBOARD_LL = 13;
    private const uint WM_KEYMSG_UP = 0x0101;      // WM_KEYUP（== WM_SYSKEYUP-4，配合 LLKHF_UP 用，仅备注）
    private const uint LLKHF_UP = 0x80, LLKHF_INJECTED = 0x10;
    private const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const uint KEYEVENTF_KEYUP = 0x02;
    private const uint WINLP_TIMER_ID = 0xB1;
    private static readonly IntPtr INJECT_MARK = 0x484B_594C; // "HKYL"：区分本库注入与真实按键

    private HookProcDelegate _hookKeepAlive;
    private IntPtr _hook;
    private bool _winlpEnabled;
    private uint _winlpHoldMs = 500;
    private bool _winDown, _winIsLeft, _winlpCombo, _winlpFired;

    // 单独按住 Win 达阈值时触发（经 CallbackContext marshal）。启用前先调 EnableWinLongPress。
    public event Action WinKeyLongPress;

    public GlobalHotKey()
    {
        _thread = new Thread(ListenerLoop) { IsBackground = true, Name = "HotKeyKit.Listener" };
        _thread.Start();
        _ready.Wait();
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("HotKeyKit: message-only 窗口创建失败");
    }

    // 注册热键。返回 false = 注册失败（多为已被其他进程占用）。id 在本实例内唯一。
    public bool Register(int id, HotKeyModifiers modifiers, Keys key, Action callback)
    {
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GlobalHotKey));
            if (_handlers.ContainsKey(id)) throw new ArgumentException("热键 id 已注册: " + id);
            _handlers[id] = callback;
        }

        long packed = ((long)(uint)modifiers << 32) | (uint)key;
        bool ok = Send(WM_APP_REGISTER, id, new IntPtr(packed));
        if (!ok) lock (_gate) _handlers.Remove(id);
        return ok;
    }

    public bool Register(HotKeyModifiers modifiers, Keys key, Action callback)
        => Register(NextFreeId(), modifiers, key, callback);

    // 注销热键。返回 false = id 不存在。
    public bool Unregister(int id)
    {
        lock (_gate)
        {
            if (_disposed || !_handlers.Remove(id)) return false;
        }
        Send(WM_APP_UNREGISTER, id, IntPtr.Zero);
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Post(WM_APP_QUIT, 0, IntPtr.Zero);
    }

    // 启用“单独长按 Win”检测（默认阈值 500ms）。返回 false = 钩子安装失败。
    // 短按 Win 仍会像平常一样打开开始菜单（合成重放），不影响 Win+X 组合键。
    public bool EnableWinLongPress(uint holdMs = 500)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GlobalHotKey));
        }
        return Send(WM_APP_WINLP_ON, (int)holdMs, IntPtr.Zero);
    }

    public void DisableWinLongPress()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }
        Send(WM_APP_WINLP_OFF, 0, IntPtr.Zero);
    }

    private int NextFreeId()
    {
        lock (_gate)
        {
            for (int i = 1; ; i++)
                if (!_handlers.ContainsKey(i)) return i;
        }
    }

    // 同步投递（SendMessage 跨线程 marshal 到监听线程），返回窗口的处理结果
    private bool Send(uint msg, int wParam, IntPtr lParam)
    {
        IntPtr hwnd;
        lock (_gate) hwnd = _hwnd;
        return hwnd != IntPtr.Zero && SendMessageW(hwnd, msg, (IntPtr)wParam, lParam) != IntPtr.Zero;
    }

    private void Post(uint msg, int wParam, IntPtr lParam)
    {
        IntPtr hwnd;
        lock (_gate) hwnd = _hwnd;
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, msg, (IntPtr)wParam, lParam);
    }

    // ---- 监听线程：创建窗口 + 消息循环 ----

    // 窗口类进程内注册一次；消息按 hWnd 分发到所属实例
    private static readonly Dictionary<IntPtr, GlobalHotKey> Windows = new();
    private static WndProcDelegate _wndProcKeepAlive;
    private static ushort _classAtom;
    private const string ClassName = "HotKeyKit.ListenerWindow";
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        GlobalHotKey self;
        lock (Windows)
        {
            if (!Windows.TryGetValue(hWnd, out self))
                return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
        return self.WndProc(hWnd, msg, wParam, lParam);
    }

    private static void EnsureClassRegistered()
    {
        lock (Windows)
        {
            if (_classAtom != 0) return;
            _wndProcKeepAlive = StaticWndProc;
            var hInst = GetModuleHandle(null);
            var wc = new WNDCLASS
            {
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = hInst,
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = ClassName,
            };
            ushort atom = RegisterClassW(ref wc);
            if (atom == 0 && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
                throw new InvalidOperationException("HotKeyKit: RegisterClassW 失败, win32 error=" + Marshal.GetLastWin32Error());
            _classAtom = atom != 0 ? atom : (ushort)1; // 类已存在也视为可用
        }
    }

    private void ListenerLoop()
    {
        try
        {
            EnsureClassRegistered();
            _hwnd = CreateWindowExW(0, ClassName, "HotKeyKit",
                0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        }
        catch { _hwnd = IntPtr.Zero; }
        _ready.Set();
        if (_hwnd == IntPtr.Zero) return;

        lock (Windows) Windows[_hwnd] = this;

        var msg = new MSG();
        while (GetMessageW(ref msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        // 线程退出前清场：拔键盘钩子 + 注销本实例的热键（Dispose 已清空 _handlers，故单独记账）
        DisableWinHook();
        lock (_gate)
        {
            foreach (var id in _registeredIds) UnregisterHotKey(_hwnd, id);
            _registeredIds.Clear();
        }
        lock (Windows) Windows.Remove(_hwnd);
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_APP_REGISTER:
            {
                int id = wParam.ToInt32();
                long packed = lParam.ToInt64();
                var ok = RegisterHotKey(hWnd, id, (uint)(packed >> 32), (uint)(packed & 0xFFFFFFFF));
                lock (_gate)
                {
                    if (ok) _registeredIds.Add(id);
                    else _handlers.Remove(id);
                }
                return ok ? 1 : 0;
            }
            case WM_APP_UNREGISTER:
            {
                int id = wParam.ToInt32();
                var ok = UnregisterHotKey(hWnd, id);
                if (ok) lock (_gate) _registeredIds.Remove(id);
                return ok ? 1 : 0;
            }
            case WM_APP_QUIT:
                PostQuitMessage(0);
                return 0;
            case WM_APP_WINLP_ON:
                _winlpHoldMs = Math.Max(150u, (uint)wParam.ToInt32());
                EnsureWinHook();
                return _hook != IntPtr.Zero ? 1 : 0;
            case WM_APP_WINLP_OFF:
                DisableWinHook();
                return 1;
            case WM_TIMER_MSG:
                if (wParam.ToInt32() == (int)WINLP_TIMER_ID && _winDown && !_winlpCombo && !_winlpFired)
                {
                    KillTimer(hWnd, WINLP_TIMER_ID);
                    _winlpFired = true;
                    var cb = WinKeyLongPress;
                    if (cb != null) Fire(cb);
                }
                return 0;
            case WM_HOTKEY:
            {
                int id = wParam.ToInt32();
                Action cb;
                lock (_gate) _handlers.TryGetValue(id, out cb);
                if (cb != null) Fire(cb);
                return 0;
            }
            default:
                return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    private void Fire(Action cb)
    {
        var ctx = CallbackContext;
        if (ctx != null) ctx.Post(_ => cb(), null);
        else cb();
    }

    // ---- Win 长按：低级键盘钩子（装/拔、回调都在监听线程）----

    private void EnsureWinHook()
    {
        if (_hook != IntPtr.Zero) { _winlpEnabled = true; return; }
        _hookKeepAlive = KeyboardHook;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL,
            Marshal.GetFunctionPointerForDelegate(_hookKeepAlive), GetModuleHandle(null), 0);
        _winlpEnabled = _hook != IntPtr.Zero;
        _winDown = _winlpCombo = _winlpFired = false;
    }

    private void DisableWinHook()
    {
        if (_winlpEnabled && _winDown && _winlpCombo) InjectWinUp(_winIsLeft ? VK_LWIN : VK_RWIN); // 别把合成按下悬在半空
        _winlpEnabled = false;
        _winDown = _winlpCombo = _winlpFired = false;
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        _hookKeepAlive = null;
    }

    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _winlpEnabled && lParam != IntPtr.Zero)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool isWin = kb.vkCode == VK_LWIN || kb.vkCode == VK_RWIN;
            bool up = (kb.flags & LLKHF_UP) != 0;

            // 只拦截未标记的真实按键；本库合成注入（带 INJECT_MARK）直接放行
            if (kb.dwExtraInfo != INJECT_MARK)
            {
                if (isWin && !up)
                {
                    if (!_winDown)
                    {
                        _winDown = true;
                        _winIsLeft = kb.vkCode == VK_LWIN;
                        _winlpCombo = false;
                        _winlpFired = false;
                        SetTimer(_hwnd, WINLP_TIMER_ID, _winlpHoldMs, IntPtr.Zero);
                    }
                    return 1; // 吞掉物理 Win 按下（含自动重复）：shell 从未看见，Start 不会提前弹
                }
                if (isWin && up && _winDown)
                {
                    KillTimer(_hwnd, WINLP_TIMER_ID);
                    var vk = _winIsLeft ? VK_LWIN : VK_RWIN;
                    _winDown = false;
                    if (_winlpFired) _winlpFired = false;                       // 长按生效：吞掉抬起
                    else if (_winlpCombo) { _winlpCombo = false; InjectWinUp(vk); } // 组合键收尾
                    else InjectWinTap(vk);                                      // 短按：重放成正常 Win 按下+抬起
                    return 1;
                }
                if (_winDown && !_winlpFired && !_winlpCombo && !up)
                {
                    // 按住 Win 又按别的键 → 是组合键：补发 Win 按下，本次按键照常放行
                    _winlpCombo = true;
                    KillTimer(_hwnd, WINLP_TIMER_ID);
                    InjectWinDown(_winIsLeft ? VK_LWIN : VK_RWIN);
                }
                else if (_winDown && _winlpFired)
                {
                    return 1; // 长按已生效：吞掉释放前的所有按键
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void InjectWinDown(uint vk)
    {
        var scan = MapVirtualKeyW(vk, 0); // MAPVK_VK_TO_VSC
        keybd_event((byte)vk, (byte)scan, 0, INJECT_MARK);
    }

    private static void InjectWinUp(uint vk)
    {
        var scan = MapVirtualKeyW(vk, 0);
        keybd_event((byte)vk, (byte)scan, KEYEVENTF_KEYUP, INJECT_MARK);
    }

    private static void InjectWinTap(uint vk) { InjectWinDown(vk); InjectWinUp(vk); }

    // ---- Win32 ----

    public enum Keys : uint
    {
        None = 0x00,
        A = 0x41, B = 0x42, C = 0x43, D = 0x44, E = 0x45, F = 0x46, G = 0x47, H = 0x48,
        I = 0x49, J = 0x4A, K = 0x4B, L = 0x4C, M = 0x4D, N = 0x4E, O = 0x4F, P = 0x50,
        Q = 0x51, R = 0x52, S = 0x53, T = 0x54, U = 0x55, V = 0x56, W = 0x57, X = 0x58,
        Y = 0x59, Z = 0x5A,
        D0 = 0x30, D1 = 0x31, D2 = 0x32, D3 = 0x33, D4 = 0x34, D5 = 0x35, D6 = 0x36,
        D7 = 0x37, D8 = 0x38, D9 = 0x39,
        F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73, F5 = 0x74, F6 = 0x75, F7 = 0x76,
        F8 = 0x77, F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B,
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr HookProcDelegate(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInst, IntPtr param);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool GetMessageW(ref MSG lpMsg, IntPtr hWnd, uint minMsg, uint maxMsg);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(uint idHook, IntPtr lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hWnd, uint nIDEvent, uint uElapse, IntPtr lpTimerFunc);
    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hWnd, uint uIDEvent);
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);
}
