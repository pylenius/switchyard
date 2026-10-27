using System.Runtime.InteropServices;

namespace Switchyard;

/// <summary>
/// The text UI draws with ANSI escape codes. Windows Terminal and PowerShell windows already interpret
/// them; a plain console (cmd.exe, or the hidden one winpty gives a program under Git Bash's mintty)
/// shows them as text until the program turns virtual terminal processing on for its output.
/// </summary>
static class WindowsConsole
{
    const int StdOutputHandle = -11;
    const uint EnableVirtualTerminalProcessing = 0x0004;

    public static void EnableAnsi()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var handle = GetStdHandle(StdOutputHandle);
        if (GetConsoleMode(handle, out var mode))
            SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
