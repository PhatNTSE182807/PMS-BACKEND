using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using PMS.Application.Common.Email;
using PMS.Application.Services;

namespace PMS.Api.IntegrationTests.Helpers.Services;

public class EmailTestService : IEmailService
{
    public static ConcurrentBag<EmailMessage> SentMessages { get; } = new();

    public ConcurrentQueue<EmailMessage> SentEmails { get; } = new();

    public async Task SendEmailAsync(EmailMessage emailMessage)
    {
        await Task.Delay(100);

        SentMessages.Add(emailMessage);
        SentEmails.Enqueue(emailMessage);
    }
}
