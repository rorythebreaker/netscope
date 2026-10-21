using System.Globalization;
using System.Text;

namespace NetScope.Reporting;

/// <summary>
/// Text that comes from outside NetScope (source files, string literals, values of a debugged process) may
/// contain terminal control sequences. They are escaped before the text reaches the console.
/// </summary>
public static class TextSafety
{
    /// <summary>Replaces C0/C1 control characters (except tab) and DEL with \xNN / \uNNNN escapes.</summary>
    public static string EscapeControl(string text)
    {
        int i = 0;
        while (i < text.Length && !IsControl(text[i])) i++;
        if (i == text.Length) return text;

        var sb = new StringBuilder(text.Length + 8);
        sb.Append(text, 0, i);
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (!IsControl(c)) sb.Append(c);
            else if (c <= 0xFF) sb.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            else sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static bool IsControl(char c) => c != '\t' && (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F));
}
