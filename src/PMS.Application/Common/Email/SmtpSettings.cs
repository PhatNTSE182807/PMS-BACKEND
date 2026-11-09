namespace PMS.Application.Common.Email;

public class SmtpSettings
{
    public string Server { get; set; }

    public int Port { get; set; }

    public string SenderName { get; set; }

    public string SenderEmail { get; set; }

    public string Username { get; set; }

    public string Password { get; set; }

    /// <summary>
    /// Brevo API key. When set, email goes out over HTTPS instead of SMTP, for hosts that block SMTP ports
    /// (Render's free plan blocks 25, 465 and 587).
    /// </summary>
    public string BrevoApiKey { get; set; }

    public bool UsesBrevo => !string.IsNullOrWhiteSpace(BrevoApiKey) && !BrevoApiKey.StartsWith('<');

    /// <summary>False while appsettings.json still holds the "&lt;account&gt;" placeholders.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Server) &&
        !string.IsNullOrWhiteSpace(Username) && !Username.StartsWith('<') &&
        !string.IsNullOrWhiteSpace(Password) && !Password.StartsWith('<');
}
