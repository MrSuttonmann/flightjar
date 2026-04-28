using System.Text.Json;
using FlightJar.Api.Auth;

namespace FlightJar.Api.Endpoints;

internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // {required} tells the UI whether to show a lock indicator at all.
        // {unlocked} is informational only — the server never trusts it for
        // access control, the gating filter re-checks the cookie on every
        // request. Always 200 so the UI can read it on first paint.
        app.MapGet("/api/auth/status", (HttpContext ctx, AuthService auth) =>
        {
            var cookie = ctx.Request.Cookies[AuthService.CookieName];
            var unlocked = auth.Required && auth.ValidateSession(cookie);
            return Results.Json(new { required = auth.Required, unlocked });
        });

        app.MapPost("/api/auth/login", async (HttpContext ctx, AuthService auth) =>
        {
            if (!auth.Required)
            {
                return Results.NotFound(new { error = "auth not configured" });
            }
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
            if (!auth.TryRecordLoginAttempt(clientIp))
            {
                ctx.Response.Headers["Retry-After"] = "60";
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }
            string? candidate = null;
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("password", out var pw)
                    && pw.ValueKind == JsonValueKind.String)
                {
                    candidate = pw.GetString();
                }
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "expected JSON {password: string}" });
            }
            if (!auth.VerifyPassword(candidate))
            {
                // Don't log the candidate. Log the IP so abuse leaves a trail.
                ctx.RequestServices.GetService<ILogger<Program>>()
                    ?.LogWarning("auth: bad password from {Ip}", clientIp);
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }
            auth.ResetRateLimit(clientIp);
            var token = auth.MintSession();
            AuthService.SetSessionCookie(ctx, token, AuthService.SessionLifetime);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/auth/logout", (HttpContext ctx, AuthService auth) =>
        {
            var cookie = ctx.Request.Cookies[AuthService.CookieName];
            auth.InvalidateSession(cookie);
            AuthService.ClearSessionCookie(ctx);
            return Results.Ok(new { ok = true });
        });

        return app;
    }
}
