using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using PMS.Application.Common.Email;
using PMS.Application.Services;

namespace PMS.Api.IntegrationTests.Helpers.Services;

public class EmailTestService : IEmailService
{
    public static ConcurrentBag<EmailMessage> SentMessages { get; } = new();

    public async Task SendEmailAsync(EmailMessage emailMessage)
    {
        SentMessages.Add(emailMessage);
        await Task.CompletedTask;
    }
}
