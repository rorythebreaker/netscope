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
}
