using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexVoice;

/// <summary>Allows remote keyboard input only on this connected user's normal desktop.</summary>
internal static class CliInteractiveDesktopGuard
{
    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;
    private const int WtsConnectState = 8;
    private const int WtsActive = 0;

    internal static bool IsReady()
    {
        if (!OperatingSystem.IsWindows()) return false;
        IntPtr stateBuffer = IntPtr.Zero;
        IntPtr desktop = IntPtr.Zero;
        try
        {
            // OpenInputDesktop alone is insufficient: on a disconnected session it
            // can return the desktop that will become active after reconnection.
            var sessionId = Process.GetCurrentProcess().SessionId;
            if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WtsConnectState,
                    out stateBuffer, out var length)
                || stateBuffer == IntPtr.Zero || length < sizeof(int)
                || Marshal.ReadInt32(stateBuffer) != WtsActive)
                return false;

            desktop = OpenInputDesktop(0, false, DesktopReadObjects);
            if (desktop == IntPtr.Zero) return false;
            var name = new StringBuilder(64);
            if (!GetUserObjectInformationW(desktop, UoiName, name,
                    (uint)(name.Capacity * sizeof(char)), out var needed)
                || needed == 0 || needed > name.Capacity * sizeof(char))
                return false;
            return string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
        finally
        {
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
            if (stateBuffer != IntPtr.Zero) WTSFreeMemory(stateBuffer);
        }
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId,
        int infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr buffer);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(IntPtr desktop, int index,
        StringBuilder value, uint length, out uint needed);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
