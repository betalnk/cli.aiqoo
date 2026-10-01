using System.Runtime.InteropServices;

namespace CodexVoice;

/// <summary>Checks the Windows UIPI boundary before inserting any keyboard events.</summary>
internal static class WindowsInputIntegrity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    internal static bool TryCheckTarget(uint targetProcessId, out string error)
    {
        if (!TryReadLevel((uint)Environment.ProcessId, out var sourceLevel)
            || !TryReadLevel(targetProcessId, out var targetLevel))
        {
            error = "Не удалось проверить права окна Codex. Текст не введён.";
            return false;
        }
        if (sourceLevel < targetLevel)
        {
            error = "Окно Codex запущено с более высокими правами, чем голосовой ввод. "
                + "Windows блокирует передачу текста в это окно. Текст не введён.";
            return false;
        }
        error = "";
        return true;
    }

    internal static bool TryReadLevel(uint processId, out int level)
    {
        level = 0;
        if (processId == 0) return false;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return false;
        var token = IntPtr.Zero;
        var data = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out token)) return false;
            _ = GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);
            if (needed <= 0 || needed > 64 * 1024) return false;
            data = Marshal.AllocHGlobal(needed);
            if (!GetTokenInformation(token, TokenIntegrityLevel, data, needed, out _))
                return false;
            var sid = Marshal.ReadIntPtr(data);
            if (sid == IntPtr.Zero || !IsValidSid(sid)) return false;
            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if (count == 0) return false;
            level = Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)count - 1));
            return true;
        }
        finally
        {
            if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
            if (token != IntPtr.Zero) _ = CloseHandle(token);
            _ = CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass,
        IntPtr information, int length, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern bool IsValidSid(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
