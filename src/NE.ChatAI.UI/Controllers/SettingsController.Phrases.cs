using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Settings;

namespace NE.ChatAI.Controllers;

/// <summary>One of the reader's quick phrases, a field of its own on the settings page.</summary>
internal sealed partial class PhraseItem : RecursiveObservable, IBindableItem
{
    /// <summary>The row's own key while the page is open; phrases are kept as a list, not by id.</summary>
    [RecursiveMember(false)]
    public string Id { get; } = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);

    [RecursiveMember]
    public partial string Text { get; set; } = string.Empty;
}

/// <summary>The quick phrases: a field each with its remove, then a new one; the chat's extras send each with one press.</summary>
internal sealed partial class SettingsController
{
    [RecursiveMember(false)]
    public RecursiveCollection<PhraseItem> Phrases { get; } = [];

    private void ShowPhrases(AppSettings settings)
    {
        Phrases.Clear();

        foreach (var phrase in settings.Phrases)
            Phrases.Add(new PhraseItem { Text = phrase });
    }

    /// <summary>A new phrase to write; it is kept once it has words.</summary>
    [UICommand]
    public void AddPhrase()
        => Phrases.Add(new PhraseItem());

    [UICommand]
    public async Task RemovePhraseAsync(string phraseId, CancellationToken cancellationToken)
    {
        if (!Phrases.Remove(phraseId))
            return;

        await SavePhrasesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Keeps the phrases as one lets go of its value; one left empty is no phrase.</summary>
    [UICommand]
    public async Task SavePhrasesAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        await Data.WriteSettingsAsync(settings with { Phrases = [.. Phrases.Select(static phrase => phrase.Text).Where(static text => !string.IsNullOrWhiteSpace(text))] }, cancellationToken).ConfigureAwait(false);
    }
}
