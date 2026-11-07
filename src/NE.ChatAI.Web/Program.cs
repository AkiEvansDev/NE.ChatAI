using Microsoft.AspNetCore.Builder;
using NE.ChatAI;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// The application lives in NE.ChatAI.UI; this host only puts it on the web.
WebStartupBuilder.Configure<ChatAIWebStartup, ChatAIStartup>(builder.Services);

WebApplication app = builder.Build();

// Before the framework's catch-all route, or every static file comes back as a rendered page.
app.UseStaticFiles();
app.UseRouting();

await app.MapStandardUIWebAsync().ConfigureAwait(false);

await app.RunAsync().ConfigureAwait(false);
