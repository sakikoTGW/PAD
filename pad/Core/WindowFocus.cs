using System.Diagnostics;
using System.IO;
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
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hWnd);

    /// <summary>
    /// 找到该 pid 的可见顶层窗口，恢复并置前；置前后用 GetForegroundWindow 验证。
    /// Windows 允许前台窗口抢焦点的条件很窄，失败时用 AttachThreadInput 再抢一次。
    /// </summary>
    public static bool Activate(int pid)
    {
        if (pid <= 0) return false;
        var found = FindWindow(pid);
        if (found == IntPtr.Zero) return false;

        try
        {
            // 最小化 → SW_RESTORE；被隐藏（托盘/后台）→ SW_SHOW，先把窗口弄出来再说。
            ShowWindow(found, IsWindowVisible(found) ? 9 : 5);
            if (SetForegroundWindow(found) && GetForegroundWindow() == found) return true;

            // 前台锁：把自己的线程挂到当前前台窗口的输入队列上再抢一次。
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                GetWindowThreadProcessId(fg, out var fgPid);
                if (AttachThreadInput(GetCurrentThreadId(), fgPid, true))
                {
                    try { SetForegroundWindow(found); }
                    finally { AttachThreadInput(GetCurrentThreadId(), fgPid, false); }
                }
            }
            return GetForegroundWindow() == found;
        }
        catch { return false; }
    }

    /// <summary>pid 自己的窗口找不到时，按进程名兜底（Electron 的窗口可能挂在子进程上）。</summary>
    public static bool ActivateByExe(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        var name = Path.GetFileNameWithoutExtension(exe.Trim().Trim('"'));
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Activate(p.Id)) return true;
                }
                catch { /* 进程退出/无权限 */ }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return false;
    }

    static IntPtr FindWindow(int pid)
    {
        var found = IntPtr.Zero;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var owner);
                if (owner != (uint)pid) return true;
                // 不再跳过隐藏窗口：Electron 常把窗口藏到托盘，PAD 的职责是把它显示出来。
                if (GetWindowTextLength(hWnd) == 0) return true;
                found = hWnd;
                return false;
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }
}
