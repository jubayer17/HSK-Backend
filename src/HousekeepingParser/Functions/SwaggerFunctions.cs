using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;

namespace HousekeepingParser.Functions;

/// <summary>
/// Swagger UI at /api/swagger and the OpenAPI document at /api/openapi.json.
/// Off unless Swagger:Enabled is true (local development); otherwise both return 404.
/// The spec is hand-written in Functions/openapi.json: update it when an endpoint changes.
/// </summary>
public sealed class SwaggerFunctions(IConfiguration config)
{
    private static readonly Lazy<string> Spec = new(() =>
    {
        using var s = typeof(SwaggerFunctions).Assembly.GetManifestResourceStream("HousekeepingParser.openapi.json")
            ?? throw new InvalidOperationException("openapi.json is not embedded.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    });

    // Pinned Swagger UI from cdnjs; the page and the API share an origin, so the session cookie is sent.
    private const string UiHtml = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>HSK Automation API</title>
          <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/swagger-ui/5.17.14/swagger-ui.min.css" />
        </head>
        <body>
          <div id="swagger-ui"></div>
          <script src="https://cdnjs.cloudflare.com/ajax/libs/swagger-ui/5.17.14/swagger-ui-bundle.min.js"></script>
          <script>
            window.ui = SwaggerUIBundle({
              url: "openapi.json",
              dom_id: "#swagger-ui",
              deepLinking: true,
              persistAuthorization: true,
              requestInterceptor: (req) => { req.credentials = "same-origin"; return req; },
            });
          </script>
        </body>
        </html>
        """;

    private bool Enabled => config.GetValue("Swagger:Enabled", false);

    [Function("SwaggerUi")]
    public IActionResult Ui([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "swagger")] HttpRequest req) =>
        Enabled ? new ContentResult { Content = UiHtml, ContentType = "text/html; charset=utf-8", StatusCode = 200 } : new NotFoundResult();

    [Function("OpenApiDocument")]
    public IActionResult Document([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "openapi.json")] HttpRequest req) =>
        Enabled ? new ContentResult { Content = Spec.Value, ContentType = "application/json; charset=utf-8", StatusCode = 200 } : new NotFoundResult();
}
