using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core.Pipeline;
using Azure.Communication.Email;
using HousekeepingParser.Reports;
using Microsoft.Extensions.Options;
using Xunit;

namespace HousekeepingParser.Tests;

public class AzureEmailTests
{
    [Fact]
    public async Task Azure_mode_sends_pdf_and_waits_for_provider_success()
    {
        var handler = new EmailHandler();
        using var http = new HttpClient(handler);
        var client = new EmailClient(new Uri("https://test.communication.azure.com"),
            new AzureKeyCredential(Convert.ToBase64String(new byte[32])),
            new EmailClientOptions { Transport = new HttpClientTransport(http) });
        var sender = new EmailSender(Options.Create(new EmailOptions { Mode = "Azure", From = "sender@example.com" }), new MemoryBlobStore(), client);
        await sender.SendAsync(new HousekeepingParser.Reports.EmailMessage(["recipient@example.com"], "Test report", "Body",
            [new HousekeepingParser.Reports.EmailAttachment("report.pdf", Encoding.ASCII.GetBytes("%PDF-test"))]), default);
        Assert.Equal("azure", sender.Transport);
        using var body = JsonDocument.Parse(handler.Body!);
        var attachment = Assert.Single(body.RootElement.GetProperty("attachments").EnumerateArray());
        Assert.Equal("application/pdf", attachment.GetProperty("contentType").GetString());
        Assert.Equal("report.pdf", attachment.GetProperty("name").GetString());
        Assert.Equal(Encoding.ASCII.GetBytes("%PDF-test"), Convert.FromBase64String(attachment.GetProperty("contentInBase64").GetString()!));
        Assert.True(handler.Polled);
    }

    [Fact]
    public async Task Azure_mode_without_configuration_does_not_fall_back_to_outbox()
    {
        var blobs = new MemoryBlobStore();
        var sender = new EmailSender(Options.Create(new EmailOptions { Mode = "Azure" }), blobs);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(
            new HousekeepingParser.Reports.EmailMessage(["recipient@example.com"], "Test", "Body", []), default));
        Assert.Empty(blobs.Blobs);
    }

    private sealed class EmailHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public bool Polled { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                Body = await request.Content!.ReadAsStringAsync(ct);
                var response = new HttpResponseMessage(HttpStatusCode.Accepted)
                    { Content = new StringContent("{\"id\":\"test-operation\",\"status\":\"Running\"}", Encoding.UTF8, "application/json") };
                response.Headers.Add("operation-location", "https://test.communication.azure.com/emails/operations/test-operation?api-version=2023-03-31");
                response.Headers.Add("retry-after", "0");
                return response;
            }
            Polled = true;
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"id\":\"test-operation\",\"status\":\"Succeeded\"}", Encoding.UTF8, "application/json") };
        }
    }
}
