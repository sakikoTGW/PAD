using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Pad.Core;

/// <summary>
/// 全局快捷键（Win32 RegisterHotKey）。规格写成 <c>Ctrl+Alt+P</c>：
/// 修饰键 Ctrl/Alt/Shift/Win 任意组合，键名 A–Z、0–9、F1–F12。
/// 注册失败通常是组合被别的程序占着——要把原因带回去，不要静默失效。
/// </summary>
public static class HotKey
{
    public const int WmHotKey = 0x0312;
    const uint ModAlt = 0x0001, ModCtrl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public static bool TryParse(string? text, out uint modifiers, out uint key, out string error)
    {
        modifiers = 0;
        key = 0;
        error = "";
        var raw = (text ?? "").Trim();
        if (raw.Length == 0) { error = "没有设置快捷键"; return false; }

        var parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) { error = "至少要一个修饰键，例如 Ctrl+Alt+P"; return false; }

        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModCtrl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win" or "windows" or "meta": modifiers |= ModWin; break;
                default:
                    error = $"不认识修饰键「{part}」";
                    return false;
            }
        }

        var last = parts[^1].ToUpperInvariant();
        if (last.Length == 1 && last[0] is >= 'A' and <= 'Z') key = last[0];
        else if (last.Length == 1 && last[0] is >= '0' and <= '9') key = last[0];
        else if (last.StartsWith('F') && int.TryParse(last[1..], out var n) && n is >= 1 and <= 12)
            key = (uint)(0x70 + n - 1);
        else
        {
            error = $"不认识键「{parts[^1]}」（支持 A–Z、0–9、F1–F12）";
            return false;
        }

        if (modifiers == 0) { error = "至少要一个修饰键"; return false; }
        return true;
    }

    public static bool Register(IntPtr hwnd, int id, uint modifiers, uint key) =>
        RegisterHotKey(hwnd, id, modifiers, key);

    public static void Unregister(IntPtr hwnd, int id)
    {
        try { UnregisterHotKey(hwnd, id); } catch { /* 窗口已经在销毁 */ }
    }
}

/// <summary>绑定在某个窗口上的一个全局快捷键；窗口销毁前 Dispose。</summary>
public sealed class HotKeyRegistration : IDisposable
{
    public const int DefaultId = 0x5A17;

    readonly IntPtr _hwnd;
    readonly HwndSource? _source;
    readonly int _id;
    bool _registered;

    /// <summary>按下时触发（已经在 UI 线程上）。</summary>
    public event Action? Pressed;

    /// <summary>没注册成功时的原因（空 = 成功）。</summary>
    public string Error { get; } = "";

    public bool Registered => _registered;
    public string Spec { get; }

    public HotKeyRegistration(Window window, string? spec, int id = DefaultId)
    {
        Spec = (spec ?? "").Trim();
        _id = id;
        try
        {
            _hwnd = new WindowInteropHelper(window).Handle;
            _source = HwndSource.FromHwnd(_hwnd);
        }
        catch
        {
            Error = "窗口句柄拿不到";
            return;
        }
        if (_source is null) { Error = "窗口句柄拿不到"; return; }

        if (!HotKey.TryParse(Spec, out var mods, out var key, out var why)) { Error = why; return; }

        _source.AddHook(WndProc);
        _registered = HotKey.Register(_hwnd, _id, mods, key);
        if (!_registered)
        {
            _source.RemoveHook(WndProc);
            Error = $"「{Spec}」被别的程序占用了，换一个组合";
        }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == HotKey.WmHotKey && (int)wParam == _id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            HotKey.Unregister(_hwnd, _id);
            _registered = false;
        }
        try { _source?.RemoveHook(WndProc); } catch { /* 已销毁 */ }
    }
}
