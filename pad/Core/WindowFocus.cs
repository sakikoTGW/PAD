using System.Runtime.InteropServices;

namespace Pad.Core;

/// <summary>把一个已经在跑的进程的主窗口叫到前台（探测到的桌面端 / TUI 用）。</summary>
public static class WindowFocus
{
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hWnd);

    /// <summary>找到该 pid 的可见顶层窗口，恢复并置前。没有返回 false。</summary>
    public static bool Activate(int pid)
    {
        if (pid <= 0) return false;
        var found = IntPtr.Zero;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var owner);
                if (owner != (uint)pid) return true;
                if (!IsWindowVisible(hWnd)) return true;
                if (GetWindowTextLength(hWnd) == 0) return true;
                found = hWnd;
                return false;
            }, IntPtr.Zero);
        }
        catch { return false; }

        if (found == IntPtr.Zero) return false;
        try
        {
            ShowWindow(found, 9);          // SW_RESTORE
            return SetForegroundWindow(found);
        }
        catch { return false; }
    }
}
