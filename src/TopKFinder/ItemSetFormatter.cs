using System.Collections.Generic;
using System.Linq;

static class ItemSetFormatter
{
    private const int RangeCompressionMinRunLength = 4;

    public static string FormatSet(IEnumerable<int> items)
    {
        var sorted = items.ToList();
        sorted.Sort();

        var segments = new List<string>();
        int i = 0;
        while (i < sorted.Count)
        {
            int runStart = i;
            while (i + 1 < sorted.Count && sorted[i + 1] == sorted[i] + 1)
                i++;

            int runLength = i - runStart + 1;
            if (runLength >= RangeCompressionMinRunLength)
            {
                segments.Add($"#{sorted[runStart] + 1} ~ #{sorted[i] + 1}");
            }
            else
            {
                for (int j = runStart; j <= i; j++)
                    segments.Add($"#{sorted[j] + 1}");
            }

            i++;
        }

        return string.Join(", ", segments);
    }
}
