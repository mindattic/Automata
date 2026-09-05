using Automata.Core.Automation.Storage;

namespace Automata.Core.Automation.Profiles;

/// <summary>What a seed did. Named tasks, not counts, because the names are what you go and open.</summary>
public sealed record ProfileSeedReport(
    string CollectionId,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Kept);

/// <summary>
/// Installs <see cref="AcceptanceProfiles"/> into a collection of their own, on request.
/// <para>
/// Deliberately much simpler than <see cref="Demos.DemoSeeder"/>, and for a reason rather than to
/// save work: a demo is generated territory that the build owns and refreshes, whereas a profile is
/// a starting point you are expected to change — re-record its sign-in, point it at your own
/// provider, tighten a selector after a site moved. So this only ever ADDS what is missing. There is
/// no refresh, no regenerate and no content hash, because there is no version of a profile that this
/// repo is entitled to consider correct once you have adapted it.
/// </para>
/// </summary>
public sealed class AcceptanceProfileSeeder(CollectionStore collections)
{
    public ProfileSeedReport Seed()
    {
        var collection = collections.EnsureCollectionNamed(AcceptanceProfiles.CollectionName);
        if (string.IsNullOrEmpty(collection.Description))
        {
            collection.Description =
                "Scenarios against sites nobody here controls. Seeded only when asked for, never "
                + "refreshed, and checked by tools/verify-live.mjs rather than by the ordinary "
                + "suites — a failure here can as easily mean a site changed as that Automata did.";
            collections.SaveCollection(collection);
        }

        var existing = collections.LoadTasks(collection.Id);
        List<string> added = [], kept = [];

        foreach (var profile in AcceptanceProfiles.All())
        {
            // By id, not by name: a profile you renamed is still that profile, and seeding again
            // must not put a second copy of it beside yours.
            if (existing.Any(t => t.Id == profile.Id))
            {
                kept.Add(existing.First(t => t.Id == profile.Id).Name);
                continue;
            }
            profile.CollectionId = collection.Id;
            collections.SaveTask(profile);
            added.Add(profile.Name);
        }

        return new ProfileSeedReport(collection.Id, added, kept);
    }
}
