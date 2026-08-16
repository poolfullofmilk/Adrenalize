using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Adrenalize.Native;

[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    // Window Message And Show State Codes
    internal const uint WindowMessageClose = 0x0010;
    internal const int ShowWindowHide = 0;
    internal const int ShowWindowRestore = 9;

    // System Menu Item And Flag Codes
    internal const uint SystemCommandClose = 0xF060;
    internal const uint MenuFlagByCommand = 0x00000000;

    // Console Input Handle And Mode Flags
    internal const int StandardInputHandle = -10;
    internal const uint EnableLineInput = 0x0002;
    internal const uint EnableEchoInput = 0x0004;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wordParameter,
        IntPtr longParameter
    );

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(
        [MarshalAs(UnmanagedType.FunctionPtr)] EnumWindowsCallback callback,
        IntPtr parameter
    );

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        out uint processIdentifier
    );

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr windowHandle);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetConsoleWindow();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr GetStdHandle(int standardHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetConsoleMode(IntPtr consoleHandle, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleMode(IntPtr consoleHandle, uint mode);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr windowHandle, int showCommand);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetSystemMenu(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.Bool)] bool revert
    );

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteMenu(IntPtr menuHandle, uint itemIdentifier, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(IntPtr windowHandle);
}
