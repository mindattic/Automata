using System.Collections;

namespace Automata.Core.Operator;

/// <summary>
/// The provider list <see cref="BrowserOperatorService"/> iterates, ordered LIVE on every
/// enumeration: the user's selected provider first, the rest behind it as fallbacks (the loop
/// takes the first provider whose credentials resolve). Reading the selection through a delegate
/// on each pass means switching provider in Settings takes effect on the very next run — no
/// restart, no singleton staleness.
/// </summary>
public sealed class ProviderRoster : IReadOnlyList<IToolCallingLlm>
{
    private readonly IReadOnlyList<(string Key, IToolCallingLlm Llm)> providers;
    private readonly Func<string?> selectedKey;

    public ProviderRoster(IReadOnlyList<(string Key, IToolCallingLlm Llm)> providers, Func<string?> selectedKey)
    {
        this.providers = providers;
        this.selectedKey = selectedKey;
    }

    private List<IToolCallingLlm> Ordered()
    {
        var key = (selectedKey() ?? "").Trim().ToLowerInvariant();
        var ordered = new List<IToolCallingLlm>(providers.Count);
        foreach (var (k, llm) in providers) if (k == key) ordered.Add(llm);
        foreach (var (k, llm) in providers) if (k != key) ordered.Add(llm);
        return ordered;
    }

    public int Count => providers.Count;
    public IToolCallingLlm this[int index] => Ordered()[index];
    public IEnumerator<IToolCallingLlm> GetEnumerator() => Ordered().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
