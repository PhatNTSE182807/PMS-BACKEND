namespace PMS.Application.Common.Email;

public class SmtpSettings
{
    public string Server { get; set; }

    public int Port { get; set; }

    public string SenderName { get; set; }

    public string SenderEmail { get; set; }

    public string Username { get; set; }

    public string Password { get; set; }

    /// <summary>False while appsettings.json still holds the "&lt;account&gt;" placeholders.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Server) &&
        !string.IsNullOrWhiteSpace(Username) && !Username.StartsWith('<') &&
        !string.IsNullOrWhiteSpace(Password) && !Password.StartsWith('<');
}
