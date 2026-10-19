using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PMS.Application.Services.Ocr;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Keeps the photos on the server's disk under <see cref="VehicleDocumentStorageOptions.StorageRoot"/>
/// (App_Data by default, outside wwwroot). Names are random GUIDs without extensions, so a path
/// reveals nothing and cannot be guessed; the folder is one per verification.
/// </summary>
public class LocalVehicleDocumentStorage : IVehicleDocumentStorage
{
    private readonly string _root;

    public LocalVehicleDocumentStorage(VehicleDocumentStorageOptions options, string contentRootPath)
    {
        _root = Path.GetFullPath(Path.IsPathRooted(options.StorageRoot)
            ? options.StorageRoot
            : Path.Combine(contentRootPath, options.StorageRoot));
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(Stream content, string folder, CancellationToken cancellationToken = default)
    {
        var relative = Path.Combine(SafeSegment(folder), Guid.NewGuid().ToString("N") + ".bin");
        var fullPath = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var file = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(file, cancellationToken);
        return relative.Replace('\\', '/');
    }

    public Stream OpenRead(string storagePath)
    {
        if (string.IsNullOrEmpty(storagePath))
            return null;

        var fullPath = Resolve(storagePath);
        return File.Exists(fullPath) ? new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    public void Delete(string storagePath)
    {
        if (string.IsNullOrEmpty(storagePath))
            return;

        var fullPath = Resolve(storagePath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);

        // Remove the verification's folder once its last file is gone
        var directory = Path.GetDirectoryName(fullPath);
        if (directory != null && directory != _root && Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
            Directory.Delete(directory);
    }

    /// <summary>Full path inside the root; anything that would escape it (e.g. "..") is refused.</summary>
    private string Resolve(string relative)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_root, relative));
        if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid document storage path.");
        return fullPath;
    }

    private static string SafeSegment(string folder)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            folder = folder.Replace(c, '_');
        return folder.Replace("..", "_");
    }
}
