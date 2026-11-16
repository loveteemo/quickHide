// Win32.cs —— quickHide 工程拆分文件（v1.3.0 起由单文件拆分而来）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace quickHide
{
// ---------- Win32 ----------

[StructLayout(LayoutKind.Sequential)]
public struct WINDOWPLACEMENT
{
    public int length;
    public int flags;
    public int showCmd;
    public POINT ptMinPosition;
    public POINT ptMaxPosition;
    public RECT rcNormalPosition;
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int x, y; }

[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int left, top, right, bottom; }

static class Win32
{
    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_RESTORE = 9;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int val, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    // Win10 1809+ / Win11 深色标题栏
    public static void EnableDarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, 4);
    }

    const int DWMWA_CLOAKED = 14;

    public static bool IsCloaked(IntPtr hwnd)
    {
        int v;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out v, 4) == 0 && v != 0) return true;
        return false;
    }
}

// Windows 任务栏按钮控制：DeleteTab 精确移除单个窗口的按钮，AddTab 恢复
// 比修改 WS_EX_TOOLWINDOW 更干净（不需要隐藏/显示窗口，也不会改变窗口外观）
static class TaskbarList
{
    static ITaskbarList list;
    static bool failed;
    static readonly object gate = new object();

    static ITaskbarList Get()
    {
        lock (gate)
        {
            if (failed) return null;
            if (list != null) return list;
            try
            {
                list = (ITaskbarList)new TaskbarListCoClass();
                list.HrInit();
                return list;
            }
            catch
            {
                failed = true;
                return null;
            }
        }
    }

    public static bool HideTab(IntPtr hwnd)
    {
        var l = Get();
        if (l == null) return false;
        try { l.DeleteTab(hwnd); return true; }
        catch { return false; }
    }

    public static bool ShowTab(IntPtr hwnd)
    {
        var l = Get();
        if (l == null) return false;
        try { l.AddTab(hwnd); return true; }
        catch { return false; }
    }
}

[ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
class TaskbarListCoClass { }

[Guid("56FDF342-FD6D-11D0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ITaskbarList
{
    void HrInit();
    void AddTab(IntPtr hwnd);
    void DeleteTab(IntPtr hwnd);
    void ActivateTab(IntPtr hwnd);
    void SetActiveAlt(IntPtr hwnd);
}
}
