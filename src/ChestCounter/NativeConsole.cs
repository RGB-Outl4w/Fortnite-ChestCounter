using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ChestCounter;

/// <summary>
/// The exe is a Windows application (a window, and no console at all for the "start with Fortnite" standby process).
/// The command line options (--once, --help) write into the terminal that started them.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NativeConsole
{
    /// <param name="attachToParent">Command line use (--once, --help): write into the terminal that started the exe.</param>
    public static void Ensure(bool attachToParent)
    {
        if (GetConsoleWindow() != IntPtr.Zero)
        {
            return; // already in a console (dotnet host, inherited)
        }
        if (IsRedirected(STD_OUTPUT_HANDLE))
        {
            return; // output goes to a pipe or a file: Console uses it as is
        }
        if (attachToParent && AttachConsole(ATTACH_PARENT_PROCESS))
        {
            return;
        }
        AllocConsole();
    }

    /// <summary>Lets the instance already open take the foreground (Windows only allows it to the active process).</summary>
    public static void AllowOthersToTakeForeground()
    {
        try { AllowSetForegroundWindow(ASFW_ANY); } catch { }
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    private const uint ASFW_ANY = 0xFFFFFFFF;

    private static bool IsRedirected(int std)
    {
        var h = GetStdHandle(std);
        if (h == IntPtr.Zero || h == new IntPtr(-1))
        {
            return false;
        }
        var type = GetFileType(h);
        return type is FILE_TYPE_DISK or FILE_TYPE_PIPE;
    }

    private const int STD_OUTPUT_HANDLE = -11;
    private const int ATTACH_PARENT_PROCESS = -1;
    private const uint FILE_TYPE_DISK = 1, FILE_TYPE_PIPE = 3;

    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int std);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
}
