using Microsoft.AspNetCore.Http;

namespace FlightJar.Api.Auth;

/// <summary>
/// Endpoint filter that gates a route on the optional shared-secret
/// session cookie. When <see cref="AuthService.Required"/> is false (no
/// <c>FLIGHTJAR_PASSWORD</c> configured) the filter is a no-op so the
/// default install behaves exactly as before. When required, missing or
/// invalid sessions get a 401 with no response body — the frontend
/// pattern-matches on the status code and prompts the user to unlock.
/// </summary>
public static class RequireAuth
{
    public static RouteHandlerBuilder RequireAuthSession(this RouteHandlerBuilder builder)
    {
        return builder.AddEndpointFilter(async (ctx, next) =>
        {
            var auth = ctx.HttpContext.RequestServices.GetRequiredService<AuthService>();
            if (!auth.Required)
            {
                return await next(ctx);
            }
            var cookie = ctx.HttpContext.Request.Cookies[AuthService.CookieName];
            if (!auth.ValidateSession(cookie))
            {
                ctx.HttpContext.Response.Headers["WWW-Authenticate"] = "FlightjarSession";
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }
            return await next(ctx);
        });
    }
}
