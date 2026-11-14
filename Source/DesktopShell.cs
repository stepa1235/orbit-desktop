using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Forms = System.Windows.Forms;

namespace OrbitDesktop;

internal static class DesktopShell
{
    internal const int WsPopup = unchecked((int)0x80000000);
    internal const int ExToolWindow = 0x80;
    internal const int ExNoActivate = 0x08000000;
    private const uint NoActivate = 0x10;
    private const uint ShowWindow = 0x40;
    private const uint NoOwnerZOrder = 0x200;
    private const uint Previous = 3;

    internal static IntPtr Locate()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            string name = ClassName(window);
            if ((name == "Progman" || name == "WorkerW") &&
                FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero &&
                IsWindowVisible(window))
            {
                found = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    internal static void Position(IntPtr child, IntPtr desktop, Forms.Screen monitor)
    {
        var area = monitor.WorkingArea;
        var aboveDesktop = GetWindow(desktop, Previous);
        if (aboveDesktop == child && !IsIconic(child) && IsWindowVisible(child) &&
            GetWindowRect(child, out var bounds) && bounds.Left == area.Left && bounds.Top == area.Top &&
            bounds.Right == area.Right && bounds.Bottom == area.Bottom)
            return;
        if (aboveDesktop == child) aboveDesktop = GetWindow(child, Previous);
        if (IsIconic(child)) ShowWindowWithoutActivation(child, 4);
        SetWindowPos(child, aboveDesktop, area.Left, area.Top, area.Width, area.Height, NoActivate | ShowWindow | NoOwnerZOrder);
    }

    internal sealed class DesktopEvents : IDisposable
    {
        private readonly EventProc callback;
        private readonly IntPtr foregroundHook, minimizeHook;
        private IntPtr reorderHook;
        private uint explorerProcess;

        internal DesktopEvents(Action changed, Action reordered)
        {
            callback = (_, kind, _, objectId, childId, _, _) =>
            {
                if (kind == 0x8004)
                {
                    if (objectId == 0 && childId == 0) reordered();
                    return;
                }
                changed();
            };
            foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, callback, 0, 0, 2);
            minimizeHook = SetWinEventHook(0x16, 0x17, IntPtr.Zero, callback, 0, 0, 2);
        }

        internal void WatchDesktop(IntPtr desktop)
        {
            uint pid = 0;
            if (desktop != IntPtr.Zero) GetWindowThreadProcessId(desktop, out pid);
            if (pid == explorerProcess) return;
            if (reorderHook != IntPtr.Zero) UnhookWinEvent(reorderHook);
            explorerProcess = pid;
            reorderHook = pid == 0 ? IntPtr.Zero : SetWinEventHook(0x8004, 0x8004, IntPtr.Zero, callback, pid, 0, 2);
        }

        public void Dispose()
        {
            if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
            if (minimizeHook != IntPtr.Zero) UnhookWinEvent(minimizeHook);
            if (reorderHook != IntPtr.Zero) UnhookWinEvent(reorderHook);
        }
    }

    internal static void MovePopup(IntPtr window, int x, int y) =>
        SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, NoActivate | 0x5);

    internal static bool HasForegroundApp(IntPtr desktop, IntPtr self, Forms.Screen monitor)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == desktop || foreground == self ||
            IsChild(desktop, foreground) || !GetWindowRect(foreground, out var bounds))
            return false;

        GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == Environment.ProcessId || ClassName(foreground) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;

        var area = monitor.WorkingArea;
        int width = Math.Max(0, Math.Min(bounds.Right, area.Right) - Math.Max(bounds.Left, area.Left));
        int height = Math.Max(0, Math.Min(bounds.Bottom, area.Bottom) - Math.Max(bounds.Top, area.Top));
        return (long)width * height >= (long)area.Width * area.Height * 0.94;
    }

    internal static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    internal static void Reveal(string path) => Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
    {
        UseShellExecute = true
    });

    internal static string ClassName(IntPtr h)
    {
        var name = new StringBuilder(256);
        GetClassName(h, name, name.Capacity);
        return name.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }

    private delegate bool EnumProc(IntPtr handle, IntPtr data);
    private delegate void EventProc(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint thread, uint timestamp);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, EventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")] private static extern bool ShowWindowWithoutActivation(IntPtr handle, int mode);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}
