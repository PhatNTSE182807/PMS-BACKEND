using System.Net.Http;
using System.Net.Http.Json;
using PMS.Application.Common.Email;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Sends email through Brevo's HTTPS API instead of SMTP. Some hosts (e.g. Render's free plan) block
/// every outbound SMTP port, while HTTPS (443) is always open. Used when SmtpSettings:BrevoApiKey is set;
/// the sender address must be verified in the Brevo account.
/// </summary>
public class BrevoEmailService(IHttpClientFactory httpClientFactory, SmtpSettings smtpSettings) : IEmailService
{
    public const string HttpClientName = "Brevo";

    private const string SendUrl = "https://api.brevo.com/v3/smtp/email";

    public async Task SendEmailAsync(EmailMessage emailMessage)
    {
        var payload = new
        {
            sender = new { name = smtpSettings.SenderName, email = smtpSettings.SenderEmail },
            to = new[] { new { email = emailMessage.ToAddress, name = emailMessage.ToAddress.Split('@')[0] } },
            subject = emailMessage.Subject,
            htmlContent = emailMessage.Body,
            // Brevo rejects an empty attachment list, so leave the field out when there is none
            attachment = emailMessage.Attachments.Count == 0
                ? null
                : emailMessage.Attachments.Select(a => new { name = a.Name, content = Convert.ToBase64String(a.Value) }).ToArray()
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, SendUrl)
        {
            Content = JsonContent.Create(payload, options: new System.Text.Json.JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            })
        };
        request.Headers.Add("api-key", smtpSettings.BrevoApiKey);
        request.Headers.Add("accept", "application/json");

        using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Brevo could not send the email ({(int)response.StatusCode}): {body}");
        }
    }
}
