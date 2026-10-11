using HousekeepingParser.Accounts;
using HousekeepingParser.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Options;

namespace HousekeepingParser.Functions;

public sealed class AuthFunctions(Api api, IAuthService auth, IOptions<AuthOptions> options)
{
    [Function("AuthLogin")]
    public Task<IActionResult> Login([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/login")] HttpRequest req) =>
        api.RunAnonymousAsync(req, async () =>
        {
            var body = await Api.ReadJsonAsync<LoginRequest>(req);
            if (string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrEmpty(body.Password))
                throw BusinessRuleException.BadRequest("Please enter your username and password.");

            var (user, token, expires) = await auth.LoginAsync(body.Username, body.Password, req.HttpContext.RequestAborted);
            // httpOnly cookie, never a token in browser storage (plan 5.3).
            req.HttpContext.Response.Cookies.Append(AuthOptions.CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                Secure = options.Value.SecureCookie,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                Expires = expires,
            });
            return Api.Ok(ToDto(user));
        });

    [Function("AuthLogout")]
    public Task<IActionResult> Logout([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/logout")] HttpRequest req) =>
        api.RunAnonymousAsync(req, async () =>
        {
            await auth.LogoutAsync(req.Cookies[AuthOptions.CookieName], req.HttpContext.RequestAborted);
            req.HttpContext.Response.Cookies.Delete(AuthOptions.CookieName, new CookieOptions { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Strict });
            return new NoContentResult();
        });

    [Function("AuthMe")]
    public Task<IActionResult> Me([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/me")] HttpRequest req) =>
        api.RunAsync(req, user => Task.FromResult(Api.Ok(ToDto(user.User))));

    private static UserDto ToDto(AppUser u) => new(u.Username, u.DisplayName, u.Role.ToString());
}
