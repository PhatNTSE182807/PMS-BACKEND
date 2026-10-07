using System.Threading.Tasks;
using PMS.Application.Common.Email;
using PMS.Application.Services;

namespace PMS.Api.IntegrationTests.Helpers.Services;

public class EmailTestService : IEmailService
{
    public async Task SendEmailAsync(EmailMessage emailMessage)
    {
        await Task.Delay(100);
    }
}
