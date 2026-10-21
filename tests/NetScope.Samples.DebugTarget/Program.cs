using System;

namespace NetScope.Samples.DebugTarget;

internal static class Program
{
    private static int Add(int a, int b) => a + b;

    private static void Main()
    {
        Console.WriteLine("debug-target start");
        int r = Add(41, 1);
        Console.WriteLine("debug-target result " + r);
    }
}
