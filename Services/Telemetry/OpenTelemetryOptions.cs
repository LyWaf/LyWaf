namespace LyWaf.Services.Telemetry;

public sealed class OpenTelemetryOptions
{
    public bool Enabled { get; set; }
    public string ServiceName { get; set; } = "lywaf";
    public string Endpoint { get; set; } = "http://localhost:4317";
    public string Protocol { get; set; } = "grpc";
    public double SampleRatio { get; set; } = 0.1;
    public bool ExportTraces { get; set; } = true;
    public bool ExportMetrics { get; set; } = true;
    public List<string> ExcludedPaths { get; set; } = ["/metrics"];
}
