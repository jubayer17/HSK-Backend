using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using Azure;
using Azure.Identity;
using Azure.Communication.Email;
using HousekeepingParser.Storage;
using Microsoft.Extensions.Options;

namespace HousekeepingParser.Reports;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Outbox for local previews, Smtp for compatibility, Azure for final Phase 1 delivery.</summary>
    public string Mode { get; set; } = "Outbox";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? Username { get; set; }

    /// <summary>From Key Vault in Azure; never in code.</summary>
    public string? Password { get; set; }

    public string From { get; set; } = "housekeeping@example.com";
    public string FromName { get; set; } = "HSK Automation";
    public string? Endpoint { get; set; }
    public string? ConnectionString { get; set; }

    public bool IsSmtp => string.Equals(Mode, "Smtp", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Host);
    public bool IsAzure => string.Equals(Mode, "Azure", StringComparison.OrdinalIgnoreCase);
}

public sealed record EmailAttachment(string FileName, byte[] Bytes);

public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string Body, IReadOnlyList<EmailAttachment> Attachments);

public interface IEmailSender
{
    /// <summary>"azure", "smtp", or "outbox".</summary>
    string Transport { get; }

    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>Sends via SMTP, or saves to an outbox container.</summary>
public sealed class EmailSender(IOptions<EmailOptions> options, IBlobStore blobs, EmailClient? azureClient = null) : IEmailSender
{
    private readonly EmailOptions _o = options.Value;

    public string Transport => _o.IsAzure ? "azure" : _o.IsSmtp ? "smtp" : "outbox";

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (_o.IsAzure)
        {
            // Explicit Azure mode must fail clearly if unconfigured; silently saving an outbox would hide delivery failures.
            var client = azureClient ?? (!string.IsNullOrWhiteSpace(_o.ConnectionString)
                ? new EmailClient(_o.ConnectionString)
                : !string.IsNullOrWhiteSpace(_o.Endpoint)
                    ? new EmailClient(new Uri(_o.Endpoint), new DefaultAzureCredential())
                    : throw new InvalidOperationException("Azure email requires Email:Endpoint or Email:ConnectionString."));
            var content = new EmailContent(message.Subject) { PlainText = message.Body };
            var recipients = new EmailRecipients(message.To.Select(address => new EmailAddress(address)));
            var outgoing = new Azure.Communication.Email.EmailMessage(_o.From, recipients, content);
            foreach (var attachment in message.Attachments)
                outgoing.Attachments.Add(new Azure.Communication.Email.EmailAttachment(
                    attachment.FileName, MediaTypeNames.Application.Pdf, new BinaryData(attachment.Bytes)));
            // A request being accepted is not enough: wait for the provider's send operation to succeed.
            var operation = await client.SendAsync(WaitUntil.Completed, outgoing, ct);
            if (operation.Value.Status != EmailSendStatus.Succeeded)
                throw new InvalidOperationException("Azure email did not reach a successful send status.");
            return;
        }
        using var mail = Build(message);
        if (_o.IsSmtp)
        {
            using var smtp = new SmtpClient(_o.Host, _o.Port)
            {
                EnableSsl = _o.EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Credentials = string.IsNullOrEmpty(_o.Username) ? null : new NetworkCredential(_o.Username, _o.Password),
            };
            await smtp.SendMailAsync(mail, ct);
            return;
        }

        // Outbox: SmtpClient writes the MIME message to a temp folder; kept as a blob.
        var dir = Directory.CreateTempSubdirectory("hsk-mail-");
        try
        {
            using (var pickup = new SmtpClient { DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory, PickupDirectoryLocation = dir.FullName })
                await pickup.SendMailAsync(mail, ct);
            foreach (var file in dir.GetFiles("*.eml"))
            {
                var name = $"{DateTime.UtcNow:yyyy/MM/dd}/{DateTime.UtcNow:HHmmss}-{file.Name}";
                await blobs.UploadAsync(Containers.Outbox, name, await File.ReadAllBytesAsync(file.FullName, ct), ct);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private MailMessage Build(EmailMessage m)
    {
        var mail = new MailMessage
        {
            From = new MailAddress(_o.From, _o.FromName),
            Subject = m.Subject,
            Body = m.Body,
            IsBodyHtml = false,
            BodyEncoding = System.Text.Encoding.UTF8,
            SubjectEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var to in m.To) mail.To.Add(to);
        foreach (var a in m.Attachments)
            mail.Attachments.Add(new Attachment(new MemoryStream(a.Bytes), a.FileName, MediaTypeNames.Application.Pdf));
        return mail;
    }
}
