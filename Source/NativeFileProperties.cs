using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OrbitDesktop;

internal static class NativeFileProperties
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        internal int Size;
        internal uint Mask;
        internal IntPtr Owner;
        [MarshalAs(UnmanagedType.LPWStr)] internal string Verb;
        [MarshalAs(UnmanagedType.LPWStr)] internal string File;
        internal IntPtr Parameters;
        internal IntPtr Directory;
        internal int Show;
        internal IntPtr Instance;
        internal IntPtr IdList;
        internal IntPtr Class;
        internal IntPtr ClassKey;
        internal uint HotKey;
        internal IntPtr Icon;
        internal IntPtr Process;
    }

    internal static void Show(string path)
    {
        var operation = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = 0x0000000C,
            Verb = "properties",
            File = path,
            Show = 1
        };
        if (!ShellExecuteEx(ref operation)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo operation);
}
