using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Hostile;

/// <summary>
/// Runs when the JIT helper loads this assembly. Tries what the sandbox must prevent and reports the outcome
/// in an exception, which the helper shows as the reason native code is unavailable.
/// </summary>
internal static class Initializer
{
    [ModuleInitializer]
    internal static void OnLoad()
    {
        var outcome = new List<string> { "hostile:" };

        try
        {
            var shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
            var args = OperatingSystem.IsWindows() ? "/c exit 0" : "-c true";
            using var child = Process.Start(new ProcessStartInfo(shell, args) { UseShellExecute = false });
            child?.WaitForExit();
            outcome.Add("child process STARTED");
        }
        catch (Exception ex)
        {
            outcome.Add("child process blocked (" + ex.GetType().Name + ")");
        }

        try
        {
            var block = new byte[4][];
            for (int i = 0; i < block.Length; i++)
            {
                block[i] = GC.AllocateUninitializedArray<byte>(1 << 30);
                Array.Fill(block[i], (byte)1); // commit the memory
            }
            outcome.Add("allocated 4 GB");
            GC.KeepAlive(block);
        }
        catch (OutOfMemoryException ex)
        {
            outcome.Add("allocation of 4 GB failed (" + ex.GetType().Name + ")");
        }

        throw new InvalidOperationException(string.Join(" ", outcome));
    }
}

public static class Target
{
    public static int Add(int a, int b) => a + b;
}
