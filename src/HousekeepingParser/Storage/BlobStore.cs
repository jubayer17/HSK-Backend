using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace HousekeepingParser.Storage;

/// <summary>Blob containers from plan section 6.</summary>
public static class Containers
{
    public const string Incoming = "incoming-pdf";
    public const string Marked = "marked-pdf";
    public const string Archive = "processed-archive";
    public const string Failed = "failed-pdf";

    /// <summary>Report emails as .eml files (Email:Mode = Outbox).</summary>
    public const string Outbox = "email-outbox";

    public static readonly string[] All = [Incoming, Marked, Archive, Failed, Outbox];
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Azure: the account's blob endpoint, reached with Managed Identity (no key, rule 12).</summary>
    public string? BlobServiceUri { get; set; }

    /// <summary>Local development only, e.g. "UseDevelopmentStorage=true" for Azurite.</summary>
    public string? ConnectionString { get; set; }
}

public interface IBlobStore
{
    Task EnsureContainersAsync(CancellationToken ct);
    Task UploadAsync(string container, string name, byte[] bytes, CancellationToken ct);
    Task<byte[]> DownloadAsync(string container, string name, CancellationToken ct);
    Task<Stream?> OpenReadAsync(string container, string name, CancellationToken ct);
    Task MoveAsync(string fromContainer, string toContainer, string name, CancellationToken ct);
    Task<int> DeleteOlderThanAsync(string container, DateTimeOffset cutoff, CancellationToken ct);
}

public sealed class AzureBlobStore : IBlobStore
{
    private readonly BlobServiceClient _service;

    public AzureBlobStore(IOptions<StorageOptions> options)
    {
        var o = options.Value;
        _service = !string.IsNullOrEmpty(o.ConnectionString)
            ? new BlobServiceClient(o.ConnectionString)
            : new BlobServiceClient(new Uri(o.BlobServiceUri ?? throw new InvalidOperationException("Storage:BlobServiceUri is not set.")), new DefaultAzureCredential());
    }

    public async Task EnsureContainersAsync(CancellationToken ct)
    {
        foreach (var name in Containers.All)
            await _service.GetBlobContainerClient(name).CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
    }

    public Task UploadAsync(string container, string name, byte[] bytes, CancellationToken ct) =>
        Blob(container, name).UploadAsync(new BinaryData(bytes), new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf" },
        }, ct);

    public async Task<byte[]> DownloadAsync(string container, string name, CancellationToken ct) =>
        (await Blob(container, name).DownloadContentAsync(ct)).Value.Content.ToArray();

    public async Task<Stream?> OpenReadAsync(string container, string name, CancellationToken ct)
    {
        try
        {
            return await Blob(container, name).OpenReadAsync(cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task MoveAsync(string fromContainer, string toContainer, string name, CancellationToken ct)
    {
        var source = Blob(fromContainer, name);
        if (!await source.ExistsAsync(ct)) return; // already moved (activity retried)
        var bytes = (await source.DownloadContentAsync(ct)).Value.Content;
        await Blob(toContainer, name).UploadAsync(bytes, overwrite: true, ct);
        await source.DeleteIfExistsAsync(cancellationToken: ct);
    }

    public async Task<int> DeleteOlderThanAsync(string container, DateTimeOffset cutoff, CancellationToken ct)
    {
        var client = _service.GetBlobContainerClient(container);
        var deleted = 0;
        await foreach (var item in client.GetBlobsAsync(cancellationToken: ct))
        {
            if (item.Properties.CreatedOn is { } created && created < cutoff)
            {
                await client.DeleteBlobIfExistsAsync(item.Name, cancellationToken: ct);
                deleted++;
            }
        }
        return deleted;
    }

    private BlobClient Blob(string container, string name) => _service.GetBlobContainerClient(container).GetBlobClient(name);
}
