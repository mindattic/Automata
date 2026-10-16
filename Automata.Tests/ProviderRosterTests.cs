using Automata.Core.Operator;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class ProviderRosterTests
{
    private sealed class StubLlm(string name) : IToolCallingLlm
    {
        public string Name => name;
        public Task<bool> IsConfiguredAsync() => Task.FromResult(true);
        public Task<ToolTurnResult> CreateTurnAsync(string s, IReadOnlyList<ToolLoopMessage> h,
            IReadOnlyList<ToolDefinition> t, int m, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static ProviderRoster Roster(Func<string?> selected) => new(
    [
        ("claude", new StubLlm("Claude")),
        ("openai", new StubLlm("OpenAI")),
        ("gemini", new StubLlm("Gemini")),
        ("kimi", new StubLlm("Kimi")),
    ], selected);

    [Test]
    public void SelectedProvider_EnumeratesFirst_RestKeepRegistrationOrder()
    {
        var roster = Roster(() => "gemini");

        Assert.That(roster.Select(l => l.Name),
            Is.EqualTo(new[] { "Gemini", "Claude", "OpenAI", "Kimi" }));
        Assert.That(roster.Count, Is.EqualTo(4));
        Assert.That(roster[0].Name, Is.EqualTo("Gemini"));
    }

    [Test]
    public void ChangingSelection_ReordersTheNextEnumeration_WithoutRebuildingTheRoster()
    {
        var selected = "claude";
        var roster = Roster(() => selected);

        Assert.That(roster.First().Name, Is.EqualTo("Claude"));
        selected = "kimi";
        Assert.That(roster.First().Name, Is.EqualTo("Kimi")); // live re-order, same instance
    }

    [Test]
    public void UnknownOrNullSelection_FallsBackToRegistrationOrder()
    {
        Assert.That(Roster(() => null).First().Name, Is.EqualTo("Claude"));
        Assert.That(Roster(() => "nonsense").First().Name, Is.EqualTo("Claude"));
        Assert.That(Roster(() => " GEMINI ").First().Name, Is.EqualTo("Gemini")); // trimmed, case-insensitive
    }
}

[TestFixture]
public class OpenAiCompatibleProviderTests
{
    [Test]
    public void KimiInstance_CarriesItsOwnName()
    {
        using var http = new HttpClient();
        var log = Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiToolCallingLlm>.Instance;

        var kimi = new OpenAiToolCallingLlm(http, log, () => "sk-kimi", model: "kimi-latest",
            name: "Kimi", endpoint: "https://api.moonshot.ai/v1/chat/completions");

        Assert.That(kimi.Name, Is.EqualTo("Kimi"));
        Assert.That(new OpenAiToolCallingLlm(http, log, () => "sk").Name, Is.EqualTo("OpenAI"));
    }
}
