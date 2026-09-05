using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Profiles;

/// <summary>
/// The three acceptance scenarios, as tasks you can open and run.
/// <para>
/// <b>These are not demos, and the difference is the whole point.</b> Everything in Demos is
/// generated against pages this repo also generates, so a demo failing means Automata broke. These
/// point at sites nobody here controls, so one failing might mean Automata broke, or might mean
/// Google changed its markup this morning, or that a consent wall appeared, or that the network is
/// down. That is a different kind of signal and it does not belong in the same batch — which is why
/// they are seeded only when asked for (<c>automata-runner profiles seed</c>), never on launch, and
/// checked only by <c>tools/verify-live.mjs</c>, which refuses to run without <c>--live</c>.
/// </para>
/// <para>
/// They carry no <see cref="TaskDefinition.Demo"/> marker, so nothing ever regenerates them. A
/// profile is a starting point you are expected to re-record when a site moves under it — and the
/// first thing that happens when one does is that self-healing repairs what it can and writes the
/// repair back, which is most of what pointing these at live sites is for.
/// </para>
/// </summary>
public static class AcceptanceProfiles
{
    public const string CollectionName = "Acceptance";

    public const string TermInput = "term";
    public const string DefaultTerm = "wolf";

    /// <summary>Where the mail profile goes, and who it signs in as. Never stored in the task.</summary>
    public const string MailUrlVar = "AUTOMATA_MAIL_URL";
    public const string MailUserVar = "AUTOMATA_MAIL_USER";
    public const string MailPassVar = "AUTOMATA_MAIL_PASS";

    /// <summary>How many subject lines the mail profile keeps — the scenario says the first 20.</summary>
    public const int MailSubjectCap = 20;

    public const string GoogleDataset = "google-titles.csv";
    public const string BingDataset = "bing-titles.csv";
    public const string MailDataset = "inbox-subjects.csv";

    public static IReadOnlyList<TaskDefinition> All() => [Google(), Bing(), Webmail()];

    // ---- the two searches ------------------------------------------------------------------------

    /// <summary>
    /// Google's search box is a <c>textarea</c> rather than an input, and has been since 2023 —
    /// fingerprinted by its name rather than by any class, because every class on that page is
    /// generated and would be rejected as unstable anyway.
    /// </summary>
    private static TaskDefinition Google() => new()
    {
        Id = "profile-google",
        Name = "Google search — result titles",
        Description =
            "Searches Google for the term it is given and collects the result titles. Points at a "
            + "site nobody here controls, so it is seeded on request and checked only by the live "
            + "suite.",
        StartUrl = "https://www.google.com/",
        Inputs = [Term()],
        Steps =
        [
            Type("profile-google-type", "Type the term into the search box",
                new ElementFingerprint
                {
                    Tag = "textarea", NameAttr = "q", AriaRole = "combobox", AriaLabel = "Search",
                }),
            Enter("profile-google-enter"),
            new Step
            {
                Id = "profile-google-wait",
                Action = StepAction.WaitForElement,
                Label = "Wait for the results to arrive",
                Target = new ElementFingerprint { Tag = "div", Id = "search", CssSelector = "#search" },
            },
            Collect("profile-google-harvest", "Collect the result titles",
                // The results container and the heading level are the two things on that page that
                // have outlived every redesign. Anything narrower would be a class name that is
                // regenerated on every deploy.
                itemSelector: "#search h3",
                field: new HarvestField { Name = "title", Source = HarvestSource.Text },
                dataset: GoogleDataset),
        ],
    };

    /// <summary>
    /// Bing is the easier of the two to read: it still serves its results as markup, one
    /// <c>li.b_algo</c> per result with the title in an anchor inside the heading.
    /// </summary>
    private static TaskDefinition Bing() => new()
    {
        Id = "profile-bing",
        Name = "Bing search — result titles",
        Description =
            "The same scenario against a second engine, which is what makes it an acceptance check "
            + "rather than a Google check.",
        StartUrl = "https://www.bing.com/",
        Inputs = [Term()],
        Steps =
        [
            Type("profile-bing-type", "Type the term into the search box",
                new ElementFingerprint
                {
                    Tag = "textarea", Id = "sb_form_q", CssSelector = "#sb_form_q", NameAttr = "q",
                }),
            Enter("profile-bing-enter"),
            new Step
            {
                Id = "profile-bing-wait",
                Action = StepAction.WaitForElement,
                Label = "Wait for the results to arrive",
                // The results LIST, not a result. A wait targets one element — pointing it at
                // `li.b_algo` matched all ten of them, which the resolver correctly refused as
                // ambiguous rather than picking whichever came first.
                Target = new ElementFingerprint { Tag = "ol", Id = "b_results", CssSelector = "#b_results" },
            },
            Collect("profile-bing-harvest", "Collect the result titles",
                itemSelector: "li.b_algo",
                field: new HarvestField { Name = "title", Selector = "h2 a", Source = HarvestSource.Text },
                dataset: BingDataset),
        ],
    };

    // ---- the one that needs an account -----------------------------------------------------------

    /// <summary>
    /// Reads the subject lines off an inbox.
    /// <para>
    /// <b>The one profile that is a starting point rather than a finished task.</b> The two searches
    /// can be written against markup anyone can go and look at; a mailbox cannot, and every provider
    /// lays its list out differently. So this is written against the shape a correctly built mail
    /// list has — an ARIA <c>main</c> region of <c>row</c>s — and is expected to be re-recorded
    /// against whichever one you use. When it fails, it fails with "no rows matched", which says
    /// what to do.
    /// </para>
    /// <para>
    /// Where it goes and who it signs in as are read from the environment, never stored: a task
    /// file is something you export and hand to somebody, and a password in one would travel with
    /// it. That is also the only demonstration anywhere of an environment-variable binding, which
    /// existed and had nothing pointing at it.
    /// </para>
    /// </summary>
    private static TaskDefinition Webmail() => new()
    {
        Id = "profile-webmail",
        Name = "Webmail — the first 20 subject lines",
        Description =
            $"Signs in with {MailUserVar}/{MailPassVar} at {MailUrlVar} and collects the subject "
            + "lines it can see. Re-record the sign-in and the collecting step against your own "
            + "provider — the shape is right, the selectors are a starting point.",
        Steps =
        [
            new Step
            {
                Id = "profile-webmail-open",
                Action = StepAction.Navigate,
                Label = "Open the mailbox named in the environment",
                Url = "https://mail.google.com/",
                Bindings = new Dictionary<string, BindingRef> { ["Url"] = Env(MailUrlVar, "mailbox URL") },
            },
            Type("profile-webmail-user", "Type the account name",
                new ElementFingerprint { Tag = "input", TypeAttr = "email", CssSelector = "input[type=\"email\"]" },
                Env(MailUserVar, "account name")),
            Enter("profile-webmail-user-enter"),
            Type("profile-webmail-pass", "Type the password",
                new ElementFingerprint { Tag = "input", TypeAttr = "password", CssSelector = "input[type=\"password\"]" },
                Env(MailPassVar, "password"),
                masked: true),
            Enter("profile-webmail-pass-enter"),
            new Step
            {
                Id = "profile-webmail-wait",
                Action = StepAction.WaitForElement,
                Label = "Wait for the inbox to finish loading",
                Target = new ElementFingerprint
                {
                    Tag = "div", AriaRole = "main", CssSelector = "[role=\"main\"]",
                },
            },
            Collect("profile-webmail-harvest", $"Collect the first {MailSubjectCap} subject lines",
                itemSelector: "[role=\"main\"] [role=\"row\"]",
                field: new HarvestField { Name = "subject", Source = HarvestSource.Text },
                dataset: MailDataset,
                maxRows: MailSubjectCap),
        ],
    };

    // ---- shorthand -------------------------------------------------------------------------------

    private static TaskInput Term() => new()
    {
        Name = TermInput,
        Description = "What to search for",
        Default = DefaultTerm,
    };

    private static BindingRef Env(string name, string what) => new()
    {
        Kind = BindingKind.EnvVar,
        EnvVarName = name,
        Label = $"{what} ({name})",
    };

    private static Step Type(
        string id, string label, ElementFingerprint target, BindingRef? binding = null, bool masked = false) => new()
    {
        Id = id,
        Action = StepAction.TypeText,
        Label = label,
        Target = target,
        // The literal stays beside the binding, the way the editor keeps it: unbinding a field
        // should give back what was in it rather than an empty box.
        Value = binding == null ? DefaultTerm : null,
        Masked = masked,
        Bindings = new Dictionary<string, BindingRef>
        {
            ["Value"] = binding ?? new BindingRef
            {
                Kind = BindingKind.TaskInput,
                ParameterName = TermInput,
                Label = "the term this task was given",
            },
        },
    };

    private static Step Enter(string id) => new()
    {
        Id = id,
        Action = StepAction.PressEnter,
        Label = "Press Enter in the field that has focus",
    };

    private static Step Collect(
        string id, string label, string itemSelector, HarvestField field, string dataset, int? maxRows = null) => new()
    {
        Id = id,
        Action = StepAction.ExtractAll,
        Label = label,
        Harvest = new HarvestSpec
        {
            ItemSelector = itemSelector,
            DatasetName = dataset,
            Format = "csv",
            Append = false,
            MaxRows = maxRows,
            Fields = [field],
        },
        Outputs = [new OutputField { Name = "count", Description = "How many rows were collected" }],
    };
}
