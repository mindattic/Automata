using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Automata.Core.Automation.Data;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Settings;
using Automata.Core.Automation.Storage;
using Automata.Core.Extensions;
using Automata.Core.Operator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Launch;
using MindAttic.Vault.Paths;
using MindAttic.Vault.Settings;
using MindAttic.Vault.Settings.Manifest;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>Every test in this assembly sees temp Vault roots, so nothing can read or write the real AppData
/// (M.VALT-LAW-6) — including code that resolves keys or settings through <see cref="VaultPaths"/>.</summary>
[SetUpFixture]
public sealed class TestVaultRoots
{
    private string root = "";

    [OneTimeSetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "AutomataVault-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, Path.Combine(root, "roaming"));
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, Path.Combine(root, "local"));
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, null);
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, null);
        try { Directory.Delete(root, recursive: true); } catch { /* scratch */ }
    }
}

/// <summary>
/// LAMP (AUT-T27): Automata's app-level settings live in MindAttic Vault, imported once from the database row,
/// overlaid on it on every load and applied live. One test per setting (each engine-default leaf is its own
/// case) proving: today's stored value imports into the right Vault name, it round-trips through the Vault file,
/// and the code that consumes it gets it.
/// </summary>
[TestFixture, NonParallelizable]
public class LampSettingsTests
{
    private TestDb db = null!;
    private string vaultDir = "";

    private JsonSettingsStore<AutomataVaultSettings> Vault() => new(vaultDir);
    private AutomataSettingsStore Store() => new(db.Database, Vault());

    [SetUp]
    public void SetUp()
    {
        db = new TestDb();
        vaultDir = Path.Combine(Path.GetTempPath(), "automata-lamp-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        db.Dispose();
        try { Directory.Delete(vaultDir, recursive: true); } catch { /* scratch */ }
    }

    /// <summary>Today's settings: the single database row the pre-LAMP app wrote (no Vault file yet).</summary>
    private void TodaysRow(Action<AutomataSettings> set)
    {
        var row = new AutomataSettings();
        set(row);
        using (var ctx = db.Database.CreateDbContext())
        {
            ctx.Entry(row).Property(Automata.Core.Automation.Data.AutomataDb.SettingsKey).CurrentValue = 1;
            ctx.Settings.Add(row);
            ctx.SaveChanges();
        }
        Assert.That(Vault().Exists(), Is.False, "precondition: not migrated yet");
    }

    private AutomataSettings EditInVault(Action<AutomataVaultSettings> edit)
    {
        Vault().Update(edit);
        return Store().Load();
    }

    /// <summary>The value under a manifest name in the Vault file, e.g. <c>engineDefaults:retry:delayMs</c>.</summary>
    private JsonNode? InVaultFile(string path)
    {
        JsonNode? node = JsonNode.Parse(File.ReadAllText(Vault().FilePath));
        foreach (var part in path.Split(':')) node = node?[part];
        return node;
    }

    private static string FullName(string path) => "MindAttic:Settings:Automata:" + path;

    private static IEnumerable<string> ManifestLeaves(IEnumerable<SettingField> fields) =>
        fields.SelectMany(f => f.Kind == SettingKind.Object && f.Fields is { Count: > 0 } ? ManifestLeaves(f.Fields) : [f.Name]);

    // ---- one test per top-level setting -----------------------------------------------------------

    [Test]
    public void Setting_Provider_ImportsRoundTripsAndIsTheProviderTheRosterAndRunsUseFirst()
    {
        TodaysRow(r => r.Provider = "gemini");
        Assert.That(Store().Load().Provider, Is.EqualTo("gemini"), "imported from the database");
        Assert.That(InVaultFile("provider")?.GetValue<string>(), Is.EqualTo("gemini"));

        var s = EditInVault(v => v.Provider = "kimi");
        Assert.That(s.Provider, Is.EqualTo("kimi"));
        var store = Store();
        var roster = new ProviderRoster([("claude", new NamedLlm("Claude")), ("kimi", new NamedLlm("Kimi"))], () => store.Load().Provider);
        Assert.That(roster[0].Name, Is.EqualTo("Kimi"), "ProviderRoster reads the provider live");
        Assert.That(EngineSettingsResolver.Resolve(store.Load()).LlmProvider, Is.EqualTo("kimi"), "runs resolve it too");
        Assert.That(EditInVault(v => v.Provider = "nonsense").Provider, Is.EqualTo("claude"), "an unknown provider falls back to the default");
    }

    [Test]
    public void Setting_Theme_ImportsRoundTripsAndIsCoerced()
    {
        TodaysRow(r => r.Theme = AutomataSettings.Themes.Light);
        Assert.That(Store().Load().Theme, Is.EqualTo("light"));
        Assert.That(InVaultFile("theme")?.GetValue<string>(), Is.EqualTo("light"));
        Assert.That(EditInVault(v => v.Theme = "dark").Theme, Is.EqualTo("dark"), "the panel and the window read Load().Theme");
        Assert.That(EditInVault(v => v.Theme = "neon").Theme, Is.EqualTo("dark"), "Themes.Coerce, as before");
    }

    [Test]
    public void Setting_BorderRadius_ImportsRoundTripsAndIsClamped()
    {
        TodaysRow(r => r.BorderRadius = 8);
        Assert.That(Store().Load().BorderRadius, Is.EqualTo(8));
        Assert.That(InVaultFile("borderRadius")?.GetValue<int>(), Is.EqualTo(8));
        Assert.That(EditInVault(v => v.BorderRadius = 3).BorderRadius, Is.EqualTo(3), "pushed to the panel as --radius");
        Assert.That(EditInVault(v => v.BorderRadius = 99).BorderRadius, Is.EqualTo(10), "same 0..10 bound the old slider had");
    }

    // ---- one case per engine-default leaf ---------------------------------------------------------

    public sealed record EngineLeaf(
        string Path,
        Action<EngineSettingsOverride> SetRow,
        object RowValue,
        Action<AutomataEngineDefaults> SetVault,
        object VaultValue,
        Func<ResolvedSettings, object> Read)
    {
        public override string ToString() => Path;
    }

    private static RetryPolicy Retry(EngineSettingsOverride o) => o.Retry ??= new RetryPolicy();
    private static AutomataRetryDefaults Retry(AutomataEngineDefaults o) => o.Retry ??= new AutomataRetryDefaults();

    public static IEnumerable<TestCaseData> EngineLeaves()
    {
        EngineLeaf[] leaves =
        [
            new("engineDefaults:defaultStepTimeoutMs", o => o.DefaultStepTimeoutMs = 15000, 15000,
                v => v.DefaultStepTimeoutMs = 2500, 2500, r => r.DefaultStepTimeoutMs),
            new("engineDefaults:selfHeal", o => o.SelfHeal = false, false, v => v.SelfHeal = true, true, r => r.SelfHeal),
            new("engineDefaults:allowLlmRepair", o => o.AllowLlmRepair = true, true, v => v.AllowLlmRepair = false, false, r => r.AllowLlmRepair),
            new("engineDefaults:retry:maxAttempts", o => Retry(o).MaxAttempts = 3, 3, v => Retry(v).MaxAttempts = 5, 5, r => r.Retry.MaxAttempts),
            new("engineDefaults:retry:delayMs", o => Retry(o).DelayMs = 500, 500, v => Retry(v).DelayMs = 750, 750, r => r.Retry.DelayMs),
            new("engineDefaults:retry:backoffMultiplier", o => Retry(o).BackoffMultiplier = 2.0, 2.0,
                v => Retry(v).BackoffMultiplier = 1.5, 1.5, r => r.Retry.BackoffMultiplier),
            new("engineDefaults:continueOnStepError", o => o.ContinueOnStepError = true, true,
                v => v.ContinueOnStepError = false, false, r => r.ContinueOnStepError),
            new("engineDefaults:continueOnTaskError", o => o.ContinueOnTaskError = false, false,
                v => v.ContinueOnTaskError = true, true, r => r.ContinueOnTaskError),
            new("engineDefaults:browserProfile", o => o.BrowserProfile = "work", "work", v => v.BrowserProfile = "home", "home", r => r.BrowserProfile),
            new("engineDefaults:screenshotOnFailure", o => o.ScreenshotOnFailure = true, true,
                v => v.ScreenshotOnFailure = false, false, r => r.ScreenshotOnFailure),
            new("engineDefaults:llmProvider", o => o.LlmProvider = "openai", "openai", v => v.LlmProvider = "gemini", "gemini", r => r.LlmProvider),
        ];
        return leaves.Select(l => new TestCaseData(l).SetName("Setting_EngineDefaults_" + l.Path.Replace("engineDefaults:", "").Replace(':', '_')));
    }

    [TestCaseSource(nameof(EngineLeaves))]
    public void Setting_EngineDefaults_Leaf_ImportsRoundTripsAndIsWhatRunsResolve(EngineLeaf leaf)
    {
        TodaysRow(r => { r.EngineDefaults = new EngineSettingsOverride(); leaf.SetRow(r.EngineDefaults); });

        // Import: the row's value is what runs get, and it now sits under its manifest name in the Vault file.
        var store = Store();
        Assert.That(leaf.Read(EngineSettingsResolver.Resolve(store.Load())), Is.EqualTo(leaf.RowValue), "imported from the database");
        Assert.That(InVaultFile(leaf.Path)?.ToJsonString(), Is.EqualTo(JsonValue.Create(leaf.RowValue)!.ToJsonString()));
        Assert.That(ManifestLeaves(SettingsManifestBuilder.Build<AutomataVaultSettings>().Fields), Does.Contain(FullName(leaf.Path)));

        // Round trip: a save in Vault is what the controller, the runner and the collection runs resolve
        // (EngineSettingsResolver.Resolve(settingsStore.Load(), collection, task, step)).
        var edited = EditInVault(v => { v.EngineDefaults = new AutomataEngineDefaults(); leaf.SetVault(v.EngineDefaults); });
        Assert.That(leaf.Read(EngineSettingsResolver.Resolve(edited)), Is.EqualTo(leaf.VaultValue));
        Assert.That(InVaultFile(leaf.Path)?.ToJsonString(), Is.EqualTo(JsonValue.Create(leaf.VaultValue)!.ToJsonString()));

        // Still the outermost scope: a collection override beats it.
        var collection = new EngineSettingsOverride();
        leaf.SetRow(collection);
        Assert.That(leaf.Read(EngineSettingsResolver.Resolve(edited, collection)), Is.EqualTo(leaf.RowValue));
    }

    [Test]
    public void EverySettingHasItsOwnTest()
    {
        var methods = GetType().GetMethods().Select(m => m.Name).ToList();
        var cases = EngineLeaves().Select(c => c.TestName!).ToList();
        foreach (var name in ManifestLeaves(SettingsManifestBuilder.Build<AutomataVaultSettings>().Fields))
        {
            var path = name["MindAttic:Settings:Automata:".Length..];
            if (path.StartsWith("engineDefaults:"))
            {
                var expected = "Setting_EngineDefaults_" + path["engineDefaults:".Length..].Replace(':', '_');
                Assert.That(cases, Does.Contain(expected), name);
            }
            else
            {
                var prefix = "Setting_" + char.ToUpperInvariant(path[0]) + path[1..] + "_";
                Assert.That(methods.Any(m => m.StartsWith(prefix)), Is.True, name);
            }
        }
        Assert.That(cases, Has.Count.EqualTo(11), "every engine-default leaf");
    }

    // ---- manifest, defaults, storage --------------------------------------------------------------

    [Test]
    public void Manifest_EveryFieldLabeledAndGrouped_NoSecrets_DeclaresTheFourLlmKeys()
    {
        var m = SettingsManifestBuilder.Build<AutomataVaultSettings>();
        Assert.That(m.AppId, Is.EqualTo("Automata"));
        Assert.That(m.ConfigurationPrefix, Is.EqualTo("MindAttic:Settings:Automata"));
        void Check(IEnumerable<SettingField> fields)
        {
            foreach (var f in fields)
            {
                Assert.That(f.Label, Is.Not.Empty, f.Name);
                Assert.That(f.Group, Is.Not.EqualTo(SettingsManifest.DefaultGroup), f.Name + " sits in a named group");
                Assert.That(f.Name, Does.Not.Match("(?i)(apikey|key|password|secret|token)$"), f.Name);
                if (f.Fields is { Count: > 0 }) Check(f.Fields);
            }
        }
        Check(m.Fields);
        Assert.That(m.Fields.Select(f => f.Group).Distinct(), Is.EquivalentTo(new[] { "AI provider", "Appearance", "Engine defaults" }));
        Assert.That(m.Credentials.Select(c => c.ProviderId), Is.EquivalentTo(new[] { "claude", "openai", "gemini", "kimi" }));
        var leaves = ManifestLeaves(m.Fields).ToList();
        foreach (var windowState in new[] { "sidebarWidth", "panelDetached", "panelWindowLeft", "panelWindowTop", "panelWindowWidth", "panelWindowHeight" })
            Assert.That(leaves, Has.None.EndsWith(":" + windowState), "window state is UI state, not a setting");
    }

    [Test]
    public void LoadOrSeed_OnAnEmptyFolder_SeedsExactlyTodaysDefaults()
    {
        var seeded = Vault().LoadOrSeed();
        var today = new AutomataSettings();
        Assert.Multiple(() =>
        {
            Assert.That(seeded.Provider, Is.EqualTo(today.Provider));
            Assert.That(seeded.Theme, Is.EqualTo(today.Theme));
            Assert.That(seeded.BorderRadius, Is.EqualTo(today.BorderRadius));
            Assert.That(seeded.EngineDefaults.ToOverride(), Is.Null, "no global override: every engine setting at its floor");
            Assert.That(File.Exists(Path.Combine(vaultDir, SettingsManifest.FileName)), Is.True);
            Assert.That(EngineSettingsResolver.Resolve(Store().Load()), Is.EqualTo(EngineSettingsResolver.Floor()));
        });
    }

    [Test]
    public void AFreshInstall_WithNoRow_SeedsDefaultsAndStaysUnsaved()
    {
        var store = Store();
        var s = store.Load();
        Assert.That((s.Provider, s.Theme, s.BorderRadius, s.EngineDefaults), Is.EqualTo(("claude", "dark", 5, (EngineSettingsOverride?)null)));
        Assert.That(Vault().Exists(), Is.True, "seeded");
        Assert.That(store.HasSaved, Is.False, "defaults are not exported as this workspace's settings");
        Vault().Update(v => v.Theme = "light");
        Assert.That(store.HasSaved, Is.True, "a value set in Vault is");
    }

    [Test]
    public void TheVaultFileWins_OnceItExists_AndTheRowIsLeftAsItWas()
    {
        TodaysRow(r => r.BorderRadius = 8);
        Vault().Save(new AutomataVaultSettings { BorderRadius = 2 });
        Assert.That(Store().Load().BorderRadius, Is.EqualTo(2));
        using var ctx = db.Database.CreateDbContext();
        Assert.That(ctx.Settings.Single().BorderRadius, Is.EqualTo(8), "the old storage is the rollback, never changed by the import");
    }

    [Test]
    public void WindowStateStaysInTheDatabase_AndItsSavesLeaveTheVaultFileAlone()
    {
        var store = Store();
        var s = store.Load();
        var stamp = File.GetLastWriteTimeUtc(Vault().FilePath);
        s.SidebarWidth = 512;
        s.PanelDetached = true;
        s.PanelWindowLeft = -1200;
        store.Save(s);
        Assert.That(File.GetLastWriteTimeUtc(Vault().FilePath), Is.EqualTo(stamp), "no rewrite, so no change event");
        var back = Store().Load();
        Assert.That((back.SidebarWidth, back.PanelDetached, back.PanelWindowLeft), Is.EqualTo((512d, true, (double?)-1200)));
        Assert.That(File.ReadAllText(Vault().FilePath), Does.Not.Contain("sidebarWidth").And.Not.Contain("panelDetached"));
    }

    [Test]
    public async Task VaultSaves_RaiseChanged_SoTheRunningAppRepushesItsSettings()
    {
        using var store = Store();
        _ = store.Load();
        var seen = new TaskCompletionSource();
        store.Changed += () => seen.TrySetResult();
        store.Watch();
        Vault().Update(v => v.Theme = "light");
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(store.Load().Theme, Is.EqualTo("light"));
    }

    [Test]
    public void TheRealDatabaseUsesTheRealVaultFile_AScratchOneGetsItsOwn()
    {
        Assert.That(AutomataSettingsStore.VaultFor(new AutomataDatabase(AutomataDatabase.DefaultPath)).FilePath,
            Is.EqualTo(Path.Combine(VaultPaths.RoamingBucket("Automata"), "settings.json")));
        Assert.That(db.Settings().Vault.Directory, Does.StartWith(db.Database.DataDirectory), "tests and harnesses never touch the user's settings");
    }

    [Test]
    public void AWorkspaceExport_CarriesTheVaultValues_AndImportingIntoAnEmptyDatabaseRestoresThem()
    {
        var source = db.Settings();
        source.Load();
        source.Vault.Update(v => { v.Provider = "openai"; v.Theme = "light"; v.EngineDefaults.SelfHeal = false; });
        var export = db.Archive().BuildWorkspaceExport();
        Assert.That(export.Settings?.Provider, Is.EqualTo("openai"));

        using var target = new TestDb();
        target.Archive().ImportJson(System.Text.Json.JsonSerializer.Serialize(export, AutomataJson.Options));
        var restored = target.Settings().Load();
        Assert.That((restored.Provider, restored.Theme, restored.EngineDefaults?.SelfHeal), Is.EqualTo(("openai", "light", (bool?)false)));
        Assert.That(target.Settings().Vault.Load().Provider, Is.EqualTo("openai"), "the restore lands in the target's Vault file");
    }

    // ---- keys ---------------------------------------------------------------------------------------

    private static (NamedKeyStore Keys, KeyHealthStore Health, string Dir) KeyVault()
    {
        var dir = Directory.CreateTempSubdirectory("automata-keys-").FullName;
        return (new NamedKeyStore(dir, new KeyAssignmentStore(Path.Combine(dir, "Vault"))), new KeyHealthStore(Path.Combine(dir, "health")), dir);
    }

    private static readonly Func<IReadOnlyList<string>> OldFallback = () => ["sk-oauth-or-shared"];

    [Test]
    public void Keys_AutomatasAssignedKeyWins()
    {
        var (store, health, dir) = KeyVault();
        try
        {
            store.Add("claude", "shared", "sk-shared");
            store.Add("claude", "automata", "sk-automata");
            store.Assignments.Set("Automata", "claude", ["automata", "shared"]);
            var pool = new AutomataKeys(store, health).PoolResolver("claude", OldFallback)();
            Assert.That(pool, Is.EqualTo(new[] { "sk-automata", "sk-shared" }));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void Keys_WithoutAnAssignment_TheOldFallbackOrderIsUnchanged()
    {
        var (store, health, dir) = KeyVault();
        try
        {
            store.Add("claude", "shared", "sk-shared");
            var keys = new AutomataKeys(store, health);
            Assert.That(keys.HasAssignment("claude"), Is.False);
            Assert.That(keys.PoolResolver("claude", OldFallback)(), Is.EqualTo(new[] { "sk-oauth-or-shared" }),
                "Claude: OAuth session first, then the shared key — whatever the old chain says");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void Keys_AssignedKeysThatAllFailed_FallBackToTheOldChain()
    {
        var (store, health, dir) = KeyVault();
        try
        {
            store.Add("openai", "only", "sk-only");
            store.Assignments.Set("Automata", "openai", ["only"]);
            health.RecordFailure("openai", "only", KeyFailureKind.Auth, DateTimeOffset.UtcNow);
            Assert.That(new AutomataKeys(store, health).PoolResolver("openai", OldFallback)(), Is.EqualTo(new[] { "sk-oauth-or-shared" }));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void Keys_AutomatasOwnPreLampCopies_AreImportedAndBecomeItsActiveKeys()
    {
        var (store, health, dir) = KeyVault();
        try
        {
            store.Add("claude", "shared", "sk-shared");
            new AppScopedCredentialStore("automata", store.StoreFor("claude")).SetKey("claude", "sk-own");
            KeyImporter.ImportAppScopedFor("Automata", store);
            var pool = new AutomataKeys(store, health).PoolResolver("claude", OldFallback)();
            Assert.That(pool[0], Is.EqualTo("sk-own"), "it keeps using the key it used before");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Keys_FirstAssignedKey401_TheSecondIsUsed()
    {
        var (store, health, dir) = KeyVault();
        try
        {
            store.Add("claude", "bad", "sk-bad");
            store.Add("claude", "good", "sk-good");
            store.Assignments.Set("Automata", "claude", ["bad", "good"]);
            var handler = new ByKeyHandler(new()
            {
                ["sk-bad"] = (HttpStatusCode.Unauthorized, """{"type":"error","error":{"message":"invalid key"}}"""),
                ["sk-good"] = (HttpStatusCode.OK, """{"content":[{"type":"text","text":"hello"}]}"""),
            });
            var llm = new AnthropicToolCallingLlm(new AnthropicToolClient(new HttpClient(handler), NullLogger<AnthropicToolClient>.Instance),
                new AutomataKeys(store, health).PoolResolver("claude", OldFallback));

            var result = await llm.CreateTurnAsync("system", [new ToolLoopMessage.UserText("hi")], [], 100, default);

            Assert.That(((AssistantPart.Text)result.Parts[0]).Value, Is.EqualTo("hello"));
            Assert.That((handler.Calls("sk-bad"), handler.Calls("sk-good")), Is.EqualTo((1, 1)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Keys_AddAutomataCore_ResolvesTheLlmKeysThroughAutomatasVaultAssignment()
    {
        var previousDb = Environment.GetEnvironmentVariable(Automata.Core.Automation.Data.AutomataDatabase.PathVariable);
        Environment.SetEnvironmentVariable(Automata.Core.Automation.Data.AutomataDatabase.PathVariable, db.Database.DatabasePath);
        var keys = NamedKeyStore.ForRoamingRoot();
        try
        {
            keys.Add("kimi", "automata-kimi-test", "sk-kimi");
            keys.Assignments.Set("Automata", "kimi", ["automata-kimi-test"]);
            using var sp = new ServiceCollection().AddLogging().AddAutomataCore().BuildServiceProvider();
            var kimi = sp.GetRequiredService<IReadOnlyList<IToolCallingLlm>>().Single(p => p.Name == "Kimi");
            Assert.That(await kimi.IsConfiguredAsync(), Is.True);
            keys.Assignments.Set("Automata", "kimi", []);
            keys.Remove("kimi", "automata-kimi-test");
            Assert.That(sp.GetRequiredService<AutomataKeys>().HasAssignment("kimi"), Is.False, "read live, no restart");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Automata.Core.Automation.Data.AutomataDatabase.PathVariable, previousDb);
        }
    }

    // ---- the cog and the old UI -----------------------------------------------------------------

    private sealed class FakeLauncher : IVaultDesktopLauncher
    {
        public List<VaultOpenRequest> Sent { get; } = [];
        public bool TrySend(VaultOpenRequest request) { Sent.Add(request); return true; }
        public bool TryStart(VaultOpenRequest request) => false;
    }

    [Test]
    public void TheCog_OpensVaultAtAutomatasTab()
    {
        var previous = VaultSettings.Launcher;
        var fake = new FakeLauncher();
        VaultSettings.Launcher = fake;
        try
        {
            Assert.That(AutomataVaultSettings.OpenInVault(), Is.True);
            Assert.That(AutomataVaultSettings.OpenInVault(FullName("borderRadius")), Is.True);
            Assert.That(fake.Sent.Select(r => (r.AppId, r.Setting)),
                Is.EqualTo(new[] { ("Automata", (string?)null), ("Automata", FullName("borderRadius")) }));
        }
        finally { VaultSettings.Launcher = previous; }
    }

    private static string AppFile(string relative) =>
        File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "Automata.App", relative));

    [Test]
    public void TheOldSettingsControlsAreGone_AndSettingsOffersOpenInVault()
    {
        var html = AppFile(Path.Combine("wwwroot", "panel.html"));
        foreach (var gone in new[] { "id=\"llm-claude\"", "id=\"key-claude\"", "id=\"set-key-save\"", "id=\"set-theme\"", "id=\"set-radius\"", "id=\"set-engine-defaults\"" })
            Assert.That(html, Does.Not.Contain(gone));
        Assert.That(html, Does.Contain("id=\"open-vault\""));
        // Kept: examples, import/export, data folder, detach.
        foreach (var kept in new[] { "id=\"set-regen-demos\"", "id=\"btn-export-workspace\"", "id=\"btn-folder\"", "id=\"set-detach\"" })
            Assert.That(html, Does.Contain(kept));

        Assert.That(AppFile(Path.Combine("wwwroot", "settings.js")), Does.Contain("post('openVault')").And.Not.Contain("saveSettings"));
        Assert.That(AppFile(Path.Combine("wwwroot", "keysetup.js")), Does.Contain("post('openVault')"));
        Assert.That(AppFile(Path.Combine("wwwroot", "scoped-settings.js")), Does.Not.Contain("saveSettings"), "the global scope is edited in Vault only");

        var controller = AppFile("AutomationController.cs");
        Assert.That(controller, Does.Contain("case \"openVault\":").And.Contain("AutomataVaultSettings.OpenInVault("));
        Assert.That(controller, Does.Not.Contain("case \"saveSettings\":").And.Not.Contain("AppScopedCredentialStore"));
    }

    private sealed class NamedLlm(string name) : IToolCallingLlm
    {
        public string Name => name;
        public Task<bool> IsConfiguredAsync() => Task.FromResult(true);
        public Task<ToolTurnResult> CreateTurnAsync(string systemPrompt, IReadOnlyList<ToolLoopMessage> history,
            IReadOnlyList<ToolDefinition> tools, int maxTokens, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ByKeyHandler(Dictionary<string, (HttpStatusCode Code, string Body)> byKey) : HttpMessageHandler
    {
        private readonly Dictionary<string, int> counts = [];
        public int Calls(string key) => counts.GetValueOrDefault(key);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = request.Headers.TryGetValues("x-api-key", out var v) ? v.First() : "";
            counts[key] = Calls(key) + 1;
            var (code, body) = byKey.TryGetValue(key, out var r) ? r : (HttpStatusCode.InternalServerError, "unknown key");
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
