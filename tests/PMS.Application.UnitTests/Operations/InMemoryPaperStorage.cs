using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PMS.Application.Services;

namespace PMS.Application.UnitTests.Operations;

/// <summary>Keeps uploaded papers in memory for the tests.</summary>
public sealed class InMemoryPaperStorage : IVehicleDocumentStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new();

    public async Task<string> SaveAsync(Stream content, string folder, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var path = $"{folder}/{Guid.NewGuid():N}";
        _files[path] = buffer.ToArray();
        return path;
    }

    public Stream OpenRead(string storagePath) =>
        _files.TryGetValue(storagePath, out var bytes) ? new MemoryStream(bytes) : null;

    public void Delete(string storagePath) => _files.TryRemove(storagePath, out _);
}
