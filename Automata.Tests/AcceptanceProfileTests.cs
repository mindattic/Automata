using System.IO;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Profiles;
using Automata.Core.Automation.Storage;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// The acceptance profiles point at sites nobody here controls, so nothing in this file can assert
/// that a run of one works — <c>tools/verify-live.mjs</c> is where that is found out, by hand. What
/// IS provable offline is the shape: that they install where they say, that installing twice does
/// not duplicate anything, and that no password is ever written into a task file.
/// </summary>
[TestFixture]
public class AcceptanceProfileTests
{
    private string root = null!;
    private CollectionStore collections = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));
        collections = new CollectionStore(Path.Combine(root, "collections"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private AcceptanceProfileSeeder Seeder() => new(collections);

    [Test]
    public void Seeding_InstallsEveryProfileIntoItsOwnCollection()
    {
        var report = Seeder().Seed();

        var collection = collections.GetCollection(report.CollectionId)!;
        var installed = collections.LoadTasks(collection.Id);
        Assert.Multiple(() =>
        {
            Assert.That(collection.Name, Is.EqualTo(AcceptanceProfiles.CollectionName));
            Assert.That(installed, Has.Count.EqualTo(AcceptanceProfiles.All().Count));
            Assert.That(report.Added, Has.Count.EqualTo(installed.Count));
            Assert.That(report.Kept, Is.Empty);
        });
    }

    [Test]
    public void SeedingTwice_AddsNothingAndSaysSo()
    {
        Seeder().Seed();
        var again = Seeder().Seed();

        Assert.Multiple(() =>
        {
            Assert.That(again.Added, Is.Empty);
            Assert.That(again.Kept, Has.Count.EqualTo(AcceptanceProfiles.All().Count));
            Assert.That(collections.LoadTasks(again.CollectionId),
                Has.Count.EqualTo(AcceptanceProfiles.All().Count));
        });
    }

    /// <summary>
    /// A profile is a starting point you are meant to adapt, so seeding again after you have
    /// changed one must leave YOUR version alone — and must not put a second copy beside it. Matched
    /// by id rather than by name, or renaming one would be enough to get a duplicate.
    /// </summary>
    [Test]
    public void AnAdaptedProfile_IsLeftAloneRatherThanDuplicated()
    {
        var first = Seeder().Seed();
        var mine = collections.LoadTasks(first.CollectionId).First(t => t.Id == "profile-google");
        mine.Name = "My own search";
        mine.Steps[0].Label = "Type it into the box I re-recorded";
        collections.SaveTask(mine);

        Seeder().Seed();

        var after = collections.LoadTasks(first.CollectionId);
        Assert.Multiple(() =>
        {
            Assert.That(after, Has.Count.EqualTo(AcceptanceProfiles.All().Count));
            var kept = after.Single(t => t.Id == "profile-google");
            Assert.That(kept.Name, Is.EqualTo("My own search"));
            Assert.That(kept.Steps[0].Label, Is.EqualTo("Type it into the box I re-recorded"));
        });
    }

    /// <summary>
    /// Demos is generated territory and gets refreshed on every launch; profiles are not and must
    /// not be. The marker is what tells the two apart, so its absence is the guarantee.
    /// </summary>
    [Test]
    public void NoProfileCarriesADemoMarker()
    {
        Assert.That(AcceptanceProfiles.All().Where(t => t.Demo != null).Select(t => t.Name),
            Is.Empty, "a profile with a demo marker would be regenerated over the top of your edits");
    }

    [Test]
    public void EveryStepIdIsUniqueAcrossEveryProfile()
    {
        var ids = AcceptanceProfiles.All()
            .SelectMany(t => Step.Flatten(t.Steps))
            .Select(s => s.Id)
            .ToList();

        Assert.That(ids, Is.Unique);
        Assert.That(AcceptanceProfiles.All().Select(t => t.Id), Is.Unique);
    }

    /// <summary>
    /// The one thing about the mail profile that is not negotiable. A task file is something you
    /// export and hand to somebody; a password written into one would travel with it.
    /// </summary>
    [Test]
    public void TheMailProfile_ReadsItsCredentialsFromTheEnvironment_AndStoresNone()
    {
        var mail = AcceptanceProfiles.All().Single(t => t.Id == "profile-webmail");
        var bound = Step.Flatten(mail.Steps)
            .Where(s => s.Bindings != null)
            .SelectMany(s => s.Bindings!.Values)
            .Where(b => b.Kind == BindingKind.EnvVar)
            .Select(b => b.EnvVarName)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(bound, Does.Contain(AcceptanceProfiles.MailUrlVar));
            Assert.That(bound, Does.Contain(AcceptanceProfiles.MailUserVar));
            Assert.That(bound, Does.Contain(AcceptanceProfiles.MailPassVar));

            // Nothing that looks like a credential is written down anywhere in the task.
            var literals = Step.Flatten(mail.Steps).Select(s => s.Value).Where(v => v != null);
            Assert.That(literals, Is.Empty,
                "the mail profile should carry no literal values at all — every one of them would be "
                + "either a credential or a URL somebody else's account has no use for");

            var password = Step.Flatten(mail.Steps).Single(s => s.Id == "profile-webmail-pass");
            Assert.That(password.Masked, Is.True, "a password step has to be masked in the recording");
        });
    }

    /// <summary>
    /// The bug the first live run found, kept as a rule. A wait or an assert targets ONE element,
    /// and the Bing profile originally pointed its wait at `li.b_algo`, which matches every result
    /// on the page — correctly refused as ambiguous, at the cost of a run that looked like Bing had
    /// changed. Anything plural belongs in a harvest's item selector, never in a target.
    /// </summary>
    [Test]
    public void NoTargetSelectorAsksForMoreThanOneElement()
    {
        var plural = AcceptanceProfiles.All()
            .SelectMany(t => Step.Flatten(t.Steps))
            .Where(s => s.Target?.CssSelector != null)
            .Where(s => !s.Target!.CssSelector!.StartsWith('#')
                     && !s.Target.CssSelector.Contains('['))
            .Select(s => $"{s.Id}: {s.Target!.CssSelector}")
            .ToList();

        Assert.That(plural, Is.Empty,
            "a target has to identify one element — a class or tag selector matches many, and the "
            + "resolver will refuse it as ambiguous rather than pick one");
    }
}
