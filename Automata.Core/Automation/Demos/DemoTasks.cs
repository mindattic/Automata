using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Demos;

/// <summary>A factory demo task, identified by a stable key rather than by its display name.</summary>
public sealed record DemoTask(
    string Key,
    string Name,
    string Description,
    string? StartUrl,
    List<Step> Steps,
    EngineSettingsOverride? Settings = null);

/// <summary>
/// The demo tasks the generator seeds, built against the pages in <see cref="DemoPages"/>.
/// <para>
/// Written as records here rather than recorded by hand so they can be regenerated: a demo that
/// only exists as a file someone once saved goes stale the moment the model gains a field, and
/// then the one thing a new user runs first is the one thing that is out of date.
/// </para>
/// <para>
/// <b>Every step id here is fixed, not generated.</b> A step id is what a binding points at, so
/// regenerating with fresh ids would either break every binding or make every demo look
/// hand-edited to the seeder. Fixed ids mean a regenerated demo is byte-for-byte the demo it
/// replaces.
/// </para>
/// <para>
/// URLs are absolute <c>file://</c> paths baked in at generation time, which is why regenerating
/// is the documented fix if the workspace folder ever moves.
/// </para>
/// </summary>
public static class DemoTasks
{
    public const string CollectionName = "Demos";

    /// <summary>Datasets the shop examples write.</summary>
    public const string ProductsDataset = "shop-products.csv";
    public const string SequentialPricesDataset = "shop-prices-sequential.csv";
    public const string ParallelPricesDataset = "shop-prices-parallel.csv";

    /// <summary>Lanes the parallel variant asks for. Four is enough to expose an ordering or
    /// locking problem while still fitting comfortably on one machine.</summary>
    public const int ParallelLanes = 4;

    public static IReadOnlyList<DemoTask> All(string demoRoot) =>
    [
        Buttons(demoRoot),
        ShopPrices(demoRoot, parallel: false),
        ShopPrices(demoRoot, parallel: true),
    ];

    /// <summary>A <c>file://</c> URL for a page under the demo root.</summary>
    public static string PageUrl(string demoRoot, string relativePath) =>
        new Uri(Path.Combine(demoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))).AbsoluteUri;

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
                                Columns = new Dictionary<string, BindingRef>
                                {
                                    ["sku"] = new()
                                    {
                                        Kind = BindingKind.DatasetColumn,
                                        ColumnName = "sku",
                                        Label = "row.sku",
                                    },
                                    ["price"] = new()
                                    {
                                        Kind = BindingKind.StepOutput,
                                        SourceStepId = extractId,
                                        OutputField = "text",
                                        Label = "Read the price → text",
                                    },
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
}
