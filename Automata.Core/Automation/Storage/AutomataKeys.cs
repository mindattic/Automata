using MindAttic.Vault.Credentials;

namespace Automata.Core.Automation.Storage;

/// <summary>
/// Automata's LLM keys under LAMP (AUT-T27). When Automata has keys assigned on its MindAttic Vault tab, those
/// (in the assigned order, skipping disabled and failed ones) win; otherwise each provider keeps its old fallback
/// (for Claude: the OAuth session first, then the shared key). That is the priority Automata had before LAMP,
/// when its own key pool won outright over the shared fallback. Its pre-LAMP own copies ("automata-&lt;provider&gt;")
/// are moved into the shared named list as its assigned keys by <see cref="ForCurrentUser"/>. Read live on every
/// call, so a change in Vault takes effect without a restart.
/// </summary>
public sealed class AutomataKeys
{
    private readonly NamedKeyStore keys;
    private readonly KeySelector selector;

    public AutomataKeys(NamedKeyStore keys, KeyHealthStore? health = null)
    {
        this.keys = keys ?? throw new ArgumentNullException(nameof(keys));
        selector = new KeySelector(new FileKeySource(keys), health);
    }

    /// <summary>The current user's Vault keys; first imports Automata's own pre-LAMP copies (never throws).</summary>
    public static AutomataKeys ForCurrentUser()
    {
        KeyImporter.ImportAppScopedFor(AutomataVaultSettings.AppId);
        return new AutomataKeys(NamedKeyStore.ForRoamingRoot(), KeyHealthStore.ForLocalRoot());
    }

    /// <summary>True when Automata has keys assigned for the provider on its Vault tab.</summary>
    public bool HasAssignment(string providerId)
    {
        try { return keys.Assignments.Get(AutomataVaultSettings.AppId, providerId).Count > 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Automata's usable assigned key values for the provider, in order; empty without an assignment.</summary>
    public IReadOnlyList<string> AssignedKeys(string providerId)
    {
        if (!HasAssignment(providerId)) return [];
        try { return selector.Plan(providerId, AutomataVaultSettings.AppId).Usable.Select(k => k.Value).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The key pool an LLM client fails over through: the assigned keys when there are any usable ones,
    /// else <paramref name="fallback"/> (the provider's pre-LAMP shared chain).</summary>
    public Func<IReadOnlyList<string>> PoolResolver(string providerId, Func<IReadOnlyList<string>> fallback) =>
        () =>
        {
            var assigned = AssignedKeys(providerId);
            return assigned.Count > 0 ? assigned : fallback();
        };
}
