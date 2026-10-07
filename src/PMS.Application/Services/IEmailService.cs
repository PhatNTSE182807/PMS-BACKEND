using PMS.Application.Common.Email;

namespace PMS.Application.Services;

public interface IEmailService
{
    Task SendEmailAsync(EmailMessage emailMessage);
}
