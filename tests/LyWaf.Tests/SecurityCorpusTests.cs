using System.Diagnostics;
using System.Text.Json;
using LyWaf.Services.Protect;
using Microsoft.AspNetCore.Http;
using Xunit;
using Xunit.Abstractions;

namespace LyWaf.Tests;

public sealed class SecurityCorpusTests(ITestOutputHelper output)
{
    [Fact]
    public void CorpusMeetsDetectionFalsePositiveLatencyAndAllocationGates()
    {
        var corpus = JsonSerializer.Deserialize<Corpus>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "tests", "fixtures", "security-corpus-v1.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("1.1.0", corpus.Version);
        Assert.Contains("bypass", corpus.Source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("synthetic", corpus.Source, StringComparison.OrdinalIgnoreCase);

        var detector = new SemanticThreatDetector();
        detector.Detect(Snapshot("body", "warmup"), new PathString("/"));
        var latencies = new List<double>();
        var attacks = 0;
        var detected = 0;
        var benign = 0;
        var falsePositives = 0;
        var beforeBytes = GC.GetAllocatedBytesForCurrentThread();

        foreach (var sample in corpus.Samples)
        {
            var value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(sample.ValueBase64));
            var stopwatch = Stopwatch.StartNew();
            var finding = detector.Detect(Snapshot(sample.Source, value),
                sample.Source == "path" ? new PathString(value) : new PathString("/"));
            stopwatch.Stop();
            latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            if (sample.Category is null)
            {
                benign++;
                if (finding is not null) falsePositives++;
            }
            else
            {
                attacks++;
                if (finding?.Category == sample.Category) detected++;
                Assert.Equal(sample.Category, finding?.Category);
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        latencies.Sort();
        var p50 = Percentile(latencies, 0.50);
        var p95 = Percentile(latencies, 0.95);
        var detectionRate = attacks == 0 ? 0 : detected * 100d / attacks;
        var falsePositiveRate = benign == 0 ? 0 : falsePositives * 100d / benign;
        output.WriteLine($"corpus={corpus.Version} samples={corpus.Samples.Count} detection={detectionRate:F2}% falsePositive={falsePositiveRate:F2}% p50={p50:F3}ms p95={p95:F3}ms allocated={allocated}B");

        Assert.Equal(100, detectionRate);
        Assert.Equal(0, falsePositiveRate);
        Assert.True(p95 < 100, $"P95 detection latency {p95:F3}ms exceeded 100ms");
        Assert.True(allocated / corpus.Samples.Count < 256 * 1024,
            $"Average allocation {allocated / corpus.Samples.Count} bytes exceeded 256KiB");
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile) =>
        values[Math.Clamp((int)Math.Ceiling(values.Count * percentile) - 1, 0, values.Count - 1)];

    private static RequestInspectionSnapshot Snapshot(string source, string value) => new(
        source == "query" ? [value] : [],
        source == "header" ? [value] : [],
        source == "body" ? [value] : [],
        false,
        []);

    private sealed record Corpus(string Version, string Source, List<CorpusSample> Samples);
    private sealed record CorpusSample(string Id, string Source, string ValueBase64, string? Category);
}
