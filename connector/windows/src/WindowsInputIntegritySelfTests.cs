namespace CodexVoice;

internal static class WindowsInputIntegritySelfTests
{
    internal static void Run()
    {
        if (!WindowsInputIntegrity.TryReadLevel((uint)Environment.ProcessId, out var level)
            || level <= 0)
            throw new InvalidOperationException("The real Windows token integrity label was not read.");
        if (!WindowsInputIntegrity.TryCheckTarget((uint)Environment.ProcessId, out var sameError)
            || sameError.Length != 0)
            throw new InvalidOperationException("An equal-integrity target was incorrectly blocked.");
        if (WindowsInputIntegrity.TryCheckTarget(0, out var missingError)
            || missingError.Length == 0)
            throw new InvalidOperationException("An unqueryable target must stop before keyboard input.");
    }
}
