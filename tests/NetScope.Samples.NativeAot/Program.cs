using System.Runtime.CompilerServices;

namespace AotSample;

public static class Program
{
    private static readonly double[] Sizes = { 1.5, 2.5 };

    public static async Task<int> Main(string[] args)
    {
        var ledger = new Ledger();
        ledger.Deposit("alice", 120);
        ledger.Deposit("bob", 30);
        Console.WriteLine(ledger.Report());
        Console.WriteLine(Geometry.ScaleAll(Sizes, args.Length + 2).Sum());
        Console.WriteLine(await Pipeline.CountWordsAsync("native ahead of time compilation").ConfigureAwait(false));
        try
        {
            Ledger.ThrowInsufficientFunds(args.Length);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine(ex.Message);
        }
        return 0;
    }
}

public sealed class Ledger
{
    private readonly Dictionary<string, decimal> _balances = new(StringComparer.Ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Deposit(string owner, decimal amount)
    {
        _balances[owner] = _balances.TryGetValue(owner, out var current) ? current + amount : amount;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public string Report() => string.Join(", ", _balances.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowInsufficientFunds(int code) => throw new InvalidOperationException("insufficient funds " + code);
}

public static class Geometry
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T[] ScaleAll<T>(T[] values, int factor) where T : System.Numerics.INumber<T>
    {
        var result = new T[values.Length];
        for (int i = 0; i < values.Length; i++) result[i] = values[i] * T.CreateChecked(factor);
        return result;
    }
}

public static class Pipeline
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<int> CountWordsAsync(string text)
    {
        await Task.Yield();
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
