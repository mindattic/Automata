namespace Automata.Core.Automation.Demos;

/// <summary>
/// One generated demo file: a path under the demo root, and its whole content.
/// <para>
/// Usually a page. Not always — <c>notes.txt</c> is the file the form example attaches to its
/// upload field, and a demo that told the user to go and find a file of their own would be a demo
/// that does not run.
/// </para>
/// </summary>
public sealed record DemoPage(string RelativePath, string Content);

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

    /// <summary>The file the form example attaches, written beside the pages so it always exists.</summary>
    public const string AttachmentFile = "notes.txt";

    /// <summary>
    /// How far from the left edge the zoom example's button sits. Comfortably off the side of any
    /// pane anybody runs this in at normal size — a browser lane is 1280px wide, so 2400 leaves no
    /// doubt — and comfortably inside the viewport once the page is at
    /// <see cref="ZoomedTo"/>%, which is the whole demonstration.
    /// </summary>
    public const int FarButtonLeftPx = 2400;

    /// <summary>The level the zoom example zooms out to.</summary>
    public const int ZoomedTo = 25;

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

    /// <summary>Every file the generator writes, in no particular order.</summary>
    public static IReadOnlyList<DemoPage> All()
    {
        var pages = new List<DemoPage>
            { Buttons(), Form(), Attachment(), Slow(), Order(), Zoom(), Invoices(), ShopSearch() };
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
          .field { display: block; margin: 0 0 12px; }
          .field > span { display: block; font-weight: 600; margin-bottom: 2px; }
          input[type=text], input[type=email], input[type=search], select {
            font: inherit; padding: 5px 7px; min-width: 260px; }
          fieldset { border: 1px solid #d4d4d4; border-radius: 6px; margin: 0 0 12px; }
          .echo { color: #157f3d; min-height: 1.4em; margin: 0 0 12px; }
          .summary { border: 1px solid #157f3d; border-radius: 6px; padding: 12px; margin-top: 16px; }
          .summary dt { font-weight: 600; }
          .summary dd { margin: 0 0 8px; }
          .facts { margin: 0 0 16px; }
          .facts dt { font-weight: 600; }
          .facts dd { margin: 0 0 8px; }
          .pending { color: #a05000; }
          table.invoices { border-collapse: collapse; }
          table.invoices th, table.invoices td { border: 1px solid #d4d4d4; padding: 6px 12px; text-align: left; }
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

    /// <summary>The file the form example uploads. Plain text, so it is obvious in Explorer what
    /// the demo attached and why.</summary>
    private static DemoPage Attachment() => new(AttachmentFile, """
        This file exists so the form example has something real to attach.
        Automata attaches it through the browser's own file input — there is no native
        file picker to click, which is why an upload step can run unattended.
        """);

    /// <summary>
    /// One of every input control, on one page, plus a summary that only appears once the form is
    /// submitted — so a task has something to wait for and something to assert against.
    /// <para>
    /// The form suppresses its own submission: pressing Enter in a text field would otherwise
    /// reload the page out from under the run, and the point of the search box here is to show
    /// Enter reaching the page rather than to navigate anywhere.
    /// </para>
    /// </summary>
    private static DemoPage Form() => new("form.html", $$"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Automata demo — one of every field</title>{{Css}}</head>
        <body>
          <h1>One of every field</h1>
          <p class="lede">Text, a search box that answers the Enter key, checkboxes, radios, a
             dropdown and a file — then a summary that takes a moment to appear.</p>

          <form id="signup" onsubmit="return false">
            <label class="field" for="full-name"><span>Full name</span>
              <input id="full-name" name="fullName" type="text"></label>

            <label class="field" for="email"><span>Email</span>
              <input id="email" name="email" type="email"></label>

            <label class="field" for="search"><span>Search (press Enter)</span>
              <input id="search" name="search" type="search" placeholder="type, then Enter"></label>
            <p id="search-echo" class="echo"></p>

            <label class="field" for="terms">
              <input id="terms" name="terms" type="checkbox"> I accept the terms</label>

            <label class="field" for="newsletter">
              <input id="newsletter" name="newsletter" type="checkbox" checked> Send me the newsletter</label>

            <fieldset>
              <legend>Shipping</legend>
              <label class="field" for="ship-standard">
                <input id="ship-standard" name="shipping" type="radio" value="Standard" checked> Standard</label>
              <label class="field" for="ship-express">
                <input id="ship-express" name="shipping" type="radio" value="Express"> Express</label>
            </fieldset>

            <label class="field" for="size"><span>Size</span>
              <select id="size" name="size">
                <option>Small</option>
                <option>Medium</option>
                <option>Large</option>
              </select></label>

            <label class="field" for="attachment"><span>Attachment</span>
              <input id="attachment" name="attachment" type="file"></label>

            <button id="submit" type="button">Submit</button>
          </form>

          <div id="summary-slot"></div>

          <script>
            document.getElementById('search').addEventListener('keydown', function (e) {
              if (e.key !== 'Enter') return;
              e.preventDefault();
              document.getElementById('search-echo').textContent = 'searched: ' + e.target.value;
            });

            document.getElementById('submit').addEventListener('click', function () {
              var slot = document.getElementById('summary-slot');
              slot.innerHTML = '<p class="pending" id="submitting">Submitting…</p>';

              // Deliberately late. A summary that appeared instantly would let a task pass
              // without ever waiting for anything, and waiting is the thing being demonstrated.
              setTimeout(function () {
                var shipping = document.querySelector('input[name=shipping]:checked');
                var file = document.getElementById('attachment').files[0];
                var choices = [
                  document.getElementById('size').value,
                  shipping ? shipping.value : 'none',
                  document.getElementById('terms').checked ? 'terms accepted' : 'terms not accepted',
                  document.getElementById('newsletter').checked ? 'newsletter on' : 'newsletter off'
                ].join(', ');

                slot.innerHTML =
                  '<dl class="summary" id="summary">' +
                  '<dt>Name</dt><dd id="summary-name"></dd>' +
                  '<dt>Email</dt><dd id="summary-email"></dd>' +
                  '<dt>Choices</dt><dd id="summary-choices"></dd>' +
                  '<dt>Attachment</dt><dd id="summary-file"></dd>' +
                  '</dl>';
                document.getElementById('summary-name').textContent =
                  document.getElementById('full-name').value;
                document.getElementById('summary-email').textContent =
                  document.getElementById('email').value;
                document.getElementById('summary-choices').textContent = choices;
                document.getElementById('summary-file').textContent = file ? file.name : 'none';
              }, 900);
            });
          </script>
        </body>
        </html>
        """);

    /// <summary>
    /// A page that finishes rendering itself long after it has loaded — the ordinary case on any
    /// modern site, and the reason a step waits for its element instead of assuming it is there.
    /// </summary>
    private static DemoPage Slow() => new("slow.html", $$"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Automata demo — a page that takes its time</title>{{Css}}</head>
        <body>
          <h1>A page that takes its time</h1>
          <p class="lede">The status settles after a moment, and the panel below does not exist
             yet at all — it is added a second later.</p>
          <div id="status" class="pending">starting</div>
          <div id="slot"></div>
          <script>
            setTimeout(function () {
              var status = document.getElementById('status');
              status.textContent = 'ready';
              status.className = '';
            }, 300);

            setTimeout(function () {
              var late = document.createElement('div');
              late.id = 'late';
              late.className = 'summary';
              late.textContent = 'The late panel is here.';
              document.getElementById('slot').appendChild(late);
            }, 1200);
          </script>
        </body>
        </html>
        """);

    /// <summary>
    /// One order, described in the plainest possible facts — a word, a number, two booleans and a
    /// blank field — so that every comparison the condition picker offers has something on this
    /// page it can honestly be asked about.
    /// <para>
    /// The note is an INPUT rather than an empty div: an element with no content has no box to
    /// resolve against, and "the note is blank" is a question about a field somebody left empty,
    /// not about markup that is missing.
    /// </para>
    /// </summary>
    private static DemoPage Order() => new("order.html", $$"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Automata demo — order 4021</title>{{Css}}</head>
        <body>
          <h1>Order 4021</h1>
          <p class="lede">Everything the shipping desk checks before it lets an order go.</p>
          <dl class="facts">
            <dt>Status</dt><dd id="status">Ready to ship</dd>
            <dt>In stock</dt><dd id="stock">12</dd>
            <dt>Express</dt><dd id="express">true</dd>
            <dt>Fragile</dt><dd id="fragile">false</dd>
          </dl>
          <label class="field" for="note"><span>Note left by the packer</span>
            <input id="note" type="text" value=""></label>
          <button id="ship">Ship it</button>
          <div id="shipped"></div>
          <script>
            document.getElementById('ship').addEventListener('click', function () {
              var done = document.createElement('div');
              done.className = 'summary';
              done.id = 'shipped-notice';
              done.textContent = 'shipped: order 4021';
              document.getElementById('shipped').appendChild(done);
            });
          </script>
        </body>
        </html>
        """);

    /// <summary>
    /// A page whose useful part is off the side of the window, and a button that reports whether
    /// it is currently within reach.
    /// <para>
    /// The report is produced by a CLICK HANDLER, not by a timer, an animation frame or a resize
    /// listener — and that is not an accident. A browser lane renders into an off-screen window,
    /// so its page counts as hidden: frames stop, repeating timers throttle almost to nothing, and
    /// a readout on any of those sits on its load-time text forever while appearing to work.
    /// A handler the automation itself triggers always runs, and measures the page as it is at
    /// that instant.
    /// </para>
    /// </summary>
    private static DemoPage Zoom() => new("zoom.html", $$"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Automata demo — wider than the window</title>{{Css}}
        <style>
          #wide { position: relative; height: 48px; }
          #far-button { position: absolute; left: {{FarButtonLeftPx}}px; top: 0; }
          #reach { font-weight: 600; }
        </style>
        </head>
        <body>
          <h1>Wider than the window</h1>
          <p class="lede">The button below sits {{FarButtonLeftPx}} pixels from the left edge, so at
             normal size it is off the side of the window and a click aimed at it lands on nothing.
             Press Check to ask the page whether it can be reached right now.</p>
          <button id="check">Check</button>
          <p id="reach">not checked yet</p>
          <div id="wide"><button id="far-button">The far button</button></div>
          <div id="zoom-slot"></div>
          <script>
            function reachable() {
              var box = document.getElementById('far-button').getBoundingClientRect();
              return box.left >= 0 && box.right <= window.innerWidth;
            }

            document.getElementById('check').addEventListener('click', function () {
              document.getElementById('reach').textContent = reachable()
                ? 'the far button is reachable'
                : 'the far button is out of reach';
            });

            document.getElementById('far-button').addEventListener('click', function () {
              var done = document.createElement('div');
              done.id = 'zoom-clicked';
              done.className = 'summary';
              // What the page could see of itself at the moment the click arrived, so the record
              // says the click landed AND that the whole width was on screen when it did.
              done.textContent = 'clicked at the far end, ' + (reachable()
                ? 'with the whole width on screen'
                : 'while it was still off screen');
              document.getElementById('zoom-slot').appendChild(done);
            });
          </script>
        </body>
        </html>
        """);

    /// <summary>
    /// Three invoices in a table — the smallest thing worth totalling, and a shape a harvest can
    /// read straight off. The amounts carry a currency symbol on purpose: text read off a page is
    /// almost never a bare number, and an aggregate that could not cope with "$12.50" would be an
    /// aggregate for datasets nobody actually has.
    /// </summary>
    private static DemoPage Invoices() => new("invoices.html", $$"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Automata demo — three invoices</title>{{Css}}</head>
        <body>
          <h1>Invoices</h1>
          <p class="lede">Three rows, one column worth adding up.</p>
          <table class="invoices">
            <thead><tr><th>Reference</th><th>Amount</th></tr></thead>
            <tbody>
        {{InvoiceRows}}
            </tbody>
          </table>
        </body>
        </html>
        """);

    /// <summary>The invoice rows, and the amounts every correct total has to agree with.</summary>
    public static readonly decimal[] InvoiceAmounts = [12.50m, 20.00m, 27.50m];

    private static string InvoiceRows => string.Join("\n", InvoiceAmounts.Select((amount, i) => $"""
                <tr data-ref="INV-{i + 1:000}">
                  <td class="ref">INV-{i + 1:000}</td>
                  <td class="amount">${amount:0.00}</td>
                </tr>
        """));

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
