using System.Text;
using HousekeepingParser.Core;
using HousekeepingParser.Parsing;
using HousekeepingParser.Storage;
using Xunit;

namespace HousekeepingParser.Tests;

public sealed class FakeClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = utcNow;
}

/// <summary>In-memory blob containers.</summary>
public sealed class MemoryBlobStore : IBlobStore
{
    public Dictionary<(string Container, string Name), byte[]> Blobs { get; } = [];

    public Task EnsureContainersAsync(CancellationToken ct) => Task.CompletedTask;

    public Task UploadAsync(string container, string name, byte[] bytes, CancellationToken ct)
    {
        Blobs[(container, name)] = bytes;
        return Task.CompletedTask;
    }

    public Task<byte[]> DownloadAsync(string container, string name, CancellationToken ct) => Task.FromResult(Blobs[(container, name)]);

    public Task<Stream?> OpenReadAsync(string container, string name, CancellationToken ct) =>
        Task.FromResult<Stream?>(Blobs.TryGetValue((container, name), out var b) ? new MemoryStream(b) : null);

    public Task MoveAsync(string fromContainer, string toContainer, string name, CancellationToken ct)
    {
        if (Blobs.Remove((fromContainer, name), out var b)) Blobs[(toContainer, name)] = b;
        return Task.CompletedTask;
    }

    public Task<int> DeleteOlderThanAsync(string container, DateTimeOffset cutoff, CancellationToken ct) => Task.FromResult(0);
}

/// <summary>Stands in for the Java reader: returns the fixture named inside the synthetic PDF.</summary>
public sealed class FixtureExtractor : ICompliantPdfExtractor
{
    public Task<PdfExtractionResult> ExtractAsync(byte[] pdfBytes, string sourceBlobName, CancellationToken ct)
    {
        var text = Encoding.ASCII.GetString(pdfBytes);
        var at = text.IndexOf("%FIXTURE:", StringComparison.Ordinal);
        if (at < 0) throw new OpenDataLoaderException("not a fixture");
        var name = text[(at + 9)..text.IndexOf('\n', at)];
        var pages = Fixtures.Pages(name);
        return Task.FromResult(new PdfExtractionResult(Guid.NewGuid().ToString("N"), pages, pages.Sum(p => p.Lines.Count), TimeSpan.Zero));
    }
}

public static class SyntheticPdf
{
    /// <summary>A minimal valid PDF (LF line endings, exact xref) with blank pages and an optional fixture tag.</summary>
    public static byte[] Create(int pages, string? fixture = null)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pages).Select(i => $"{i + 3} 0 R"))}] /Count {pages} >>",
        };
        for (var i = 0; i < pages; i++) objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

        var sb = new StringBuilder("%PDF-1.4\n");
        if (fixture is not null) sb.Append($"%FIXTURE:{fixture}\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}

/// <summary>Skipped when no Docker engine is reachable (the SQL Server tests need Testcontainers).</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerCheck.Available) Skip = "Docker is not running: SQL Server integration tests skipped.";
    }
}

internal static class DockerCheck
{
    public static readonly bool Available =
        File.Exists("/var/run/docker.sock")
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST"))
        || OperatingSystem.IsWindows();
}
