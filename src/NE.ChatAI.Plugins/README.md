# NE.ChatAI.Plugins

Scenario plugin SDK for [`NE.ChatAI`](https://github.com/AkiEvansDev/NE.ChatAI), built on [`NE.Standard.UI.Graph`](https://github.com/AkiEvansDev/NE.Standard).

## Overview

Reference `NE.ChatAI.Plugins` with `ExcludeAssets="runtime"` so host contracts unify with the running `NE.ChatAI` process when loaded inside its collectible `PluginLoadContext`:

```xml
<PropertyGroup>
  <TargetFramework>net11.0</TargetFramework>
  <EnableDynamicLoading>true</EnableDynamicLoading>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="NE.ChatAI.Plugins" Version="1.5.0" ExcludeAssets="runtime" PrivateAssets="all" />
  <None Include="plugin.json" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
</ItemGroup>
```

## Key Types

- `IChatPlugin` — optional explicit entry point (`Id` and `NodeTypes`); when omitted, public concrete `[GraphNode]` classes in the entry assembly are discovered automatically.
- `IScenarioContext` — read-only metadata for the active scenario run (`ScenarioId`, `ScenarioName`, `ChatId`, `CharacterId`, `CharacterName`, `UserName`, `IsPreview`, `Draft`).
- `IScenarioSecrets` — reads named API keys encrypted in `secrets.json` or supplied via `ChatAI__Secrets__<name>`.
- `IScenarioMedia` — saves generated or downloaded images into `data/chats/<chatId>/images/` and returns a served URL ready for `[GraphOutput(Image = true)]` and `Set visual` (`chat.visual`).
- `ScenarioNodeExtensions` — `UINodeRunContext` helpers (`GetScenarioContext()`, `GetScenarioSecrets()`, `GetScenarioMedia()`, `CreateHttpClient()`).

See [`AkiEvansDev/NE.ChatAI.Plugins.Translate`](https://github.com/AkiEvansDev/NE.ChatAI.Plugins.Translate) for a complete standalone plugin repository and GitHub Releases workflow.
