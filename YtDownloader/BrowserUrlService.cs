using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace YtDownloader;

public static class BrowserUrlService
{
    private static readonly string[] BrowserClasses =
    {
        "Chrome_WidgetWin_1",   // Chrome, Edge, Brave, Opera, Vivaldi, Arc, etc.
        "MozillaWindowClass",    // Firefox
    };

    public static (string? Url, string? BrowserWindowTitle) TryGetTopmostBrowserUrl(IntPtr selfHwnd)
    {
        foreach (var hwnd in EnumerateZOrder())
        {
            if (hwnd == selfHwnd) continue;
            if (!IsWindowVisible(hwnd)) continue;
            if (IsIconic(hwnd)) continue;

            var cls = GetWindowClass(hwnd);
            if (Array.IndexOf(BrowserClasses, cls) < 0) continue;

            var url = ReadAddressBar(hwnd);
            if (!string.IsNullOrWhiteSpace(url))
                return (NormalizeUrl(url), GetWindowTitle(hwnd));
        }
        return (null, null);
    }

    private static string NormalizeUrl(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return raw;
        if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return raw;
        return "https://" + raw;
    }

    private static string? ReadAddressBar(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return null;

            var edits = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

            foreach (AutomationElement edit in edits)
            {
                var name = (edit.Current.Name ?? "").ToLowerInvariant();
                if (!(name.Contains("address") || name.Contains("url")
                      || name.Contains("search") || name.Contains("location")))
                    continue;

                if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out var patObj))
                    continue;
                if (patObj is not ValuePattern vp) continue;

                var value = vp.Current.Value;
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        catch { }
        return null;
    }

    private const uint GW_HWNDNEXT = 2;

    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    private static IEnumerable<IntPtr> EnumerateZOrder()
    {
        var h = GetTopWindow(IntPtr.Zero);
        while (h != IntPtr.Zero)
        {
            yield return h;
            h = GetWindow(h, GW_HWNDNEXT);
        }
    }

    private static string GetWindowClass(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetWindowTitle(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }
}
