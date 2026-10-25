using System.Runtime.InteropServices;  // P/Invoke 调系统 API

namespace ZincXManagerTests;

/// <summary>
/// Win32 注入:不改客户端代码,直接把鼠标/键盘事件打进它的窗口。
/// Avalonia 窗口是单窗口自绘,没有子窗口句柄可拿,所以按"窗口矩形 + 相对位置"点。
/// </summary>
public static class Win32
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort VkKeyScan(char ch);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);


    private delegate bool EnumProc(IntPtr h, IntPtr param);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // mouse_event:按下左键。
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    // mouse_event:松开左键。
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    // keybd_event:按键抬起。
    private const uint KEYEVENTF_KEYUP = 0x0002;
    // SetWindowPos:不改尺寸 / 不改层级。
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004;

    /// <summary>窗口是否还在、能不能拿到矩形。</summary>
    public static bool TryRect(IntPtr h, out int x, out int y, out int w, out int hgt)
    {
        x = y = w = hgt = 0;
        if (!GetWindowRect(h, out var r)) { return false; }
        x = r.Left; y = r.Top;
        w = r.Right - r.Left;
        hgt = r.Bottom - r.Top;
        return w > 0 && hgt > 0;
    }

    /// <summary>把窗口摆到固定位置和大小(注入用,免得坐标飘)。</summary>
    public static void PlaceWindow(IntPtr h, int x, int y, int w, int hgt)
    {
        ShowWindow(h, 9);                        // SW_RESTORE
        SetWindowPos(h, IntPtr.Zero, x, y, w, hgt, SWP_NOZORDER);
        Thread.Sleep(300);
    }

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    // 后台消息:只发给目标窗口,不动真实鼠标/键盘
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_CHAR = 0x0102;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;

    /// <summary>
    /// 往窗口客户区(相对坐标 0..1)发一次点击。
    /// 用 PostMessage 直接投递,<b>不移动真实鼠标、不抢焦点</b>。
    /// </summary>
    public static bool ClickIn(IntPtr h, double rx, double ry)
    {
        if (!GetClientRect(h, out var cr))
        {
            return false;
        }

        var w = cr.Right - cr.Left;
        var hgt = cr.Bottom - cr.Top;
        if (w <= 0 || hgt <= 0)
        {
            return false;
        }

        var cx = (int)(w * rx);
        var cy = (int)(hgt * ry);
        var lp = (IntPtr)((cy << 16) | (cx & 0xFFFF));

        // SendMessage(同步)比 PostMessage 稳:Avalonia 的窗口过程要在自己的循环里拿到消息
        SendMessage(h, WM_MOUSEMOVE, IntPtr.Zero, lp);
        Thread.Sleep(40);
        SendMessage(h, WM_LBUTTONDOWN, (IntPtr)1, lp);
        Thread.Sleep(60);
        SendMessage(h, WM_LBUTTONUP, IntPtr.Zero, lp);
        Thread.Sleep(120);
        return true;
    }

    /// <summary>往窗口发一串 ASCII(走 WM_CHAR,不碰真实键盘;窗口要有焦点才能进输入框)</summary>
    public static void TypeTo(IntPtr h, string text)
    {
        foreach (var ch in text)
        {
            SendMessage(h, WM_CHAR, (IntPtr)ch, IntPtr.Zero);
            Thread.Sleep(25);
        }
    }

    /// <summary>发一次回车</summary>
    public static void PressEnterTo(IntPtr h)
    {
        PostMessage(h, WM_KEYDOWN, (IntPtr)0x0D, IntPtr.Zero);
        Thread.Sleep(30);
        PostMessage(h, WM_KEYUP, (IntPtr)0x0D, IntPtr.Zero);
    }

    /// <summary>把窗口提到前台拿焦点(不动鼠标;某些输入需要焦点才能进控件)</summary>
    public static void Focus(IntPtr h)
    {
        ShowWindow(h, 9);
        SetForegroundWindow(h);
        Thread.Sleep(250);
    }

    /// <summary>取某个进程所有可见窗口的标题(找弹窗用)</summary>
    public static List<(IntPtr Hwnd, string Title)> WindowsOf(int processId)
    {
        var list = new List<(IntPtr, string)>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pid != (uint)processId || !IsWindowVisible(h))
            {
                return true;
            }

            var sb = new System.Text.StringBuilder(256);
            GetWindowTextW(h, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.Length > 0)
            {
                list.Add((h, title));
            }

            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>按标题片段找窗口(找不到返回 IntPtr.Zero)</summary>
    public static IntPtr FindWindowByTitle(int processId, string part)
    {
        foreach (var (h, title) in WindowsOf(processId))
        {
            if (title.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return h;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>窗口尺寸(验证弹窗真的开出来了)</summary>
    public static bool SizeOf(IntPtr h, out int w, out int hgt)
    {
        w = hgt = 0;
        if (!GetWindowRect(h, out var r))
        {
            return false;
        }

        w = r.Right - r.Left;
        hgt = r.Bottom - r.Top;
        return w > 0 && hgt > 0;
    }
}
