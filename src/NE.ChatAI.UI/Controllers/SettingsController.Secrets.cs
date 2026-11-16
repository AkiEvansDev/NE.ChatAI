using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NE.ChatAI.Secrets;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>One secret entry shown on the settings page — its slug name and whether it is stored in <c>secrets.json</c>, never its value.</summary>
internal sealed partial class SecretItem : RecursiveObservable, IBindableItem
{
    [RecursiveMember(false)]
    public required string Id { get; init; }

    [RecursiveMember]
    public partial string Name { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIVisibility DeleteVisibility { get; set; } = UIVisibility.Visible;
}

/// <summary>The encrypted <c>secrets.json</c> API keys tab: creating or updating a named secret via dialog and removing stored entries.</summary>
internal sealed partial class SettingsController
{
    public const string SecretSearchId = "chatai-secrets-search";
    public const string SecretDialogKey = "chatai-settings-secret";

    private SecretStore SecretsStore => Context.Services.GetRequiredService<SecretStore>();

    [RecursiveMember]
    public partial UIPhrase? SecretDialogTitle { get; set; }

    [RecursiveMember]
    public partial string SecretName { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string SecretValue { get; set; } = string.Empty;

    [RecursiveMember]
    public partial bool SecretNameReadOnly { get; set; }

    [RecursiveMember(false)]
    public RecursiveCollection<SecretItem> Secrets { get; } = [];

    private async Task LoadSecretsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SecretInfo> items = await SecretsStore.ListAsync(cancellationToken).ConfigureAwait(false);
        Secrets.Clear();

        for (var i = 0; i < items.Count; i++)
        {
            SecretInfo info = items[i];
            Secrets.Add(new SecretItem
            {
                Id = info.Name,
                Name = info.Name,
                DeleteVisibility = info.Stored ? UIVisibility.Visible : UIVisibility.Collapsed
            });
        }
    }

    /// <summary>Opens the dialog to create a new named secret.</summary>
    [UICommand]
    public UICommandResult AskCreateSecret()
    {
        SecretDialogTitle = new UIPhrase("chatai.secrets.new.title");
        SecretName = string.Empty;
        SecretValue = string.Empty;
        SecretNameReadOnly = false;

        return UICommandResult.Ok([new OpenDialogEffect(SecretDialogKey)]);
    }

    /// <summary>Opens the dialog to replace the value of <paramref name="secretId"/>.</summary>
    [UICommand]
    public UICommandResult AskEditSecret(string secretId)
    {
        if (!Slug.IsValidId(secretId))
            return UICommandResult.Ok();

        SecretDialogTitle = new UIPhrase("chatai.secrets.edit.title");
        SecretName = secretId;
        SecretValue = string.Empty;
        SecretNameReadOnly = true;

        return UICommandResult.Ok([new OpenDialogEffect(SecretDialogKey)]);
    }

    /// <summary>Encrypts and saves a secret under a normalized slug, closing the dialog and clearing the password input right away.</summary>
    [UICommand]
    public async Task<UICommandResult> SaveSecretAsync(CancellationToken cancellationToken)
    {
        var slug = Slug.Of(SecretName);

        if (slug.Length == 0)
            return Notify("chatai.secrets.invalid-name", UIColorStyle.Warning);

        if (string.IsNullOrWhiteSpace(SecretValue))
            return Notify("chatai.secrets.empty-value", UIColorStyle.Warning);

        var value = SecretValue;
        SecretName = string.Empty;
        SecretValue = string.Empty;
        SecretNameReadOnly = false;

        await SecretsStore.SetAsync(slug, value, cancellationToken).ConfigureAwait(false);
        await LoadSecretsAsync(cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok([
            new CloseDialogEffect(SecretDialogKey),
            new ShowNotificationEffect(UIPhrase.Of("chatai.secrets.saved", ("name", slug)), UIColorStyle.Success)
        ]);
    }

    /// <summary>Removes a stored secret from <c>secrets.json</c>.</summary>
    [UICommand]
    public async Task DeleteSecretAsync(string secretId, CancellationToken cancellationToken)
    {
        if (!Slug.IsValidId(secretId))
            return;

        _ = await SecretsStore.RemoveAsync(secretId, cancellationToken).ConfigureAwait(false);
        await LoadSecretsAsync(cancellationToken).ConfigureAwait(false);
    }
}
