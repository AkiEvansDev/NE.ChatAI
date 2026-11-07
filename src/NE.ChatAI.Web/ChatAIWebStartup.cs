using System;
using Microsoft.Extensions.DependencyInjection;
using NE.ChatAI.Views;

namespace NE.ChatAI;

/// <summary>The web half: the renderers, the icons the pages draw and the macros' highlighting in the code input.</summary>
internal sealed class ChatAIWebStartup : WebStartupBase<ChatAIStartup>
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // As large as the largest picture a field takes, not the framework's 4 MB.
        _ = services.Configure<WebValueOptions>(options => options.MaxValueSize = PictureFields.MaxBackgroundBytes);
        _ = services.AddStandardRenderers();
        _ = services.AddCodeInput();
        _ = services.AddCharts();
        _ = services.AddGraph();
        _ = services.AddMaterialWebIcons(MaterialIconStyle.Fill | MaterialIconStyle.Outlined, AppIcons.All());
        _ = services.AddSingleton(new WebAssetDescriptor
        {
            Key = "chatai-macros.js",
            Kind = UIWebAssetKind.JavaScript,
            SourceKind = UIWebAssetSourceKind.EmbeddedResource,
            Source = "NE.ChatAI.Web.Client.macros.js",
            ResourceAssemblyName = "NE.ChatAI.Web",
            PublicPath = "/chatai/js/macros.js"
        });
    }
}
