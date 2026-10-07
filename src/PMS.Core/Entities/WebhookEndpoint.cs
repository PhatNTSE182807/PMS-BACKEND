using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class WebhookEndpoint
{
    public int Id { get; set; }

    public int? IntegrationId { get; set; }

    public int? PartnerId { get; set; }

    public string Url { get; set; }

    public string SecretHash { get; set; }

    public string EventTypes { get; set; }

    public bool IsActive { get; set; }

    public int FailureCount { get; set; }

    public DateTime? LastDeliveryAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
