using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NE.ChatAI;
using NE.ChatAI.Hosting;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebStartupBuilder.Configure<ChatAIWebStartup, ChatAIStartup>(builder.Services);

WebApplication app = builder.Build();

// Must precede the framework's catch-all route so static assets are not served as HTML pages.
app.UseStaticFiles();
app.UseRouting();

app.Use(async (http, next) =>
{
    DataFolder data = http.RequestServices.GetRequiredService<DataFolder>();
    AppSettings settings = await data.ReadSettingsAsync(http.RequestAborted).ConfigureAwait(false);

    if (string.IsNullOrWhiteSpace(settings.PinHash))
    {
        var cookieName = CookieNameOf(http);
        var secret = http.RequestServices.GetRequiredService<UserSessionResolver>().SecretFor(http.User);

        if (!http.Request.Cookies.TryGetValue(cookieName, out var current) || !string.Equals(current, secret, StringComparison.Ordinal))
            AppendSessionCookie(http, cookieName, secret);
    }

    await next().ConfigureAwait(false);
});

app.MapGet(PinCookieBridge.EndpointPath, (HttpContext http, string? ticket, PinCookieBridge bridge) =>
{
    if (bridge.TryConsume(ticket) is not (var secret, var returnUrl))
        return Results.LocalRedirect(AppRoutes.Pin);

    AppendSessionCookie(http, CookieNameOf(http), secret);

    return Results.LocalRedirect(returnUrl);
});

await app.MapStandardUIWebAsync().ConfigureAwait(false);

await app.RunAsync().ConfigureAwait(false);

static string CookieNameOf(HttpContext http)
{
    return http.Request.IsHttps ? "__Host-" + UserSessionResolver.CookieName : UserSessionResolver.CookieName;
}

static void AppendSessionCookie(HttpContext http, string cookieName, string secret)
{
    http.Response.Cookies.Append(cookieName, secret, new CookieOptions
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Secure = http.Request.IsHttps,
        MaxAge = UserSessionResolver.CookieLifetime,
        Path = "/"
    });
}
