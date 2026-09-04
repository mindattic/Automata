using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Demos;

/// <summary>A factory demo task, identified by a stable key rather than by its display name.</summary>
public sealed record DemoTask(
    string Key,
    string Name,
    string Description,
    string? StartUrl,
    List<Step> Steps,
    EngineSettingsOverride? Settings = null)
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
    public const string SequentialPricesDataset = "shop-prices-sequential.csv";
    public const string ParallelPricesDataset = "shop-prices-parallel.csv";

    /// <summary>Dataset the order example writes one row to per check that held.</summary>
    public const string OrderChecksDataset = "order-checks.csv";

    /// <summary>Lanes the parallel variant asks for. Four is enough to expose an ordering or
    /// locking problem while still fitting comfortably on one machine.</summary>
    public const int ParallelLanes = 4;

    /// <summary>
    /// The time of day the parking example waits for. Any fixed time works — the point is that it
    /// is far enough away to be worth releasing the browser for, which is what parking means.
    /// </summary>
    public static readonly TimeOnly ParkTimeOfDay = new(9, 0);

    public static IReadOnlyList<DemoTask> All(string demoRoot) =>
    [
        Buttons(demoRoot),
        Form(demoRoot),
        Slow(demoRoot),
        Order(demoRoot),
        Zoom(demoRoot),
        Chain(demoRoot),
        ShopPrices(demoRoot, parallel: false),
        ShopPrices(demoRoot, parallel: true),
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
                Wait = new WaitSpec
                {
                    Mode = WaitMode.UntilCondition,
                    PollMs = 250,
                    TimeoutMs = 5_000,
                    Condition = new ConditionSpec
                    {
                        Left = StepText("demo-slow-status", "Read the status"),
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
    /// that asks the page to measure itself right then — a page in a browser lane is off-screen
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
        "Run two other examples",
        "A task can call another task, which is how one long recording becomes several short ones "
        + "that can be fixed independently. This runs 'Click a button' and then 'Wait for a page "
        + "that is not ready' — opening the right page before each, because a called task starts "
        + "on whatever page the caller left open.",
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
    /// The sequential and parallel variants differ ONLY in <see cref="ForEachSpec.MaxConcurrency"/>
    /// and in which dataset they write, which is the point — running both and comparing the totals
    /// is how you find out whether raising the concurrency of a working loop changed its results.
    /// </para>
    /// </summary>
    private static DemoTask ShopPrices(string demoRoot, bool parallel)
    {
        var prefix = parallel ? "demo-shop-par" : "demo-shop-seq";
        var extractId = $"{prefix}-price";

        return new DemoTask(
            parallel ? "shop-prices-parallel" : "shop-prices-sequential",
            parallel ? "Shop prices — several at once" : "Shop prices — one at a time",
            parallel
                ? $"The same job as 'one at a time', with {ParallelLanes} browser lanes running at once. "
                + "The totals from the two must match; if they do not, concurrency changed the answer."
                : "Harvest every product on a results page, then visit each product page in turn and "
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
                    Label = parallel
                        ? $"For every product — {ParallelLanes} at a time"
                        : "For every product — one at a time",
                    ForEach = new ForEachSpec
                    {
                        Source = new BindingRef
                        {
                            Kind = BindingKind.DatasetRow,
                            DatasetName = ProductsDataset,
                            Label = ProductsDataset,
                        },
                        RowVariableName = "row",
                        MaxConcurrency = parallel ? ParallelLanes : 1,
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
                                DatasetName = parallel ? ParallelPricesDataset : SequentialPricesDataset,
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
            ],
            // A ForEach may only ask for concurrency; the resolved ceiling grants it. Without this
            // override the parallel demo runs one row at a time on a default install and
            // demonstrates nothing — the engine says so plainly, but a demo should not need the
            // user to go and change a global setting before it does what its name says.
            parallel ? new EngineSettingsOverride { MaxConcurrency = ParallelLanes } : null);
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
    private static BindingRef StepText(string stepId, string label) => new()
    {
        Kind = BindingKind.StepOutput,
        SourceStepId = stepId,
        OutputField = "text",
        Label = label,
    };

    private static BindingRef Literal(string value) => new()
    {
        Kind = BindingKind.Literal,
        Literal = value,
        Label = $"\"{value}\"",
    };
}
