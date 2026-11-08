using System.Net;
using System.Net.Mail;
using Bindery.Host.Configuration;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Notifications;

/// <summary>A message on its way out, already addressed.</summary>
public sealed record OutgoingMessage(string Address, string Subject, string Body);

/// <summary>
/// One way of reaching a person. Email is the only one shipped; a webhook or ntfy channel
/// would implement this and be picked up by the worker without touching any producer.
/// </summary>
public interface INotificationChannel
{
    string Name { get; }

    bool IsConfigured { get; }

    Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Email over SMTP with STARTTLS, or into a pickup directory as .eml files.
/// </summary>
/// <remarks>
/// <see cref="SmtpClient"/> rather than MailKit: it ships with the framework, so the image
/// gains no dependency, and the one thing it cannot do — implicit TLS on 465 — every
/// mainstream provider also offers on 587. The password comes from configuration (a
/// Kubernetes Secret in a real deployment) and is never logged.
/// </remarks>
public sealed class EmailChannel(IOptions<BinderyOptions> options) : INotificationChannel
{
    private readonly EmailOptions _email = options.Value.Notifications.Email;

    public string Name => "email";

    public bool IsConfigured => _email.IsConfigured;

    public async Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        using var mail = new MailMessage
        {
            From = new MailAddress(_email.From ?? "bindery@localhost"),
            Subject = NotificationService.SingleLine(message.Subject),
            Body = message.Body,
            IsBodyHtml = false,
            BodyEncoding = System.Text.Encoding.UTF8,
            SubjectEncoding = System.Text.Encoding.UTF8
        };

        mail.To.Add(new MailAddress(message.Address));
        mail.Headers.Add("Auto-Submitted", "auto-generated");

        using var smtp = CreateClient();
        await smtp.SendMailAsync(mail, cancellationToken);
    }

    private SmtpClient CreateClient()
    {
        if (!string.IsNullOrWhiteSpace(_email.PickupDirectory))
        {
            Directory.CreateDirectory(_email.PickupDirectory);

            return new SmtpClient
            {
                DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
                PickupDirectoryLocation = _email.PickupDirectory
            };
        }

        var client = new SmtpClient(_email.Host, _email.Port)
        {
            EnableSsl = _email.UseStartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 30_000
        };

        if (!string.IsNullOrEmpty(_email.Username))
        {
            client.Credentials = new NetworkCredential(_email.Username, _email.Password);
        }

        return client;
    }

    /// <summary>A plausible address, checked before anything is queued against it.</summary>
    public static bool IsValidAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 320 || address.Contains('\n') || address.Contains('\r'))
        {
            return false;
        }

        return MailAddress.TryCreate(address.Trim(), out var parsed) && parsed.Address == address.Trim();
    }
}
