using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace TopKFinder.Tests;

public sealed class CounterSnapshotExporterTests
{
    [Fact]
    public void ExportCompactCounterSnapshot()
    {
        if (!ShouldExport("compact"))
            return;

        IReadOnlyList<CompactCounterSnapshotExportRow> rows = CounterSnapshotExportHelpers.BuildCompactSnapshotRows();
        ExportRows(rows, "CounterSnapshotCompact");
    }

    [Fact]
    public void ExportIterativeCounterSnapshot()
    {
        if (!ShouldExport("iterative"))
            return;

        IReadOnlyList<IterativeCounterSnapshotExportRow> rows = CounterSnapshotExportHelpers.BuildIterativeSnapshotRows();
        ExportRows(rows, "CounterSnapshotIterative");
    }

    private static bool ShouldExport(string expectedKind)
        => string.Equals(Environment.GetEnvironmentVariable("COUNTER_SNAPSHOT_KIND"), expectedKind, StringComparison.OrdinalIgnoreCase);

    private static void ExportRows<T>(IReadOnlyList<T> rows, string prefix)
    {
        string jsonPath = RequirePath("COUNTER_SNAPSHOT_JSON_PATH");
        string csvPath = RequirePath("COUNTER_SNAPSHOT_CSV_PATH");

        WriteJson(rows, jsonPath);
        WriteCsv(rows, csvPath);

        Console.WriteLine($"Wrote {prefix} snapshot JSON: {jsonPath}");
        Console.WriteLine($"Wrote {prefix} snapshot CSV:  {csvPath}");
    }

    private static string RequirePath(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Missing required environment variable {name}.");

        string? directory = Path.GetDirectoryName(value);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        return value;
    }

    private static void WriteJson<T>(IReadOnlyList<T> rows, string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        File.WriteAllText(path, JsonSerializer.Serialize(rows, options));
    }

    private static void WriteCsv<T>(IReadOnlyList<T> rows, string path)
    {
        if (rows.Count == 0)
        {
            File.WriteAllText(path, string.Empty);
            return;
        }

        var properties = typeof(T).GetProperties();
        string header = string.Join(",", properties.Select(property => property.Name));
        var lines = new List<string> { header };

        foreach (T row in rows)
        {
            lines.Add(string.Join(",", properties.Select(property => Convert.ToString(property.GetValue(row), System.Globalization.CultureInfo.InvariantCulture))));
        }

        File.WriteAllText(path, string.Join(Environment.NewLine, lines));
    }
}