using System.Text;

namespace NetScope.Diff;

public enum EditKind { Same, Removed, Added }

/// <summary>One line of an edit script: index into the old and/or new sequence.</summary>
public readonly record struct Edit(EditKind Kind, int OldIndex, int NewIndex);

/// <summary>
/// Shortest edit script between two line sequences (Myers' O(ND) algorithm), compared by a key so that
/// lines can differ in what is shown (IL offsets) but not in what is compared.
/// </summary>
public static class LineDiff
{
    /// <summary>Beyond this many differences the script degrades to "remove all, add all".</summary>
    public const int MaxDifferences = 4000;

    public static List<Edit> Compute(IReadOnlyList<string> oldKeys, IReadOnlyList<string> newKeys)
    {
        int n = oldKeys.Count, m = newKeys.Count;
        // Common prefix and suffix are cheap and keep the search small for typical edits.
        int prefix = 0;
        while (prefix < n && prefix < m && oldKeys[prefix] == newKeys[prefix]) prefix++;
        int suffix = 0;
        while (suffix < n - prefix && suffix < m - prefix && oldKeys[n - 1 - suffix] == newKeys[m - 1 - suffix]) suffix++;

        var edits = new List<Edit>(Math.Max(n, m));
        for (int i = 0; i < prefix; i++) edits.Add(new Edit(EditKind.Same, i, i));
        edits.AddRange(Middle(oldKeys, newKeys, prefix, n - suffix, prefix, m - suffix));
        for (int i = 0; i < suffix; i++) edits.Add(new Edit(EditKind.Same, n - suffix + i, m - suffix + i));
        return edits;
    }

    private static IEnumerable<Edit> Middle(IReadOnlyList<string> a, IReadOnlyList<string> b, int aStart, int aEnd, int bStart, int bEnd)
    {
        int n = aEnd - aStart, m = bEnd - bStart;
        if (n == 0 && m == 0) return Array.Empty<Edit>();
        int max = Math.Min(n + m, MaxDifferences);
        int offset = max;
        var v = new int[2 * max + 2];
        var trace = new List<int[]>();
        int found = -1;
        for (int d = 0; d <= max && found < 0; d++)
        {
            trace.Add((int[])v.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[aStart + x] == b[bStart + y]) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
        }
        if (found < 0)
        {
            // Too different: show the whole range as replaced.
            var all = new List<Edit>(n + m);
            for (int i = 0; i < n; i++) all.Add(new Edit(EditKind.Removed, aStart + i, -1));
            for (int j = 0; j < m; j++) all.Add(new Edit(EditKind.Added, -1, bStart + j));
            return all;
        }

        // Walk the trace back from (n, m) to (0, 0).
        var result = new List<Edit>();
        int cx = n, cy = m;
        for (int d = found; d > 0; d--)
        {
            var pv = trace[d];
            int k = cx - cy;
            int prevK = k == -d || (k != d && pv[offset + k - 1] < pv[offset + k + 1]) ? k + 1 : k - 1;
            int prevX = pv[offset + prevK], prevY = prevX - prevK;
            while (cx > prevX && cy > prevY) { cx--; cy--; result.Add(new Edit(EditKind.Same, aStart + cx, bStart + cy)); }
            if (cx == prevX) { cy--; result.Add(new Edit(EditKind.Added, -1, bStart + cy)); }
            else { cx--; result.Add(new Edit(EditKind.Removed, aStart + cx, -1)); }
        }
        while (cx > 0 && cy > 0) { cx--; cy--; result.Add(new Edit(EditKind.Same, aStart + cx, bStart + cy)); }
        result.Reverse();
        return result;
    }

    /// <summary>Unified diff (3 lines of context) of the shown lines; empty when nothing changed.</summary>
    public static string Unified(IReadOnlyList<string> oldShown, IReadOnlyList<string> newShown, List<Edit> edits, string oldName, string newName, int context = 3)
    {
        var changes = Enumerable.Range(0, edits.Count).Where(i => edits[i].Kind != EditKind.Same).ToList();
        if (changes.Count == 0) return "";

        // Changes separated by at most 2 * context unchanged lines share one hunk.
        var hunks = new List<(int Start, int End)>();
        int first = changes[0], last = changes[0];
        foreach (var c in changes.Skip(1))
        {
            if (c - last - 1 > 2 * context)
            {
                hunks.Add((first, last));
                first = c;
            }
            last = c;
        }
        hunks.Add((first, last));

        var sb = new StringBuilder();
        sb.Append("--- ").Append(oldName).Append('\n');
        sb.Append("+++ ").Append(newName).Append('\n');
        foreach (var (firstChange, lastChange) in hunks)
        {
            int start = Math.Max(0, firstChange - context), end = Math.Min(edits.Count, lastChange + context + 1);
            var hunk = edits.GetRange(start, end - start);
            int oldFirst = FirstIndex(edits, start, e => e.OldIndex), newFirst = FirstIndex(edits, start, e => e.NewIndex);
            int oldCount = hunk.Count(e => e.Kind != EditKind.Added), newCount = hunk.Count(e => e.Kind != EditKind.Removed);
            sb.Append("@@ -").Append(Range(oldFirst, oldCount)).Append(" +").Append(Range(newFirst, newCount)).Append(" @@\n");
            foreach (var e in hunk)
            {
                switch (e.Kind)
                {
                    case EditKind.Same: sb.Append(' ').Append(newShown[e.NewIndex]).Append('\n'); break;
                    case EditKind.Removed: sb.Append('-').Append(oldShown[e.OldIndex]).Append('\n'); break;
                    default: sb.Append('+').Append(newShown[e.NewIndex]).Append('\n'); break;
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>0-based index of the first line of a side at or after the position (for the @@ header).</summary>
    private static int FirstIndex(List<Edit> edits, int from, Func<Edit, int> index)
    {
        for (int j = from; j < edits.Count; j++)
            if (index(edits[j]) >= 0) return index(edits[j]);
        // The side has no line from here on: count the lines before it.
        for (int j = from - 1; j >= 0; j--)
            if (index(edits[j]) >= 0) return index(edits[j]) + 1;
        return 0;
    }

    private static string Range(int first, int count) => count == 1 ? (first + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
        : $"{(count == 0 ? first : first + 1)},{count}";
}
