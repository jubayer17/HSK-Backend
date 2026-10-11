using System.Text.Json;
using System.Text.Json.Serialization;
using HousekeepingParser.Accounts;
using HousekeepingParser.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HousekeepingParser.Functions;

/// <summary>Shared request handling: session cookie → user, role check on the server, errors as {"error": "…"}.</summary>
public sealed class Api(IAuthService auth, ILogger<Api> log)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static IActionResult Ok(object? value, int status = 200) =>
        new JsonResult(value, Json) { StatusCode = status };

    public static IActionResult Error(int status, string message) =>
        new JsonResult(new ErrorDto(message), Json) { StatusCode = status };

    /// <summary>Runs an action for a signed-in user. Pages that show room data are never cached.</summary>
    public async Task<IActionResult> RunAsync(HttpRequest req, Func<CurrentUser, Task<IActionResult>> action)
    {
        req.HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            var user = await auth.ResolveAsync(req.Cookies[AuthOptions.CookieName], req.HttpContext.RequestAborted);
            if (user is null) return Error(401, "Not signed in.");
            return await action(user);
        }
        catch (Exception ex)
        {
            return Handle(ex);
        }
    }

    public async Task<IActionResult> RunAnonymousAsync(HttpRequest req, Func<Task<IActionResult>> action)
    {
        req.HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            return Handle(ex);
        }
    }

    private IActionResult Handle(Exception ex)
    {
        switch (ex)
        {
            case BusinessRuleException rule:
                return Error(rule.Status, rule.Message);
            case JsonException or BadHttpRequestException or InvalidDataException:
                return Error(400, "The request could not be read.");
            case OperationCanceledException:
                return Error(499, "The request was cancelled.");
            default:
                // Type only: an exception message could contain data from a PDF.
                log.LogError("Unhandled {ExceptionType} at {Site}", ex.GetType().FullName, ex.TargetSite?.Name);
                return Error(500, "Something went wrong on the server. Please try again.");
        }
    }

    public static async Task<T> ReadJsonAsync<T>(HttpRequest req) where T : class =>
        await JsonSerializer.DeserializeAsync<T>(req.Body, Json, req.HttpContext.RequestAborted)
        ?? throw BusinessRuleException.BadRequest("The request body is empty.");
}
