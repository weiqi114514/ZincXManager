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

    /// <summary>把鼠标移到屏幕坐标点一下(给 GUI 注入用)。</summary>
    public static void ClickAt(int x, int y)
    {
        SetForegroundWindow(GetForegroundWindow());
        SetCursorPos(x, y);
        Thread.Sleep(120);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(200);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    /// <summary>按窗口矩形 + 相对坐标点一下。</summary>
    public static bool ClickIn(IntPtr h, double rx, double ry)
    {
        if (!TryRect(h, out var x, out var y, out var w, out var hgt)) { return false; }
        SetForegroundWindow(h);
        Thread.Sleep(200);
        ClickAt(x + (int)(w * rx), y + (int)(hgt * ry));
        return true;
    }

    /// <summary>往当前焦点敲一串 ASCII 字符。</summary>
    public static void TypeAscii(string text)
    {
        foreach (var ch in text)
        {
            var vk = VkKeyScan(ch);
            var key = (byte)(vk & 0xFF);
            var shift = (vk & 0x0100) != 0;
            if (shift) { keybd_event(0x10, 0, 0, UIntPtr.Zero); }
            keybd_event(key, 0, 0, UIntPtr.Zero);
            Thread.Sleep(20);
            keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            if (shift) { keybd_event(0x10, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); }
            Thread.Sleep(25);
        }
    }

    /// <summary>P/Invoke:发一次按键(按下/抬起)。</summary>
    public static void PressEnter() { keybd_event(0x0D, 0, 0, UIntPtr.Zero); Thread.Sleep(30); keybd_event(0x0D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); }
}
