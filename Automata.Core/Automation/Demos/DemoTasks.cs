using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Demos;

/// <summary>A factory demo task, identified by a stable key rather than by its display name.</summary>
public sealed record DemoTask(
    string Key,
    string Name,
    string Description,
    string? StartUrl,
    List<Step> Steps,
    EngineSettingsOverride? Settings = null,
    List<TaskInput>? Inputs = null,
    List<TaskOutput>? Outputs = null)
{
    /// <summary>
    /// The id the seeded task carries. Fixed, for the same reason step ids are: a
    /// <see cref="StepAction.RunTask"/> step points at a task BY id, so an id generated per
    /// install would either break every reference or leave the seeder unable to write one at all.
    /// </summary>
    public string TaskId => TaskIdFor(Key);

    public static string TaskIdFor(string key) => $"demo-{key}";
}

/// <summary>
/// The demo tasks the generator seeds, built against the pages in <see cref="DemoPages"/>.
/// <para>
/// Written as records here rather than recorded by hand so they can be regenerated: a demo that
/// only exists as a file someone once saved goes stale the moment the model gains a field, and
/// then the one thing a new user runs first is the one thing that is out of date.
/// </para>
/// <para>
/// <b>Every id here is fixed, not generated</b> — step ids and the task ids alike. An id is what a
/// binding or a <c>runTask</c> step points at, so regenerating with fresh ids would either break
/// every reference or make every demo look hand-edited to the seeder. Fixed ids mean a regenerated
/// demo is byte-for-byte the demo it replaces.
/// </para>
/// <para>
/// Between them the demos exercise every value of <see cref="StepAction"/>,
/// <see cref="ConditionOp"/> and <see cref="WaitMode"/> — <c>DemoCoverageTests</c> fails the build
/// when a new one is added and nothing here demonstrates it. That is deliberate pressure: a
/// capability nobody can find an example of is a capability nobody will use.
/// </para>
/// <para>
/// URLs and file paths are absolute and baked in at generation time, which is why regenerating is
/// the documented fix if the workspace folder ever moves.
/// </para>
/// </summary>
public static class DemoTasks
{
    public const string CollectionName = "Demos";

    /// <summary>Datasets the shop examples write.</summary>
    public const string ProductsDataset = "shop-products.csv";
    public const string PricesDataset = "shop-prices.csv";

    /// <summary>Dataset the order example writes one row to per check that held.</summary>
    public const string OrderChecksDataset = "order-checks.csv";

    /// <summary>Where the slow example writes the two readings that prove its wait watched the
    /// page rather than re-checking what had already been read.</summary>
    public const string SlowReadingsDataset = "slow-readings.csv";

    /// <summary>The rows harvested from inside a same-origin frame, and from across an origin
    /// boundary. Two files, because they are reached by two different mechanisms and a check that
    /// could not tell which one broke would be worth less.</summary>
    public const string FramedRowsDataset = "framed-rows.csv";
    public const string OpaqueRowsDataset = "opaque-rows.csv";

    /// <summary>Dataset the roster example writes one row to per person it added.</summary>
    public const string RosterAddedDataset = "roster-added.csv";

    /// <summary>Dataset the pipeline example's last task writes its one row to.</summary>
    public const string PipelineDataset = "pipeline-ticket.csv";

    /// <summary>The names the pipeline tasks publish and take. Written once, because a wiring is
    /// two halves that have to agree and a typo in either is a wiring that silently does nothing.</summary>
    public const string TicketIdValue = "ticketId";
    public const string TicketOwnerValue = "owner";
    public const string TicketPriorityValue = "priority";

    /// <summary>
    /// What the middle task looks up when nothing hands it a ticket. It has to be a REAL ticket on
    /// the page: an input's default is what makes a wired task still runnable on its own, and a
    /// default that fails would teach the opposite lesson.
    /// </summary>
    public const string FallbackTicketId = "TCK-2316";

    /// <summary>
    /// The time of day the parking example waits for. Any fixed time works — the point is that it
    /// is far enough away to be worth releasing the browser for, which is what parking means.
    /// </summary>
    public static readonly TimeOnly ParkTimeOfDay = new(9, 0);

    public static IReadOnlyList<DemoTask> All(string demoRoot) =>
    [
        Buttons(demoRoot),
        Drift(demoRoot),
        Form(demoRoot),
        Slow(demoRoot),
        Order(demoRoot),
        Zoom(demoRoot),
        Invoices(demoRoot),
        Shadow(demoRoot),
        Closed(demoRoot),
        Roster(demoRoot),
        Search(demoRoot),
        Chain(demoRoot),
        ShopPrices(demoRoot),
        PipelineFind(demoRoot),
        PipelineLookUp(demoRoot),
        PipelineRecord(demoRoot),
        Park(demoRoot),
    ];

    /// <summary>A <c>file://</c> URL for a page under the demo root.</summary>
    public static string PageUrl(string demoRoot, string relativePath) =>
        new Uri(Path.Combine(demoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))).AbsoluteUri;

    /// <summary>The local path of a generated file, for steps that take a path rather than a URL.</summary>
    public static string FilePath(string demoRoot, string relativePath) =>
        Path.Combine(demoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    // ---- the smallest one --------------------------------------------------------------------

    private static DemoTask Buttons(string demoRoot) => new(
        "buttons",
        "Click a button",
        "The smallest complete task: open a page, click one button, and confirm the page reacted. "
        + "Start here.",
        PageUrl(demoRoot, "buttons.html"),
        [
            new Step
            {
                Id = "demo-buttons-click",
                Action = StepAction.Click,
                Label = "Click 'Beta'",
                Target = Css("button", "#beta"),
            },
            new Step
            {
                Id = "demo-buttons-assert",
                Action = StepAction.AssertElement,
                Label = "Confirm the page says 'clicked: beta'",
                Target = Css("div", ".clicked"),
                Value = "clicked: beta",
            },
        ]);

    // ---- the one that repairs itself -----------------------------------------------------------

    /// <summary>
    /// The only example whose fingerprint is deliberately WRONG. Its button's id and CSS selector
    /// name something `drift.html` no longer has — the shape of a site that redeployed — so the
    /// cascade falls through to the words on the button, finds it, and writes the identity it
    /// actually found back into the step. Run it twice and the second run resolves by id and heals
    /// nothing, which is the whole claim: a repair is kept, not re-made every time.
    /// <para>
    /// Healing edits the example, so the seeder will thereafter treat it as edited and leave it
    /// alone — the same protection every hand-edited task gets. <c>demos regenerate</c> puts the
    /// stale fingerprint back when you want to watch it happen again.
    /// </para>
    /// </summary>
    private static DemoTask Drift(string demoRoot) => new(
        "drift",
        "Repair a step whose page moved",
        "The page was redeployed and the button's id changed. Nothing about this task is edited by "
        + "hand: it finds the button by the words on it, then saves what it found so the next run "
        + "resolves first time.",
        PageUrl(demoRoot, "drift.html"),
        [
            new Step
            {
                Id = "demo-drift-click",
                Action = StepAction.Click,
                Label = "Click 'Place order'",
                // Recorded against the old markup: this id and selector no longer exist.
                Target = new ElementFingerprint
                {
                    Tag = "button",
                    Id = "place-order-v1",
                    CssSelector = "#place-order-v1",
                    VisibleText = "Place order",
                },
            },
            new Step
            {
                Id = "demo-drift-assert",
                Action = StepAction.AssertElement,
                Label = "Confirm the page says 'order placed'",
                Target = Css("div", ".clicked"),
                Value = "order placed",
            },
        ]);

    // ---- every input control -----------------------------------------------------------------

    /// <summary>
    /// One of every way a step can touch a field: keystrokes, a direct value, the Enter key,
    /// checkboxes both directions, a radio, a dropdown and a file upload — then a wait for the
    /// summary the page takes its time producing.
    /// <para>
    /// The typing and the choosing are wrapped in groups rather than left as one flat run of ten
    /// steps: a group is the only structural thing a task can say about itself, and a tree of two
    /// named halves is what the sidebar is for.
    /// </para>
    /// </summary>
    private static DemoTask Form(string demoRoot) => new(
        "form",
        "Fill in a form",
        "Type into a field, set one directly, press Enter, tick and untick a box, choose a radio "
        + "and a dropdown option, and attach a file — then wait for the summary the page takes a "
        + "moment to render. Every kind of field, in one task.",
        PageUrl(demoRoot, "form.html"),
        [
            new Step
            {
                Id = "demo-form-typing",
                Action = StepAction.Group,
                Label = "Fill in the text fields",
                Children =
                [
                    new Step
                    {
                        Id = "demo-form-name",
                        Action = StepAction.TypeText,
                        Label = "Type the name, keystroke by keystroke",
                        Target = Css("input", "#full-name"),
                        Value = "Ada Lovelace",
                    },
                    new Step
                    {
                        Id = "demo-form-email",
                        Action = StepAction.SetValue,
                        Label = "Set the email directly — faster, and works on React fields",
                        Target = Css("input", "#email"),
                        Value = "ada@example.com",
                    },
                    new Step
                    {
                        Id = "demo-form-search",
                        Action = StepAction.TypeText,
                        Label = "Type into the search box",
                        Target = Css("input", "#search"),
                        Value = "wolf",
                    },
                    // No target on purpose: Enter goes to whatever has focus, which is the field
                    // the step above just typed into. That is the idiom for a search box, where
                    // there is often no button to click.
                    new Step
                    {
                        Id = "demo-form-enter",
                        Action = StepAction.PressEnter,
                        Label = "Press Enter in the field that has focus",
                    },
                    new Step
                    {
                        Id = "demo-form-echo",
                        Action = StepAction.AssertElement,
                        Label = "Confirm the page saw the Enter key",
                        Target = Css("p", "#search-echo"),
                        Value = "searched: wolf",
                    },
                ],
            },
            new Step
            {
                Id = "demo-form-choices",
                Action = StepAction.Group,
                Label = "Make the choices",
                Children =
                [
                    new Step
                    {
                        Id = "demo-form-terms",
                        Action = StepAction.Check,
                        Label = "Tick 'I accept the terms'",
                        Target = Css("input", "#terms"),
                    },
                    new Step
                    {
                        Id = "demo-form-newsletter",
                        Action = StepAction.Uncheck,
                        Label = "Untick 'Send me the newsletter'",
                        Target = Css("input", "#newsletter"),
                    },
                    new Step
                    {
                        Id = "demo-form-express",
                        Action = StepAction.SelectRadio,
                        Label = "Choose express shipping",
                        Target = Css("input", "#ship-express"),
                    },
                    new Step
                    {
                        Id = "demo-form-size",
                        Action = StepAction.SelectOption,
                        Label = "Choose the 'Large' size",
                        Target = Css("select", "#size"),
                        Value = "Large",
                    },
                    // The file is generated beside the pages, so this path always exists. A demo
                    // that told the user to go and find a file of their own would not run.
                    new Step
                    {
                        Id = "demo-form-attach",
                        Action = StepAction.UploadFile,
                        Label = $"Attach {DemoPages.AttachmentFile}",
                        Target = Css("input", "#attachment"),
                        Value = FilePath(demoRoot, DemoPages.AttachmentFile),
                    },
                ],
            },
            new Step
            {
                Id = "demo-form-submit",
                Action = StepAction.Click,
                Label = "Submit the form",
                Target = Css("button", "#submit"),
                IsCommitPoint = true,
            },
            new Step
            {
                Id = "demo-form-wait",
                Action = StepAction.WaitForElement,
                Label = "Wait for the summary to appear",
                Target = Css("dl", "#summary"),
                TimeoutMs = 15_000,
            },
            new Step
            {
                Id = "demo-form-assert-name",
                Action = StepAction.AssertElement,
                Label = "The summary shows the name that was typed",
                Target = Css("dd", "#summary-name"),
                Value = "Ada Lovelace",
            },
            new Step
            {
                Id = "demo-form-assert-choices",
                Action = StepAction.AssertElement,
                Label = "…and every choice that was made",
                Target = Css("dd", "#summary-choices"),
                Value = "Large, Express, terms accepted, newsletter off",
            },
            new Step
            {
                Id = "demo-form-assert-file",
                Action = StepAction.AssertElement,
                Label = "…and the file that was attached",
                Target = Css("dd", "#summary-file"),
                Value = DemoPages.AttachmentFile,
            },
        ]);

    // ---- waiting ------------------------------------------------------------------------------

    /// <summary>
    /// The three shapes of waiting that a run can do without leaving the machine: a flat pause, a
    /// wait for an element that is not there yet, and a wait for a value to say what it should.
    /// <para>
    /// The element wait comes BEFORE the status is read, deliberately. Waiting for an element
    /// polls the page, so it is the honest way to wait out a render; a condition wait only ever
    /// re-checks values the run has already captured, so it cannot be used to wait for a page.
    /// </para>
    /// </summary>
    private static DemoTask Slow(string demoRoot) => new(
        "slow",
        "Wait for a page that is not ready",
        "Most pages finish drawing themselves after they have loaded. This one settles its status "
        + "after a moment and adds a whole panel a second later — so the task pauses, waits for "
        + "the element, reads the status, and only carries on once it says 'ready'.",
        PageUrl(demoRoot, "slow.html"),
        [
            new Step
            {
                Id = "demo-slow-pause",
                Action = StepAction.Wait,
                Label = "Pause for a moment",
                Wait = new WaitSpec { Mode = WaitMode.Duration, DurationMs = 400 },
            },
            new Step
            {
                Id = "demo-slow-late",
                Action = StepAction.WaitForElement,
                Label = "Wait for the panel that does not exist yet",
                Target = Css("div", "#late"),
                TimeoutMs = 15_000,
            },
            new Step
            {
                Id = "demo-slow-status",
                Action = StepAction.ExtractText,
                Label = "Read the status",
                Target = Css("div", "#status"),
                Outputs = [new OutputField { Name = "text", Description = "What the status says" }],
            },
            new Step
            {
                Id = "demo-slow-ready",
                Action = StepAction.Wait,
                Label = "Carry on once the status reads 'ready'",
                // A target on a wait means it WATCHES this element — reads it afresh every poll
                // rather than re-asking a question about the value the step above captured. That
                // captured value still says "working" and always will, so this wait can only pass
                // by going back to the page.
                Target = Css("div", "#status"),
                Outputs = [new OutputField { Name = "value", Description = "What the status said when the wait ended" }],
                Wait = new WaitSpec
                {
                    Mode = WaitMode.UntilCondition,
                    PollMs = 250,
                    TimeoutMs = 15_000,
                    Condition = new ConditionSpec
                    {
                        Left = StepText("demo-slow-ready", "this step's live reading", "value"),
                        Op = ConditionOp.Equals,
                        Right = Literal("ready"),
                    },
                },
            },
            new Step
            {
                Id = "demo-slow-assert",
                Action = StepAction.AssertElement,
                Label = "Confirm the late panel really arrived",
                Target = Css("div", "#late"),
                Value = "The late panel is here.",
            },
            new Step
            {
                Id = "demo-slow-record",
                Action = StepAction.WriteDataset,
                Label = "Write down both readings, which are not the same",
                // The evidence, and the reason this example writes a file at all: one column is what
                // the page said when it was read, the other is what the wait saw by watching. A run
                // where the wait had re-checked the captured value could not produce two different
                // words here — it could only time out.
                WriteDataset = new DatasetWriteSpec
                {
                    DatasetName = SlowReadingsDataset,
                    Format = "csv",
                    // One row per run, replacing the last one. The file is evidence about THIS
                    // run's two readings; a growing pile of identical pairs would say nothing more
                    // and would make the example non-repeatable, which the chain example — it runs
                    // this one again — would find immediately.
                    Append = false,
                    Columns = new Dictionary<string, BindingRef>
                    {
                        ["captured"] = StepText("demo-slow-status", "Read the status"),
                        ["watched"] = StepText("demo-slow-ready", "Carry on once the status reads 'ready'", "value"),
                    },
                },
            },
        ]);

    // ---- conditions ---------------------------------------------------------------------------

    /// <summary>
    /// Nine pre-flight checks over one order — one for every comparison the condition picker
    /// offers.
    /// <para>
    /// Each check that holds writes its own row to <see cref="OrderChecksDataset"/>, which is what
    /// makes the demo self-evidencing: nine rows means all nine branches were taken, and an
    /// acceptance check can say so without reading a log. A condition that quietly did not hold
    /// would otherwise look exactly like a task that passed.
    /// </para>
    /// </summary>
    private static DemoTask Order(string demoRoot) => new(
        "order",
        "Check an order before shipping",
        "Read five plain facts off an order, then ask nine questions about them — is the status "
        + "blank, does it say what it should, is there stock, is it flagged express, did the "
        + $"packer leave a note. Every check that holds writes a row to {OrderChecksDataset}, so "
        + "the dataset afterwards is the list of what passed.",
        PageUrl(demoRoot, "order.html"),
        [
            Read("status", "#status", "dd", "Read the status"),
            Read("stock", "#stock", "dd", "Read how many are in stock"),
            Read("express", "#express", "dd", "Read the express flag"),
            Read("fragile", "#fragile", "dd", "Read the fragile flag"),
            Read("note", "#note", "input", "Read the packer's note"),

            new Step
            {
                Id = "demo-order-checks",
                Action = StepAction.Group,
                Label = "Nine ways to ask a question",
                Children =
                [
                    Check("notempty", "There is a status at all", "status", ConditionOp.NotEmpty, null),
                    Check("equals", "The status is exactly 'Ready to ship'", "status", ConditionOp.Equals, "Ready to ship"),
                    Check("notequals", "The status is not 'Cancelled'", "status", ConditionOp.NotEquals, "Cancelled"),
                    Check("contains", "The status mentions shipping", "status", ConditionOp.Contains, "ship"),
                    Check("empty", "The packer left no note", "note", ConditionOp.Empty, null),
                    Check("greater", "There is at least one in stock", "stock", ConditionOp.GreaterThan, "0"),
                    Check("less", "Stock is below the 100 we would query", "stock", ConditionOp.LessThan, "100"),
                    Check("istrue", "It is flagged express", "express", ConditionOp.IsTrue, null),
                    Check("isfalse", "It is not flagged fragile", "fragile", ConditionOp.IsFalse, null),
                ],
            },
            new Step
            {
                Id = "demo-order-ship",
                Action = StepAction.Click,
                Label = "Ship it",
                Target = Css("button", "#ship"),
                IsCommitPoint = true,
            },
            new Step
            {
                Id = "demo-order-shipped",
                Action = StepAction.AssertElement,
                Label = "Confirm the desk says it shipped",
                Target = Css("div", "#shipped-notice"),
                Value = "shipped: order 4021",
            },
        ]);

    /// <summary>One ExtractText step over the order page, publishing its text for the checks.</summary>
    private static Step Read(string slug, string selector, string tag, string label) => new()
    {
        Id = $"demo-order-{slug}",
        Action = StepAction.ExtractText,
        Label = label,
        Target = Css(tag, selector),
        Outputs = [new OutputField { Name = "text", Description = label }],
    };

    /// <summary>
    /// One condition, and the row it writes when it holds. The right-hand side is a literal
    /// binding rather than a bare string because a condition compares two BOUND values — keeping
    /// the shape means switching the comparison to another step's output is a picker change, not
    /// a model change.
    /// </summary>
    private static Step Check(string slug, string label, string source, ConditionOp op, string? right) => new()
    {
        Id = $"demo-order-if-{slug}",
        Action = StepAction.If,
        Label = label,
        Condition = new ConditionSpec
        {
            Left = StepText($"demo-order-{source}", $"{source} → text"),
            Op = op,
            Right = right == null ? null : Literal(right),
        },
        Children =
        [
            new Step
            {
                Id = $"demo-order-log-{slug}",
                Action = StepAction.WriteDataset,
                Label = "Record that this check held",
                WriteDataset = new DatasetWriteSpec
                {
                    DatasetName = OrderChecksDataset,
                    Format = "csv",
                    Append = true,
                    // Nine writes, one dataset, one run: the first replaces and the rest add to it,
                    // so the file afterwards is this run's nine checks rather than every run's.
                    ResetOnFirstWrite = true,
                    Columns = new Dictionary<string, BindingRef>
                    {
                        ["check"] = Literal(slug),
                        ["value"] = StepText($"demo-order-{source}", $"{source} → text"),
                    },
                },
            },
        ],
    };

    // ---- zoom ---------------------------------------------------------------------------------

    /// <summary>
    /// The example for a site whose layout is wider than the window it is being driven in.
    /// <para>
    /// It proves the two things a zoom step has to be true for: the page really changed size, and
    /// a click still lands where it is aimed afterwards. Every check is made by pressing a button
    /// that asks the page to measure itself right then — a page in the headless browser is off-screen
    /// and therefore hidden, so anything driven by a timer, a frame or a resize would report its
    /// load-time answer forever and look like it was working.
    /// </para>
    /// </summary>
    private static DemoTask Zoom(string demoRoot) => new(
        "zoom",
        "See more of a page that is too wide",
        $"Some layouts do not fit the window they are driven in, and the thing you need is off the "
        + $"side of it. This asks the page whether a far-off button is reachable, zooms out to "
        + $"{DemoPages.ZoomedTo}%, asks again, clicks the button that was out of reach a moment "
        + "before, and puts the zoom back.",
        PageUrl(demoRoot, "zoom.html"),
        [
            Ask("demo-zoom-check-before", "Ask the page whether the far button is reachable"),
            new Step
            {
                Id = "demo-zoom-before",
                Action = StepAction.AssertElement,
                Label = "At normal size it is off the side of the window",
                Target = Css("p", "#reach"),
                Value = "out of reach",
            },
            new Step
            {
                Id = "demo-zoom-out",
                Action = StepAction.SetZoom,
                Label = $"Zoom out to {DemoPages.ZoomedTo}%",
                ZoomPercent = DemoPages.ZoomedTo,
            },
            Ask("demo-zoom-check-after", "Ask again, now the page is zoomed out"),
            new Step
            {
                Id = "demo-zoom-after",
                Action = StepAction.AssertElement,
                Label = "Now the whole width fits",
                Target = Css("p", "#reach"),
                Value = "reachable",
            },
            new Step
            {
                Id = "demo-zoom-click",
                Action = StepAction.Click,
                Label = "Click the button that was out of reach",
                Target = Css("button", "#far-button"),
            },
            new Step
            {
                Id = "demo-zoom-clicked",
                Action = StepAction.AssertElement,
                Label = "The click landed, and the page agrees it was on screen",
                Target = Css("div", "#zoom-clicked"),
                Value = "clicked at the far end, with the whole width on screen",
            },
            new Step
            {
                Id = "demo-zoom-back",
                Action = StepAction.SetZoom,
                Label = "Put the zoom back to normal",
                ZoomPercent = 100,
            },
            Ask("demo-zoom-check-restored", "Ask once more, back at normal size"),
            new Step
            {
                Id = "demo-zoom-restored",
                Action = StepAction.AssertElement,
                Label = "…and the far end is out of reach again",
                Target = Css("p", "#reach"),
                Value = "out of reach",
            },
        ]);

    /// <summary>The zoom example's Check button — the page measures itself only when asked.</summary>
    private static Step Ask(string id, string label) => new()
    {
        Id = id,
        Action = StepAction.Click,
        Label = label,
        Target = Css("button", "#check"),
    };

    // ---- adding it up -----------------------------------------------------------------------

    /// <summary>Dataset the invoice example harvests into, and the one it totals into.</summary>
    public const string InvoicesDataset = "invoices.csv";
    public const string InvoiceTotalsDataset = "invoice-totals.csv";

    /// <summary>
    /// Harvest three amounts and reduce them five ways.
    /// <para>
    /// The five answers are written to a dataset rather than only logged, because a number in a
    /// log is a number nobody can check. On disk it can be compared against the page it came from
    /// — which is exactly what the acceptance check does, and what makes this example evidence
    /// rather than a demonstration.
    /// </para>
    /// </summary>
    private static DemoTask Invoices(string demoRoot) => new(
        "invoices",
        "Add up what you collected",
        "Harvest three invoice amounts off a page, then work out the total, how many there were, "
        + "the smallest, the largest and the average — five steps, no arithmetic to write — and "
        + $"record all five as one row of {InvoiceTotalsDataset}.",
        PageUrl(demoRoot, "invoices.html"),
        [
            new Step
            {
                Id = "demo-invoices-harvest",
                Action = StepAction.ExtractAll,
                Label = "Harvest every invoice on the page",
                Harvest = new HarvestSpec
                {
                    ItemSelector = "table.invoices tbody > tr",
                    ExpectedCount = DemoPages.InvoiceAmounts.Length,
                    DatasetName = InvoicesDataset,
                    Format = "csv",
                    Append = false,
                    DedupeBy = "ref",
                    Fields =
                    [
                        new HarvestField { Name = "ref", Source = HarvestSource.Attribute, AttributeName = "data-ref" },
                        new HarvestField { Name = "amount", Selector = "td.amount", Source = HarvestSource.Text },
                    ],
                },
                Outputs = [new OutputField { Name = "count", Description = "How many invoices were harvested" }],
            },
            Reduce("total", AggregateOp.Sum, "Add the amounts up"),
            Reduce("count", AggregateOp.Count, "Count how many there were"),
            Reduce("smallest", AggregateOp.Min, "Find the smallest"),
            Reduce("largest", AggregateOp.Max, "Find the largest"),
            Reduce("average", AggregateOp.Average, "Work out the average"),
            new Step
            {
                Id = "demo-invoices-record",
                Action = StepAction.WriteDataset,
                Label = "Record all five",
                WriteDataset = new DatasetWriteSpec
                {
                    DatasetName = InvoiceTotalsDataset,
                    Format = "csv",
                    // One row describing one run, so it replaces rather than piling up.
                    Append = false,
                    Columns = new Dictionary<string, BindingRef>
                    {
                        ["total"] = Aggregated("total"),
                        ["count"] = Aggregated("count"),
                        ["smallest"] = Aggregated("smallest"),
                        ["largest"] = Aggregated("largest"),
                        ["average"] = Aggregated("average"),
                    },
                },
            },
        ]);

    /// <summary>One aggregate step over the harvested amounts.</summary>
    private static Step Reduce(string slug, AggregateOp op, string label) => new()
    {
        Id = $"demo-invoices-{slug}",
        Action = StepAction.Aggregate,
        Label = label,
        Aggregate = new AggregateSpec
        {
            DatasetName = InvoicesDataset,
            ColumnName = "amount",
            Op = op,
        },
        Outputs = [new OutputField { Name = "value", Description = label }],
    };

    /// <summary>A binding to what one of those aggregate steps worked out.</summary>
    private static BindingRef Aggregated(string slug) => new()
    {
        Kind = BindingKind.StepOutput,
        SourceStepId = $"demo-invoices-{slug}",
        OutputField = "value",
        Label = $"{slug} → value",
    };

    // ---- behind a boundary --------------------------------------------------------------------

    /// <summary>
    /// The example for the two places a selector run against the top document cannot see: an open
    /// shadow root and a same-origin iframe.
    /// <para>
    /// It clicks inside each and then asserts on what each one wrote INTO ITS OWN TREE. Asserting
    /// on something the outer page put up would have proved only that the click landed; asserting
    /// inside proves the resolver got back in there to read it.
    /// </para>
    /// </summary>
    private static DemoTask Shadow(string demoRoot) => new(
        "shadow",
        "Reach into a shadow root and a frame",
        "Component libraries put their controls inside shadow roots, and embedded pages put theirs "
        + "inside iframes; a selector run against the page itself sees neither. This clicks a "
        + "button in each and reads the answer back out of the same place it was written.",
        PageUrl(demoRoot, "shadow.html"),
        [
            new Step
            {
                Id = "demo-shadow-click",
                Action = StepAction.Click,
                Label = "Click the button inside the shadow root",
                Target = Css("button", "#in-shadow"),
            },
            new Step
            {
                Id = "demo-shadow-assert",
                Action = StepAction.AssertElement,
                Label = "Read what it wrote, inside that same shadow root",
                Target = Css("p", "#shadow-said"),
                Value = "the shadow root was clicked",
            },
            new Step
            {
                Id = "demo-shadow-frame-click",
                Action = StepAction.Click,
                Label = "Click the button inside the iframe",
                Target = Css("button", "#in-frame"),
            },
            new Step
            {
                Id = "demo-shadow-frame-assert",
                Action = StepAction.AssertElement,
                Label = "Read what it wrote, inside that same frame",
                Target = Css("p", "#frame-said"),
                Value = "the frame was clicked",
            },
            new Step
            {
                Id = "demo-shadow-attach",
                Action = StepAction.UploadFile,
                // The one action that did not go through the resolver, and therefore the one that
                // stopped at the first component library it met. It asks for the element the
                // resolver found rather than for a selector, so it now reaches wherever a resolve
                // reaches.
                Label = $"Attach {DemoPages.AttachmentFile} to the file input inside the shadow root",
                Target = Css("input", "#in-shadow-file"),
                Value = FilePath(demoRoot, DemoPages.AttachmentFile),
            },
            new Step
            {
                Id = "demo-shadow-harvest",
                Action = StepAction.ExtractAll,
                Label = "Harvest the list that lives inside the frame",
                Harvest = new HarvestSpec
                {
                    ItemSelector = "ul.framed-list > li.framed-row",
                    ExpectedCount = 3,
                    DatasetName = FramedRowsDataset,
                    Format = "csv",
                    Append = false,
                    Fields =
                    [
                        new HarvestField { Name = "ref", Source = HarvestSource.Attribute, AttributeName = "data-ref" },
                        new HarvestField { Name = "text", Source = HarvestSource.Text },
                    ],
                },
                Outputs = [new OutputField { Name = "count", Description = "How many rows the frame held" }],
            },
        ]);

    /// <summary>
    /// The example for the two boundaries that cannot be walked through at all: a CLOSED shadow
    /// root, and a CROSS-ORIGIN frame.
    /// <para>
    /// Its neighbour, <see cref="Shadow"/>, covers the pair a walk from the top document can still
    /// cross. These two cannot be crossed by any walk, and the difference is worth having as its own
    /// example rather than four more steps on that one: they are reached by a different mechanism,
    /// and when one of them breaks the other is very unlikely to be the cause.
    /// </para>
    /// <para>
    /// Same assertion discipline as there. Each click is answered INSIDE the tree it happened in, so
    /// reading the answer back is a second, independent proof that the resolver got in — a step that
    /// checked something on the outer page would pass on a click that landed anywhere at all.
    /// </para>
    /// </summary>
    private static DemoTask Closed(string demoRoot) => new(
        "closed",
        "Reach into a closed root and a cross-origin frame",
        "A closed shadow root hands its only reference to the component that made it, and a "
        + "cross-origin frame throws the moment anything reads it — neither can be found by "
        + "searching the page. This clicks a button in each and reads the answer back out of the "
        + "same place it was written.",
        PageUrl(demoRoot, "closed.html"),
        [
            new Step
            {
                Id = "demo-closed-click",
                Action = StepAction.Click,
                Label = "Click the button inside the closed shadow root",
                Target = Css("button", "#in-closed"),
            },
            new Step
            {
                Id = "demo-closed-assert",
                Action = StepAction.AssertElement,
                Label = "Read what it wrote, inside that same closed root",
                Target = Css("p", "#closed-said"),
                Value = "the closed root was clicked",
            },
            new Step
            {
                Id = "demo-closed-frame-click",
                Action = StepAction.Click,
                Label = "Click the button inside the cross-origin frame",
                Target = Css("button", "#in-opaque"),
            },
            new Step
            {
                Id = "demo-closed-frame-assert",
                Action = StepAction.AssertElement,
                Label = "Read what it wrote, inside that same frame",
                Target = Css("p", "#opaque-said"),
                Value = "the cross-origin frame was clicked",
            },
            new Step
            {
                Id = "demo-closed-attach",
                Action = StepAction.UploadFile,
                Label = $"Attach {DemoPages.AttachmentFile} to the file input inside the CLOSED root",
                Target = Css("input", "#in-closed-file"),
                Value = FilePath(demoRoot, DemoPages.AttachmentFile),
            },
            new Step
            {
                Id = "demo-closed-harvest",
                Action = StepAction.ExtractAll,
                // Nothing here can read that list. The copy of the harvester running inside the
                // frame can, and is asked to by name — which needs no eval, so a frame with a
                // strict Content-Security-Policy answers this even though it would refuse a
                // forwarded action.
                Label = "Harvest the list that lives across the origin boundary",
                Harvest = new HarvestSpec
                {
                    ItemSelector = "ul.opaque-list > li.opaque-row",
                    ExpectedCount = 2,
                    DatasetName = OpaqueRowsDataset,
                    Format = "csv",
                    Append = false,
                    Fields =
                    [
                        new HarvestField { Name = "ref", Source = HarvestSource.Attribute, AttributeName = "data-ref" },
                        new HarvestField { Name = "text", Source = HarvestSource.Text },
                    ],
                },
                Outputs = [new OutputField { Name = "count", Description = "How many rows were across the boundary" }],
            },
        ]);

    // ---- a list with gaps in it -----------------------------------------------------------------

    /// <summary>
    /// The example for the shape a JSON blob from somewhere else actually has: a list where not
    /// every row carries every field.
    /// <para>
    /// It is two guards, nested. The outer one is the everyday "only bother with rows that have a
    /// role at all"; the inner one branches on the missing name, and its <c>otherwise</c> belongs
    /// to it rather than to the outer guard — which is the rule the Gherkin round-trip has to get
    /// right, and the reason this example is nested rather than flat.
    /// </para>
    /// <para>
    /// <c>is not present</c> and not <c>is empty</c>: asking whether an absent value is empty FAILS
    /// the run, deliberately, because a column that is not there is nearly always a mis-typed
    /// column name. Presence is how you say you meant to ask.
    /// </para>
    /// </summary>
    private static DemoTask Roster(string demoRoot) => new(
        "roster",
        "Work through a list with gaps in it",
        $"Iterates {DemoPages.RosterDataset} — a list where {DemoPages.RosterNamed} of the "
        + $"{DemoPages.RosterNamed + DemoPages.RosterUnnamed} rows have a name and "
        + $"{DemoPages.RosterUnnamed} does not — and branches on it: type the name and add, or "
        + "skip. This is what a JSON blob from a previous task looks like when you feed it to the "
        + "next one.",
        PageUrl(demoRoot, "roster.html"),
        [
            new Step
            {
                Id = "demo-roster-loop",
                Action = StepAction.ForEach,
                Label = "For every row of the list",
                ForEach = new ForEachSpec
                {
                    Source = new BindingRef
                    {
                        Kind = BindingKind.DatasetRow,
                        DatasetName = DemoPages.RosterDataset,
                        Label = DemoPages.RosterDataset,
                    },
                    RowVariableName = "row",
                },
                Children =
                [
                    new Step
                    {
                        Id = "demo-roster-has-role",
                        Action = StepAction.If,
                        Label = "Only rows that have a role at all",
                        Condition = new ConditionSpec { Left = Column("Role"), Op = ConditionOp.Exists },
                        Children =
                        [
                            new Step
                            {
                                Id = "demo-roster-no-name",
                                Action = StepAction.If,
                                Label = "When the row has no name",
                                Condition = new ConditionSpec { Left = Column("Name"), Op = ConditionOp.NotExists },
                                Children =
                                [
                                    // Absence would be the ordinary case for a search that comes
                                    // back empty, not a broken run — this is what a run branches on
                                    // to decide "skip this row" instead of aborting outright when
                                    // WaitForElement or AssertElement would fail it.
                                    new Step
                                    {
                                        Id = "demo-roster-check-skip",
                                        Action = StepAction.CheckElement,
                                        Label = "Check whether the skip button is there",
                                        Target = Css("button", "#skip"),
                                        Outputs = [new OutputField
                                        {
                                            Name = "present",
                                            Description = "Whether the skip button resolved on this row",
                                        }],
                                    },
                                    new Step
                                    {
                                        Id = "demo-roster-skip",
                                        Action = StepAction.Click,
                                        Label = "Skip it",
                                        Target = Css("button", "#skip"),
                                    },
                                ],
                            },
                            new Step
                            {
                                Id = "demo-roster-otherwise",
                                Action = StepAction.Else,
                                Label = "Otherwise",
                                // Named, not merely adjacent: if the guard above it were ever
                                // deleted this branch would otherwise attach itself to whatever
                                // condition ended up in front of it, and run the wrong half.
                                PairedIfId = "demo-roster-no-name",
                                Children =
                                [
                                    new Step
                                    {
                                        Id = "demo-roster-type",
                                        Action = StepAction.SetValue,
                                        Label = "Put the name in the box",
                                        Target = Css("input", "#txtName"),
                                        Bindings = new Dictionary<string, BindingRef>
                                        {
                                            ["Value"] = Column("Name"),
                                        },
                                    },
                                    new Step
                                    {
                                        Id = "demo-roster-add",
                                        Action = StepAction.Click,
                                        Label = "Add them",
                                        Target = Css("button", "#add"),
                                    },
                                    // The two things a loop knows that its columns do not: where
                                    // in the list this row sat, and what the row was. Written side
                                    // by side with the name so a result can be traced back to the
                                    // line of input it came from — which is the whole reason to
                                    // want either of them.
                                    new Step
                                    {
                                        Id = "demo-roster-record",
                                        Action = StepAction.WriteDataset,
                                        Label = "Record who was added, and where they came from",
                                        WriteDataset = new DatasetWriteSpec
                                        {
                                            DatasetName = RosterAddedDataset,
                                            Format = "csv",
                                            Append = true,
                                            ResetOnFirstWrite = true,
                                            Columns = new Dictionary<string, BindingRef>
                                            {
                                                ["position"] = RowNumber(),
                                                ["name"] = Column("Name"),
                                                // A field INSIDE the row's Contact object, named
                                                // the way a picker offers it. Nothing about the
                                                // binding says it is nested; that is the point.
                                                ["email"] = Column(DemoPages.RosterEmailColumn),
                                                ["source"] = WholeRow(DemoPages.RosterDataset),
                                            },
                                        },
                                    },
                                ],
                            },
                        ],
                    },
                ],
            },
            new Step
            {
                Id = "demo-roster-tally",
                Action = StepAction.AssertElement,
                Label = "Confirm both branches were taken the right number of times",
                Target = Css("p", "#tally"),
                Value = $"added {DemoPages.RosterNamed}, skipped {DemoPages.RosterUnnamed}",
            },
        ]);

    /// <summary>A binding to one column of the row an enclosing for-each is on.</summary>
    private static BindingRef Column(string name) => new()
    {
        Kind = BindingKind.DatasetColumn,
        ColumnName = name,
        Label = "row." + name,
    };

    /// <summary>A binding to the row's position in the list, counting from 1. A column like any
    /// other as far as a binding is concerned — the loop is what publishes it.</summary>
    private static BindingRef RowNumber() => Column(ForEachSpec.RowNumberKey);

    /// <summary>A binding to the whole current row, as one line of JSON.</summary>
    private static BindingRef WholeRow(string datasetName) => new()
    {
        Kind = BindingKind.DatasetRow,
        DatasetName = datasetName,
        Label = "the whole row",
    };

    // ---- a task run more than one way -----------------------------------------------------------

    /// <summary>
    /// The example that takes a value from whoever runs it.
    /// <para>
    /// This is what "the same task for a different search term" looks like without templating: the
    /// task DECLARES an input, the steps bind to it through the picker, and the value arrives from
    /// a caller, from <c>--input</c>, or from the default. Nothing is typed as a placeholder, so
    /// nothing can be misspelled into a literal that silently stays literal.
    /// </para>
    /// </summary>
    private static DemoTask Search(string demoRoot) => new(
        "search",
        "Search for a word you choose",
        $"Types whatever it is given into a search box and confirms the page saw it. Run on its own "
        + $"it searches for \"{DefaultSearchTerm}\"; another task can call it with something else, "
        + $"and so can the command line with --input term=…",
        PageUrl(demoRoot, "form.html"),
        [
            new Step
            {
                Id = "demo-search-type",
                Action = StepAction.TypeText,
                Label = "Type the term this task was given",
                Target = Css("input", "#search"),
                // The literal is kept beside the binding rather than cleared: unbinding a field
                // should give back what was there, not an empty box.
                Value = DefaultSearchTerm,
                Bindings = new Dictionary<string, BindingRef>
                {
                    ["Value"] = Input(SearchTermInput),
                },
            },
            new Step
            {
                Id = "demo-search-enter",
                Action = StepAction.PressEnter,
                Label = "Press Enter in the field that has focus",
            },
            new Step
            {
                Id = "demo-search-assert",
                Action = StepAction.AssertElement,
                Label = "Confirm the page searched for it",
                Target = Css("p", "#search-echo"),
                Value = $"searched: {DefaultSearchTerm}",
                // Prefix + one reference is the whole of composition here, and it is enough:
                // "searched: " and the value. Anything more belongs to the authoring layer.
                Bindings = new Dictionary<string, BindingRef>
                {
                    ["Value"] = Input(SearchTermInput, prefix: "searched: "),
                },
            },
        ],
        Inputs:
        [
            new TaskInput
            {
                Name = SearchTermInput,
                Description = "What to search for",
                Default = DefaultSearchTerm,
            },
        ]);

    /// <summary>The one input the search example declares, and what it searches for by default.</summary>
    public const string SearchTermInput = "term";
    public const string DefaultSearchTerm = "wolf";

    /// <summary>The term the chain example hands it instead, to show a value being passed.</summary>
    public const string ChainedSearchTerm = "badger";

    /// <summary>A binding to one of the task's own declared inputs.</summary>
    private static BindingRef Input(string name, string? prefix = null) => new()
    {
        Kind = BindingKind.TaskInput,
        ParameterName = name,
        Prefix = prefix,
        Label = "input: " + name,
    };

    // ---- three tasks that hand values along ------------------------------------------------------

    /// <summary>
    /// The first of three tasks that only mean something in the order they are written: find a
    /// ticket, look it up, write down what was found.
    /// <para>
    /// This is what a COLLECTION is for. Each task is worth running on its own, each is short
    /// enough to fix without re-recording the others, and the collection is what makes them one
    /// job — the run walks them in order on one browser, and what each task DECLARES as an output
    /// is offered to the ones after it.
    /// </para>
    /// <para>
    /// The declaration is the whole point. This task publishes <c>ticketId</c> and says which step
    /// produces it; the next task's input names <c>ticketId</c>, not a step id inside here. So
    /// re-recording this task's steps cannot silently change what the next one receives — only
    /// changing the published name can, and that is a rename you can see.
    /// </para>
    /// </summary>
    private static DemoTask PipelineFind(string demoRoot) => new(
        "pipeline-find",
        "Pipeline 1 — find the next ticket",
        "Reads the id of the ticket that is next in the queue and publishes it for the tasks after "
        + "it. Run the Demos collection and the next two tasks use what this one found; run it on "
        + "its own and it simply reports the id.",
        PageUrl(demoRoot, "pipeline/queue.html"),
        [
            new Step
            {
                Id = "demo-pipeline-read-id",
                Action = StepAction.ExtractText,
                Label = "Read the id of the ticket that is next",
                Target = Css("dd", "#next-ticket"),
                Outputs = [new OutputField { Name = "text", Description = "The ticket id as shown" }],
            },
        ],
        Outputs:
        [
            new TaskOutput
            {
                Name = TicketIdValue,
                Description = "The ticket the tasks after this one should work on",
                SourceStepId = "demo-pipeline-read-id",
            },
        ]);

    /// <summary>
    /// The middle task: it takes a ticket id from whoever runs it, looks that ticket up on a
    /// DIFFERENT page, and publishes what the desk says about it.
    /// <para>
    /// A different page on purpose. If this task could have read the owner off the queue page it
    /// would prove nothing — the id has to travel out of the page it was found on for the carrying
    /// to be real rather than a second reading of the same markup.
    /// </para>
    /// </summary>
    private static DemoTask PipelineLookUp(string demoRoot) => new(
        "pipeline-look-up",
        "Pipeline 2 — look that ticket up",
        "Types the ticket id it was given into the ticket desk and reads back who owns it and how "
        + "urgent it is. In the collection the id comes from 'Pipeline 1'; on its own it falls "
        + $"back to {FallbackTicketId}, which is a real ticket — a wired task still has to work "
        + "when nothing wires it.",
        PageUrl(demoRoot, "pipeline/ticket.html"),
        [
            new Step
            {
                Id = "demo-pipeline-type-id",
                Action = StepAction.TypeText,
                Label = "Type the ticket id this task was given",
                Target = Css("input", "#ticket-id"),
                Value = FallbackTicketId,
                Bindings = new Dictionary<string, BindingRef>
                {
                    ["Value"] = Input(TicketIdValue),
                },
            },
            new Step
            {
                Id = "demo-pipeline-look-up",
                Action = StepAction.Click,
                Label = "Press Look up",
                Target = Css("button", "#look-up"),
            },
            new Step
            {
                Id = "demo-pipeline-assert-found",
                Action = StepAction.AssertElement,
                Label = "Confirm the desk found that exact ticket",
                Target = Css("p", "#found"),
                Value = $"found: {FallbackTicketId}",
                // Bound to the same input the typing was: it is what turns "a ticket was found"
                // into "the ticket we were handed was found", which is the only version of this
                // check worth having in a pipeline.
                Bindings = new Dictionary<string, BindingRef>
                {
                    ["Value"] = Input(TicketIdValue, prefix: "found: "),
                },
            },
            new Step
            {
                Id = "demo-pipeline-read-owner",
                Action = StepAction.ExtractText,
                Label = "Read who owns it",
                Target = Css("dd", "#owner"),
                Outputs = [new OutputField { Name = "text", Description = "The owner as shown" }],
            },
            new Step
            {
                Id = "demo-pipeline-read-priority",
                Action = StepAction.ExtractText,
                Label = "Read how urgent it is",
                Target = Css("dd", "#priority"),
                Outputs = [new OutputField { Name = "text", Description = "The priority as shown" }],
            },
        ],
        Inputs:
        [
            new TaskInput
            {
                Name = TicketIdValue,
                Description = "Which ticket to look up",
                Default = FallbackTicketId,
                From = new TaskOutputRef
                {
                    TaskId = DemoTask.TaskIdFor("pipeline-find"),
                    TaskName = "Pipeline 1 — find the next ticket",
                    OutputName = TicketIdValue,
                },
            },
        ],
        Outputs:
        [
            new TaskOutput
            {
                Name = TicketOwnerValue,
                Description = "Who the desk says owns the ticket",
                SourceStepId = "demo-pipeline-read-owner",
            },
            new TaskOutput
            {
                Name = TicketPriorityValue,
                Description = "How urgent the desk says it is",
                SourceStepId = "demo-pipeline-read-priority",
            },
        ]);

    /// <summary>
    /// The last of the three: it touches no page at all, and writes down what the two before it
    /// found.
    /// <para>
    /// Three values from two different tasks, in one row. That is the shape a pipeline is FOR —
    /// and it is why the values are declared rather than shared: this task names what it needs, so
    /// reading it tells you what it depends on without opening either of the others.
    /// </para>
    /// </summary>
    private static DemoTask PipelineRecord(string demoRoot) => new(
        "pipeline-record",
        "Pipeline 3 — write down what we found",
        "Writes one row holding the ticket the first task found and what the second task learned "
        + "about it. It opens no page: everything it records was carried here by the collection.",
        null,
        [
            new Step
            {
                Id = "demo-pipeline-record",
                Action = StepAction.WriteDataset,
                Label = "Record the ticket, its owner and its priority",
                WriteDataset = new DatasetWriteSpec
                {
                    DatasetName = PipelineDataset,
                    Format = "csv",
                    Append = true,
                    // One row per run, replacing the last: this is a report of what the collection
                    // just found, not a history of every time it ran.
                    ResetOnFirstWrite = true,
                    Columns = new Dictionary<string, BindingRef>
                    {
                        ["ticket"] = Input(TicketIdValue),
                        ["owner"] = Input(TicketOwnerValue),
                        ["priority"] = Input(TicketPriorityValue),
                    },
                },
            },
        ],
        Inputs:
        [
            new TaskInput
            {
                Name = TicketIdValue,
                Description = "The ticket the first task found",
                Default = FallbackTicketId,
                From = new TaskOutputRef
                {
                    TaskId = DemoTask.TaskIdFor("pipeline-find"),
                    TaskName = "Pipeline 1 — find the next ticket",
                    OutputName = TicketIdValue,
                },
            },
            new TaskInput
            {
                Name = TicketOwnerValue,
                Description = "Who the second task found owns it",
                Default = "(nobody looked it up)",
                From = new TaskOutputRef
                {
                    TaskId = DemoTask.TaskIdFor("pipeline-look-up"),
                    TaskName = "Pipeline 2 — look that ticket up",
                    OutputName = TicketOwnerValue,
                },
            },
            new TaskInput
            {
                Name = TicketPriorityValue,
                Description = "How urgent the second task found it is",
                Default = "(nobody looked it up)",
                From = new TaskOutputRef
                {
                    TaskId = DemoTask.TaskIdFor("pipeline-look-up"),
                    TaskName = "Pipeline 2 — look that ticket up",
                    OutputName = TicketPriorityValue,
                },
            },
        ]);

    // ---- one task calling another ---------------------------------------------------------------

    /// <summary>
    /// A task made of other tasks.
    /// <para>
    /// The Navigate step between the two calls is not decoration: a called task runs its steps on
    /// whatever page is already open — its own start URL belongs to running it directly — so the
    /// caller is the one that decides where. Showing that plainly beats letting it be discovered.
    /// </para>
    /// </summary>
    private static DemoTask Chain(string demoRoot) => new(
        "chain",
        "Run three other examples",
        "A task can call another task, which is how one long recording becomes several short ones "
        + "that can be fixed independently. This runs 'Click a button', then 'Wait for a page that "
        + "is not ready', then 'Search for a word you choose' — handing that last one a term of "
        + $"its own (\"{ChainedSearchTerm}\") rather than letting it use its default. A called task "
        + "starts on whatever page the caller left open, so this opens the right page before the "
        + "second one — and shows the other way round for the third, which is told to open its own "
        + "start page first.",
        PageUrl(demoRoot, "buttons.html"),
        [
            new Step
            {
                Id = "demo-chain-buttons",
                Action = StepAction.RunTask,
                Label = "Run 'Click a button'",
                RunTaskId = DemoTask.TaskIdFor("buttons"),
            },
            new Step
            {
                Id = "demo-chain-open-slow",
                Action = StepAction.Navigate,
                Label = "Open the slow page for the next one",
                Url = PageUrl(demoRoot, "slow.html"),
            },
            new Step
            {
                Id = "demo-chain-slow",
                Action = StepAction.RunTask,
                Label = "Run 'Wait for a page that is not ready'",
                RunTaskId = DemoTask.TaskIdFor("slow"),
            },
            // No Navigate in front of this one, on purpose: it is told to open the called task's
            // own start page instead. The two calls above show the default rule; this shows the
            // choice, and the step says which it is rather than leaving it to be inferred.
            new Step
            {
                Id = "demo-chain-search",
                Action = StepAction.RunTask,
                Label = $"Run the search from its own page, for \"{ChainedSearchTerm}\"",
                RunTaskId = DemoTask.TaskIdFor("search"),
                RunTaskOpensStartUrl = true,
                RunTaskInputs = new Dictionary<string, BindingRef>
                {
                    [SearchTermInput] = new()
                    {
                        Kind = BindingKind.Literal,
                        Literal = ChainedSearchTerm,
                        Label = $"\"{ChainedSearchTerm}\"",
                    },
                },
            },
            new Step
            {
                Id = "demo-chain-searched",
                Action = StepAction.AssertElement,
                Label = "Confirm it searched for what it was handed, not its default",
                Target = Css("p", "#search-echo"),
                Value = $"searched: {ChainedSearchTerm}",
            },
        ]);

    // ---- parking ---------------------------------------------------------------------------------

    /// <summary>
    /// The one demo that deliberately does not finish while you watch it.
    /// <para>
    /// A wait long enough to be worth parking checkpoints the run and hands the browser back, and
    /// a later scheduler tick picks it up. There is no way to demonstrate that in two seconds —
    /// the whole property being shown is that hours can pass with nothing held open.
    /// </para>
    /// </summary>
    private static DemoTask Park(string demoRoot) => new(
        "park",
        "Start at a set time",
        $"Waits until {ParkTimeOfDay:HH\\:mm} and then does its work. Running it parks the run — "
        + "the browser is released straight away and a later scheduler tick carries on from the "
        + "step after the wait, which is what lets an overnight job cost nothing all day. It will "
        + "sit under 'Parked' in status until then; cancel it there if you only wanted to look.",
        PageUrl(demoRoot, "buttons.html"),
        [
            new Step
            {
                Id = "demo-park-wait",
                Action = StepAction.Wait,
                Label = $"Wait until {ParkTimeOfDay:HH\\:mm}",
                Wait = new WaitSpec { Mode = WaitMode.UntilTimeOfDay, TimeOfDay = ParkTimeOfDay },
            },
            new Step
            {
                Id = "demo-park-click",
                Action = StepAction.Click,
                Label = "Click 'Beta'",
                Target = Css("button", "#beta"),
            },
            new Step
            {
                Id = "demo-park-assert",
                Action = StepAction.AssertElement,
                Label = "Confirm the page says 'clicked: beta'",
                Target = Css("div", ".clicked"),
                Value = "clicked: beta",
            },
        ]);

    // ---- the shop ---------------------------------------------------------------------------------

    /// <summary>
    /// The demo that ties the whole data model together: harvest a list off one page, then visit
    /// every row of it and collect a value from each.
    /// <para>
    /// One row at a time, on the one browser the run holds. The loop is worth writing precisely
    /// because each row leaves the browser somewhere the next row starts from — twenty-four
    /// product pages visited in order, each price recorded against the SKU that led to it.
    /// </para>
    /// </summary>
    private static DemoTask ShopPrices(string demoRoot)
    {
        const string prefix = "demo-shop";
        var extractId = $"{prefix}-price";

        return new DemoTask(
            "shop-prices",
            "Shop prices — every product in turn",
            "Harvest every product on a results page, then visit each product page in turn and "
                + "collect its price. This is the whole input/output loop in one task.",
            PageUrl(demoRoot, "shop/search.html"),
            [
                new Step
                {
                    Id = $"{prefix}-harvest",
                    Action = StepAction.ExtractAll,
                    Label = $"Harvest all {DemoPages.ProductCount} products on this page",
                    Harvest = new HarvestSpec
                    {
                        ItemSelector = "ul.results > li.product",
                        ExpectedCount = DemoPages.ProductCount,
                        DatasetName = ProductsDataset,
                        Format = "csv",
                        Append = false,
                        DedupeBy = "sku",
                        Fields =
                        [
                            new HarvestField { Name = "sku", Source = HarvestSource.Attribute, AttributeName = "data-sku" },
                            new HarvestField { Name = "title", Selector = "a.title", Source = HarvestSource.Text },
                            new HarvestField { Name = "url", Selector = "a.title", Source = HarvestSource.Href },
                        ],
                    },
                    Outputs = [new OutputField { Name = "count", Description = "How many products were harvested" }],
                },
                new Step
                {
                    Id = $"{prefix}-loop",
                    Action = StepAction.ForEach,
                    Label = "For every product — one at a time",
                    ForEach = new ForEachSpec
                    {
                        Source = new BindingRef
                        {
                            Kind = BindingKind.DatasetRow,
                            DatasetName = ProductsDataset,
                            Label = ProductsDataset,
                        },
                        RowVariableName = "row",
                    },
                    Children =
                    [
                        new Step
                        {
                            Id = $"{prefix}-open",
                            Action = StepAction.Navigate,
                            Label = "Open this product's page",
                            Url = PageUrl(demoRoot, "shop/search.html"),
                            Bindings = new Dictionary<string, BindingRef>
                            {
                                ["Url"] = new()
                                {
                                    Kind = BindingKind.DatasetColumn,
                                    ColumnName = "url",
                                    Label = "row.url",
                                },
                            },
                        },
                        new Step
                        {
                            Id = extractId,
                            Action = StepAction.ExtractText,
                            Label = "Read the price",
                            Target = Css("div", ".price"),
                            Outputs =
                            [
                                new OutputField { Name = "text", Description = "The price as shown on the page" },
                            ],
                        },
                        new Step
                        {
                            Id = $"{prefix}-record",
                            Action = StepAction.WriteDataset,
                            Label = "Record the price against its SKU",
                            WriteDataset = new DatasetWriteSpec
                            {
                                DatasetName = PricesDataset,
                                Format = "csv",
                                Append = true,
                                // Every row appends to the same file, so the loop has to say which
                                // write starts it — otherwise a second run of the example reports
                                // twenty-four products and double the money.
                                ResetOnFirstWrite = true,
                                Columns = new Dictionary<string, BindingRef>
                                {
                                    ["sku"] = new()
                                    {
                                        Kind = BindingKind.DatasetColumn,
                                        ColumnName = "sku",
                                        Label = "row.sku",
                                    },
                                    ["price"] = StepText(extractId, "Read the price → text"),
                                },
                            },
                        },
                    ],
                },
            ]);
    }

    // ---- shorthand ---------------------------------------------------------------------------------

    /// <summary>
    /// A fingerprint that resolves by CSS selector. Demos are generated against pages this repo
    /// also generates, so the selector is known-good — there is nothing to record and nothing to
    /// self-heal from.
    /// </summary>
    private static ElementFingerprint Css(string tag, string selector) => new()
    {
        Tag = tag,
        CssSelector = selector,
    };

    /// <summary>A binding to the "text" output of an earlier step.</summary>
    private static BindingRef StepText(string stepId, string label, string field = "text") => new()
    {
        Kind = BindingKind.StepOutput,
        SourceStepId = stepId,
        OutputField = field,
        Label = label,
    };

    private static BindingRef Literal(string value) => new()
    {
        Kind = BindingKind.Literal,
        Literal = value,
        Label = $"\"{value}\"",
    };
}
