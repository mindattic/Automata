using Automata.Core.Operator;
using Microsoft.Extensions.Logging.Abstractions;
using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class ToolCallingLlmTests
{
    /// <summary>
    /// <see cref="AnthropicToolCallingLlm.DefaultResolveApiKey"/> checks the shared credential
    /// store under "claude" — the id every MindAttic app uses. Redirects
    /// MINDATTIC_LLM_CREDENTIALS to a temp dir so no real profile is touched.
    /// </summary>
    [TestFixture]
    public class DefaultResolveApiKeyTests
    {
        private string? previousEnv;
        private string tempDir = null!;

        [SetUp]
        public void SetUp()
        {
            previousEnv = Environment.GetEnvironmentVariable(LlmCredentialStore.DirectoryEnvVar);
            tempDir = Path.Combine(Path.GetTempPath(), "automata-claude-key-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            Environment.SetEnvironmentVariable(LlmCredentialStore.DirectoryEnvVar, tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable(LlmCredentialStore.DirectoryEnvVar, previousEnv);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }

        [Test]
        public void ReturnsSharedClaudeKey_WhenPresent()
        {
            new LlmCredentialStore(tempDir).SetKey("claude", "shared-under-claude");

            Assert.That(AnthropicToolCallingLlm.DefaultResolveApiKey(), Is.EqualTo("shared-under-claude"));
        }

        [Test]
        public void ReturnsNull_WhenNoKeyPresent()
        {
            Assert.That(AnthropicToolCallingLlm.DefaultResolveApiKey(), Is.Null);
        }
    }

    [Test]
    public async Task AnthropicToolCallingLlm_IsConfiguredAsync_FalseWhenResolverReturnsNull()
    {
        var client = new AnthropicToolClient(new HttpClient(), NullLogger<AnthropicToolClient>.Instance);
        var llm = new AnthropicToolCallingLlm(client, () => null);

        Assert.That(await llm.IsConfiguredAsync(), Is.False);
    }

    [Test]
    public async Task AnthropicToolCallingLlm_IsConfiguredAsync_TrueWhenResolverReturnsKey()
    {
        var client = new AnthropicToolClient(new HttpClient(), NullLogger<AnthropicToolClient>.Instance);
        var llm = new AnthropicToolCallingLlm(client, () => "sk-ant-oat-test");

        Assert.That(await llm.IsConfiguredAsync(), Is.True);
    }

    [Test]
    public async Task OpenAiToolCallingLlm_IsConfiguredAsync_FalseWhenResolverReturnsNull()
    {
        var llm = new OpenAiToolCallingLlm(new HttpClient(), NullLogger<OpenAiToolCallingLlm>.Instance, () => null);

        Assert.That(await llm.IsConfiguredAsync(), Is.False);
    }

    [Test]
    public async Task OpenAiToolCallingLlm_IsConfiguredAsync_TrueWhenResolverReturnsKey()
    {
        var llm = new OpenAiToolCallingLlm(new HttpClient(), NullLogger<OpenAiToolCallingLlm>.Instance, () => "sk-test");

        Assert.That(await llm.IsConfiguredAsync(), Is.True);
    }

    [Test]
    public void ToolLoopMessage_AssistantTurn_CarriesTextAndToolCallParts()
    {
        var turn = new ToolLoopMessage.AssistantTurn(new List<AssistantPart>
        {
            new AssistantPart.Text("thinking..."),
            new AssistantPart.ToolCall("id-1", "click_button", "{\"text_candidates\":[\"ok\"]}"),
        });

        Assert.That(turn.Parts, Has.Count.EqualTo(2));
        Assert.That(turn.Parts[0], Is.InstanceOf<AssistantPart.Text>());
        Assert.That(turn.Parts[1], Is.InstanceOf<AssistantPart.ToolCall>());
    }
}
