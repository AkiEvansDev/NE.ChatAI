using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Characters;

/// <summary>Which world a chat is played in, and what of it goes into the prompt.</summary>
public static class WorldInfo
{
    /// <summary>The character's world, else the persona's; none when neither names one the data folder holds.</summary>
    public static async Task<World?> OfAsync(DataFolder data, Card? character, Card? persona, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        foreach (var id in (string?[])[character?.World, persona?.World])
        {
            if (id is not null && Slug.IsValidId(id) && await data.Worlds.ReadAsync(id, cancellationToken).ConfigureAwait(false) is World world)
                return world;
        }

        return null;
    }

    /// <summary>What a world puts into the prompt: its overview after the story, then each article that is on.</summary>
    public static IReadOnlyList<WorldArticle> ArticlesOf(World? world)
    {
        if (world is null)
            return [];

        List<WorldArticle> articles = new(world.Articles.Count + 1);

        if (!string.IsNullOrWhiteSpace(world.Description))
            articles.Add(new WorldArticle { Title = world.Name, Text = world.Description });

        foreach (WorldArticle article in world.Articles)
        {
            if (article.Enabled && !string.IsNullOrWhiteSpace(article.Text))
                articles.Add(article);
        }

        return articles;
    }
}
