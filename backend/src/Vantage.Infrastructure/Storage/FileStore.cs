using System.Security.Cryptography;

namespace Vantage.Infrastructure.Storage;

/// <summary>Uploaded files (.pbix, GenAI .html, thumbnails). Local disk in the local build; S3 in AWS.</summary>
public interface IFileStore
{
    /// <summary>Saves the stream under the key and returns its size and SHA-256.</summary>
    Task<(long Size, string Sha256)> SaveAsync(string key, Stream content, CancellationToken ct = default);
    Stream OpenRead(string key);
    /// <summary>Deletes the file if it exists (old versions beyond the last 3, failed uploads).</summary>
    void Delete(string key);
}

public sealed class LocalFileStore(string rootPath) : IFileStore
{
    public async Task<(long Size, string Sha256)> SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            sha.AppendData(buffer, 0, read);
            size += read;
        }
        return (size, Convert.ToHexStringLower(sha.GetHashAndReset()));
    }

    public Stream OpenRead(string key) => File.OpenRead(PathFor(key));

    public void Delete(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string key)
    {
        var full = Path.GetFullPath(Path.Combine(rootPath, key));
        if (!full.StartsWith(Path.GetFullPath(rootPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid storage key.", nameof(key));
        return full;
    }
}
