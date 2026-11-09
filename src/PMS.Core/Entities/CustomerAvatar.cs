using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// A customer's profile photo, kept in the database so it survives redeploys of the API
/// (the hosting disk is wiped on every deploy). One row per customer; a new upload replaces it
/// under a new random Id, so the public URL changes and browsers never show a stale photo.
/// </summary>
public class CustomerAvatar
{
    public Guid Id { get; set; }

    public int CustomerId { get; set; }

    public byte[] Content { get; set; }

    public string ContentType { get; set; }

    public DateTime CreatedAt { get; set; }
}
