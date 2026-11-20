using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NE.ChatAI;
using NE.ChatAI.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebStartupBuilder.Configure<ChatAIWebStartup, ChatAIStartup>(builder.Services);

WebApplication app = builder.Build();

// Must precede the framework's catch-all route so static assets are not served as HTML pages.
app.UseStaticFiles();
app.UseRouting();

app.Use((http, next) =>
{
    var cookieName = http.Request.IsHttps ? "__Host-" + UserSessionResolver.CookieName : UserSessionResolver.CookieName;
    var secret = http.RequestServices.GetRequiredService<UserSessionResolver>().SecretFor(http.User);

    if (!http.Request.Cookies.TryGetValue(cookieName, out var current) || !string.Equals(current, secret, StringComparison.Ordinal))
    {
        http.Response.Cookies.Append(cookieName, secret, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            Path = "/"
        });
    }

    return next();
});

await app.MapStandardUIWebAsync().ConfigureAwait(false);

await app.RunAsync().ConfigureAwait(false);
