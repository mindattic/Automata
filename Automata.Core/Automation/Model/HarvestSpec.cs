namespace Automata.Core.Automation.Model;

/// <summary>Where one harvested field's value comes from, within the row element that holds it.</summary>
public enum HarvestSource
{
    /// <summary>The element's visible text.</summary>
    Text,

    /// <summary>A named attribute, verbatim.</summary>
    Attribute,

    /// <summary>
    /// A link's destination, resolved to an absolute URL. Distinct from
    /// <see cref="Attribute"/> on "href" on purpose: the attribute is usually a relative path,
    /// and a relative path is useless to a later Navigate step.
    /// </summary>
    Href,
}

/// <summary>
/// One column of a harvest — a value read out of every matched row element.
/// <para>
/// <see cref="Selector"/> is relative to the row element, not to the document, which is what makes
/// a harvest a harvest: "the price inside each tile", never "the 4th price on the page".
/// </para>
/// </summary>
public sealed class HarvestField
{
    /// <summary>Becomes the dataset column name, and what an enclosing ForEach binds to.</summary>
    public string Name { get; set; } = "";

    /// <summary>CSS selector relative to the row element. Null/blank reads the row element itself.</summary>
    public string? Selector { get; set; }

    public HarvestSource Source { get; set; } = HarvestSource.Text;

    /// <summary>Used when <see cref="Source"/> is <see cref="HarvestSource.Attribute"/>.</summary>
    public string? AttributeName { get; set; }
}

/// <summary>
/// Reads many rows off the current page in one pass and writes them to a dataset.
/// <para>
/// This is the step that closes the loop between browsing and iterating: everything else in the
/// data model can already fan out over a dataset and write results back, but until now a dataset
/// could only come from a file a human put there. A harvest produces one from the page in front of
/// it.
/// </para>
/// <para>
/// <b>It writes a dataset rather than an in-memory list, deliberately.</b> A file survives parking
/// with no serialization of engine internals, it can be opened in Explorer and checked before the
/// loop that consumes it ever runs, and it reaches ForEach through exactly the same door a
/// hand-dropped CSV does — so looping, conditions, parallel lanes and the Data tab all keep
/// working with no new machinery.
/// </para>
/// </summary>
public sealed class HarvestSpec
{
    /// <summary>
    /// CSS selector for the repeating row element, computed by the picker from one example the
    /// user clicked — never hand-typed. The picker generalises away the clicked element's own id
    /// and text, because those are what make it one tile instead of all of them.
    /// </summary>
    public string ItemSelector { get; set; } = "";

    /// <summary>What the picker matched when the selector was built, so a later run can say
    /// "expected about 24, found 3" instead of just handing back three rows.</summary>
    public int? ExpectedCount { get; set; }

    public List<HarvestField> Fields { get; set; } = [];

    /// <summary>Dataset to write. A file name, as everywhere else — "products.csv".</summary>
    public string DatasetName { get; set; } = "";

    /// <summary>"csv" or "json".</summary>
    public string Format { get; set; } = "csv";

    /// <summary>
    /// Append to the dataset rather than replacing it. Defaults to FALSE: a harvest re-reads the
    /// same page section, so appending by default would silently double every row on a second run.
    /// </summary>
    public bool Append { get; set; }

    /// <summary>
    /// Drop rows whose value in this column has already been seen. Blank keeps every row.
    /// The usual use is a product id, where the same item appears in both a carousel and the grid.
    /// </summary>
    public string? DedupeBy { get; set; }

    /// <summary>Stop after this many rows (after de-duplication). Null takes everything.</summary>
    public int? MaxRows { get; set; }
}
