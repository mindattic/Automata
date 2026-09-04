using Automata.Core.Automation.Flow;
using Automata.Core.Operator;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class FlowAuthoringServiceTests
{
    /// <summary>Replies with a scripted sequence of turns and records what it was asked.</summary>
    private sealed class ScriptedLlm(bool configured, params string[] replies) : IToolCallingLlm
    {
        private int turn;
        public string Name => "Scripted";
        public List<string> Prompts { get; } = [];
        public List<IReadOnlyList<ToolLoopMessage>> Histories { get; } = [];
        public Task<bool> IsConfiguredAsync() => Task.FromResult(configured);

        public Task<ToolTurnResult> CreateTurnAsync(
            string systemPrompt, IReadOnlyList<ToolLoopMessage> history,
            IReadOnlyList<ToolDefinition> tools, int maxTokens, CancellationToken ct)
        {
            Prompts.Add(systemPrompt);
            Histories.Add(history.ToList());
            var reply = replies[Math.Min(turn++, replies.Length - 1)];
            return Task.FromResult(new ToolTurnResult([new AssistantPart.Text(reply)]));
        }
    }

    private const string GoodFeature = """
        Feature: Search
          Scenario: Wolf tshirts
            Given I open "https://www.google.com"
            And I type "wolf tshirts" into "Search"
            And I press Enter
            And I click "Images"
        """;

    [Test]
    public async Task DraftsAFeatureAndCompilesItOnTheFirstAttempt()
    {
        var llm = new ScriptedLlm(true, GoodFeature);
        var service = new FlowAuthoringService([llm]);

        var draft = await service.DraftAsync("search google for wolf tshirts and click images", new FlowAuthoringContext());

        Assert.Multiple(() =>
        {
            Assert.That(draft.Result.HasErrors, Is.False, string.Join(" | ", draft.Result.Diagnostics));
            Assert.That(draft.Attempts, Is.EqualTo(1));
            Assert.That(draft.Result.Tasks.Single().Steps, Has.Count.EqualTo(4));
            Assert.That(draft.Result.Tasks.Single().Steps[3].Label, Does.Contain("Images"));
        });
    }

    /// <summary>
    /// The repair loop is the reason for having an intermediate artifact at all: a failure comes
    /// back as a diagnostic with a line number, which the model can act on.
    /// </summary>
    [Test]
    public async Task RepairsAgainstItsOwnDiagnosticsAndSucceedsOnTheSecondAttempt()
    {
        var broken = """
            Feature: Search
              Scenario: S
                Given I open "https://x.example"
                And I frobnicate the widget
            """;
        var llm = new ScriptedLlm(true, broken, GoodFeature);
        var service = new FlowAuthoringService([llm]);

        var draft = await service.DraftAsync("do a search", new FlowAuthoringContext());

        Assert.Multiple(() =>
        {
            Assert.That(draft.Result.HasErrors, Is.False);
            Assert.That(draft.Attempts, Is.EqualTo(2));

            // The second turn must have been given the actual diagnostic, not just "try again".
            var repairTurn = llm.Histories[1].OfType<ToolLoopMessage.UserText>().Last().Text;
            Assert.That(repairTurn, Does.Contain("line 4"));
            Assert.That(repairTurn, Does.Contain("frobnicate"));
        });
    }

    [Test]
    public async Task GivesUpAfterThreeAttemptsAndHandsBackWhatItGot()
    {
        var broken = """
            Feature: Search
              Scenario: S
                Given I frobnicate the widget
            """;
        var llm = new ScriptedLlm(true, broken);
        var service = new FlowAuthoringService([llm]);

        var draft = await service.DraftAsync("do a search", new FlowAuthoringContext());

        Assert.Multiple(() =>
        {
            Assert.That(draft.Attempts, Is.EqualTo(FlowAuthoringService.MaxAttempts));
            Assert.That(draft.Result.HasErrors, Is.True);
            Assert.That(draft.FeatureText, Does.Contain("frobnicate"),
                "the user must still see what the model wrote, so they can fix it themselves");
        });
    }

    [Test]
    public async Task WithNoConfiguredProvider_SaysSoRatherThanThrowing()
    {
        var service = new FlowAuthoringService([new ScriptedLlm(false, GoodFeature)]);

        var draft = await service.DraftAsync("anything", new FlowAuthoringContext());

        Assert.That(draft.Result.HasErrors, Is.True);
        Assert.That(draft.Result.Diagnostics.Single().Message, Does.Contain("Settings"));
    }

    [Test]
    public async Task SkipsAProviderWithNoCredentialsAndUsesTheNext()
    {
        var unusable = new ScriptedLlm(false, GoodFeature);
        var usable = new ScriptedLlm(true, GoodFeature);
        var service = new FlowAuthoringService([unusable, usable]);

        var draft = await service.DraftAsync("go", new FlowAuthoringContext());

        Assert.That(draft.Result.HasErrors, Is.False);
        Assert.That(unusable.Prompts, Is.Empty);
        Assert.That(usable.Prompts, Is.Not.Empty);
    }

    /// <summary>The prompt is generated from the catalog, so the model cannot be told a form the
    /// compiler would reject.</summary>
    [Test]
    public async Task ThePromptCarriesTheCatalogAndTheLiveContext()
    {
        var llm = new ScriptedLlm(true, GoodFeature);
        var service = new FlowAuthoringService([llm]);

        await service.DraftAsync("go", new FlowAuthoringContext
        {
            DatasetNames = ["skus.csv"],
            TaskNames = ["Send report"],
            CurrentUrl = "https://shop.example",
        });

        var prompt = llm.Prompts.Single();
        Assert.Multiple(() =>
        {
            Assert.That(prompt, Does.Contain("I extract text from"), "the vocabulary must be in the prompt");
            Assert.That(prompt, Does.Contain("skus.csv"));
            Assert.That(prompt, Does.Contain("Send report"));
            Assert.That(prompt, Does.Contain("https://shop.example"));
        });
    }

    [TestCase("```gherkin\nFeature: F\n```", "Feature: F")]
    [TestCase("```\nFeature: F\n```", "Feature: F")]
    [TestCase("Feature: F", "Feature: F")]
    [TestCase("  Feature: F  ", "Feature: F")]
    public void StripsCodeFencesModelsAddAnyway(string reply, string expected)
    {
        Assert.That(FlowAuthoringService.StripFences(reply), Is.EqualTo(expected));
    }

    [Test]
    public async Task AFencedReplyStillCompiles()
    {
        var llm = new ScriptedLlm(true, "```gherkin\n" + GoodFeature + "\n```");
        var service = new FlowAuthoringService([llm]);

        var draft = await service.DraftAsync("go", new FlowAuthoringContext());

        Assert.That(draft.Result.HasErrors, Is.False, string.Join(" | ", draft.Result.Diagnostics));
    }
}
