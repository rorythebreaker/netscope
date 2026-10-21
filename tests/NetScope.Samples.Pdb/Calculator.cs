namespace PdbSample;

public static class Calculator
{
    public static int SumOfSquares(int[] values)
    {
        int total = 0;
        foreach (var value in values)
        {
            int square = value * value;
            total += square;
        }
        return total;
    }

    public static string Describe(int total) => total > 100 ? "large" : "small";

    /// <summary>An audit finding (NS005) with a source line, for SARIF tests.</summary>
    public static string Fingerprint(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return System.Convert.ToHexString(System.Security.Cryptography.MD5.HashData(bytes));
    }
}
