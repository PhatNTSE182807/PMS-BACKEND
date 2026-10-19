using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PMS.Application.Services;

/// <summary>
/// Private storage for vehicle paper photos. Files are never served statically: the API streams them
/// only to the uploader and to Staff, after checking access.
/// </summary>
public interface IVehicleDocumentStorage
{
    /// <summary>Saves the file and returns its storage path (relative, used to read or delete it later).</summary>
    Task<string> SaveAsync(Stream content, string folder, CancellationToken cancellationToken = default);

    /// <summary>Opens a stored file for reading, or null when it no longer exists.</summary>
    Stream OpenRead(string storagePath);

    /// <summary>Deletes a stored file; missing files are ignored.</summary>
    void Delete(string storagePath);
}
