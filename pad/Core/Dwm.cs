using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Pad.Core;

/// <summary>
/// Window frame policy. MainWindow is layered; visible corners come from OuterShell's
/// CornerRadius. DWM round on the HWND must stay off during page-size morph — otherwise
/// the pre-tiled transparent surplus gets a visible rounded outline of the big window.
/// </summary>
public static class Dwm
{
    const int DwmwaWindowCornerPreference = 33;
    const int DwmwcpDoNotRound = 1;
    const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Round(Window window) => SetCorners(window, round: true);

    /// <summary>Win11 HWND corner preference. false = square silhouette (morph-safe).</summary>
    public static void SetCorners(Window window, bool round)
    {
        if (Environment.OSVersion.Version.Build < 22000) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var pref = round ? DwmwcpRound : DwmwcpDoNotRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));
    }
}
