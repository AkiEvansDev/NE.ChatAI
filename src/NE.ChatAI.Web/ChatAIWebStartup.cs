using System;
using Microsoft.Extensions.DependencyInjection;
using NE.ChatAI.Hosting;
using NE.ChatAI.Views;

namespace NE.ChatAI;

internal sealed class ChatAIWebStartup : WebStartupBase<ChatAIStartup>
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Raises the SignalR input/sheet value limit above the 4 MB default for large node graphs and lore cards.
        _ = services.Configure<WebValueOptions>(options => options.MaxValueSize = PictureFields.MaxBackgroundBytes);
        _ = services.AddSingleton<PinCookieBridge>();
        _ = services.AddSingleton<IPinCookieBridge>(static provider => provider.GetRequiredService<PinCookieBridge>());
        _ = services.AddStandardRenderers();
        _ = services.AddCodeInput();
        _ = services.AddCharts();
        _ = services.AddGraph();
        _ = services.AddMaterialWebIcons(MaterialIconStyle.Fill | MaterialIconStyle.Outlined, AppIcons.All());
    }
}
