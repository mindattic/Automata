namespace Automata.Core.Automation.Demos;

/// <summary>One generated demo page: a file name under the demo root, and its whole content.</summary>
public sealed record DemoPage(string RelativePath, string Html);

/// <summary>
/// The HTML the demo generator writes to disk.
/// <para>
/// Local pages rather than live sites, on purpose. A demo whose job is to prove that harvesting,
/// looping and parallel lanes work cannot also be a bet on someone else's markup, consent banner
/// and rate limiter — when a demo like that fails, the user learns nothing about Automata. These
/// pages are deterministic, work offline, and each one exercises a named capability, so a failing
/// demo points at the feature that broke.
/// </para>
/// <para>
/// The one live-web demo stays the first-run tutorial against Google. That is the floor, and it is
/// supposed to be a real site.
/// </para>
/// </summary>
public static class DemoPages
{
    /// <summary>How many products the generated shop holds.</summary>
    public const int ProductCount = 12;

    /// <summary>
    /// Price of product <paramref name="index"/> in whole cents. Twelve of these total 45750 —
    /// $457.50, the figure every correct run of the shop example has to arrive at.
    /// <para>
    /// Deterministic and integral so the right answer is known arithmetic rather than something a
    /// test has to take a run's word for — which is what lets the shop acceptance check assert
    /// three-way (one at a time == several at once == the truth) instead of merely comparing two
    /// runs that could both be wrong in the same way. The total is deliberately NOT exposed as a
    /// constant here: <c>tools/verify-shop.mjs</c> derives it by reading the generated pages, and
    /// an oracle that shared a definition with the thing it checks would not be an oracle.
    /// </para>
    /// </summary>
    public static int PriceCents(int index) => 1299 + (457 * index);

    public static string Sku(int index) => $"SKU-{index + 1:000}";

    private static readonly string[] Colours =
    [
        "Midnight", "Ash", "Ochre", "Fern", "Rust", "Slate",
        "Bone", "Cobalt", "Moss", "Clay", "Plum", "Sand",
    ];

    public static string ProductName(int index) => $"Wolf Tshirt — {Colours[index % Colours.Length]}";

    /// <summary>Every page the generator writes, in no particular order.</summary>
    public static IReadOnlyList<DemoPage> All()
    {
        var pages = new List<DemoPage> { Buttons(), ShopSearch() };
        for (var i = 0; i < ProductCount; i++) pages.Add(ShopItem(i));
        return pages;
    }

    private const string Css = """
        <style>
          :root { color-scheme: light dark; }
          body { font: 15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif;
                 margin: 0; padding: 24px; max-width: 900px; }
          h1 { font-size: 20px; margin: 0 0 4px; }
          .lede { color: #666; margin: 0 0 20px; }
          .results { list-style: none; margin: 0; padding: 0;
                     display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 12px; }
          .product { border: 1px solid #d4d4d4; border-radius: 6px; padding: 12px; }
          .product .title { display: block; font-weight: 600; margin-bottom: 4px; }
          .product .brand { color: #777; font-size: 13px; }
          .price { font-size: 24px; font-weight: 700; margin: 12px 0; }
          .sku { color: #777; font-size: 13px; }
          button { font: inherit; padding: 6px 14px; margin-right: 8px; }
          .clicked { margin-top: 8px; color: #157f3d; }
        </style>
        """;

    /// <summary>
    /// Three buttons that record what was clicked — the smallest thing worth automating, and the
    /// first example a new user should run. Intentionally the same page as
    /// <c>tools/verify-ui-fixture.html</c>, which the UI harness still writes for itself; the two
    /// are duplicates until there are enough demo pages to be worth sharing one asset set.
    /// </summary>
    // $$ so the page's own JavaScript braces stay literal; interpolation holes are {{ }} here.
    private static DemoPage Buttons() => new("buttons.html", $$"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Automata demo — three buttons</title>{{Css}}</head>
        <body>
          <h1>Three buttons</h1>
          <p class="lede">The smallest thing worth automating: click one, and the page says which.</p>
          <button id="alpha">Alpha</button>
          <button id="gamma">Gamma</button>
          <button id="beta">Beta</button>
          <script>
            document.querySelectorAll('button').forEach(function (btn) {
              btn.addEventListener('click', function () {
                var marker = document.createElement('div');
                marker.className = 'clicked';
                marker.setAttribute('data-id', btn.id);
                marker.textContent = 'clicked: ' + btn.id;
                document.body.appendChild(marker);
                document.title = 'clicked:' + btn.id;
              });
            });
          </script>
        </body>
        </html>
        """);

    /// <summary>
    /// A results grid of repeating tiles — the shape a harvest exists for. Every tile carries its
    /// id as <c>data-sku</c> and links to its own page, so the harvested rows are enough to drive
    /// a loop that visits each product in turn.
    /// </summary>
    private static DemoPage ShopSearch()
    {
        var tiles = string.Join("\n", Enumerable.Range(0, ProductCount).Select(i => $"""
                <li class="product" data-sku="{Sku(i)}">
                  <a class="title" href="item-{Sku(i)}.html">{ProductName(i)}</a>
                  <span class="brand">Lupine</span>
                </li>
        """));

        return new DemoPage("shop/search.html", $"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Wolf Tshirts — {ProductCount} results</title>{Css}</head>
        <body>
          <h1>Wolf Tshirts</h1>
          <p class="lede">{ProductCount} results. Each tile links to a page that holds the price.</p>
          <ul class="results">
        {tiles}
          </ul>
        </body>
        </html>
        """);
    }

    private static DemoPage ShopItem(int index)
    {
        var cents = PriceCents(index);
        var price = $"{cents / 100}.{cents % 100:00}";

        return new DemoPage($"shop/item-{Sku(index)}.html", $"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>{ProductName(index)}</title>{Css}</head>
        <body>
          <h1 class="item-title">{ProductName(index)}</h1>
          <p class="sku">SKU {Sku(index)}</p>
          <div class="price" data-cents="{cents}">${price}</div>
          <p class="lede"><a href="search.html">Back to results</a></p>
        </body>
        </html>
        """);
    }
}
