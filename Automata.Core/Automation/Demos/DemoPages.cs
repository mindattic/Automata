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
/// Local pages rather than live sites, on purpose. A demo whose job is to prove that harvesting
/// and looping work cannot also be a bet on someone else's markup, consent banner
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
    /// pane anybody runs this in at normal size — the headless browser is 1280px wide, so 2400 leaves no
    /// doubt — and comfortably inside the viewport once the page is at
    /// <see cref="ZoomedTo"/>%, which is the whole demonstration.
    /// </summary>
    public const int FarButtonLeftPx = 2400;

    /// <summary>The level the zoom example zooms out to.</summary>
    public const int ZoomedTo = 25;

    /// <summary>The dataset the roster example iterates, and the file it is written to.</summary>
    public const string RosterDataset = "roster.json";

    /// <summary>
    /// A deliberately RAGGED list: two of the three rows carry a name and one does not.
    /// <para>
    /// Ragged is the normal shape of a JSON blob that came out of somewhere else, and it is the
    /// case a spreadsheet cannot produce — a CSV gives every row every column because it has one
    /// header. It is written as an example ASSET rather than harvested, because a harvest fills
    /// every column of every row and so could never produce the gap this example is about.
    /// </para>
    /// <para>
    /// It is also NESTED, for the second thing a JSON blob does that a spreadsheet cannot: the two
    /// named rows carry a <c>Contact</c> object, and the example binds <c>row.Contact.Email</c> to
    /// show a field inside one is reachable by name.
    /// </para>
    /// </summary>
    public const string RosterJson = """
        [
          { "Name": "Ada", "Role": "engineer", "Contact": { "Email": "ada@example.com" } },
          { "Role": "unknown" },
          { "Name": "Grace", "Role": "admiral", "Contact": { "Email": "grace@example.com" } }
        ]
        """;

    /// <summary>The nested field the roster example reads, written the way a binding names it.</summary>
    public const string RosterEmailColumn = "Contact.Email";

    /// <summary>How many roster rows carry a name, and how many do not — asserted by the example
    /// itself, so they are stated once.</summary>
    public const int RosterNamed = 2;
    public const int RosterUnnamed = 1;

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
        {
            Buttons(), Form(), Attachment(), Slow(), Order(), Zoom(), Invoices(),
            Shadow(), Closed(), ClosedFrame(), Roster(), ShopSearch(), Drift(), TicketQueue(),
            TicketLookup(),
        };
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
          h2.section { font-size: 15px; margin: 20px 0 8px; }
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

    /// <summary>The ticket the queue page says is next, and the one the pipeline example carries
    /// from its first task to its second.</summary>
    public const string NextTicketId = "TCK-2317";

    /// <summary>
    /// The support queue: a list, with the one to deal with next called out by id.
    /// <para>
    /// Two pages rather than one, deliberately. A pipeline whose second half could have read what
    /// it needed off the first half's page would demonstrate nothing — the value has to LEAVE the
    /// page it was found on to prove it was carried between tasks rather than merely re-read.
    /// </para>
    /// </summary>
    private static DemoPage TicketQueue() => new("pipeline/queue.html", $$"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Automata demo — support queue</title>{{Css}}</head>
        <body>
          <h1>Support queue</h1>
          <p class="lede">Six tickets are open. The oldest one still unassigned is next.</p>
          <dl class="facts">
            <dt>Open tickets</dt><dd id="open-count">6</dd>
            <dt>Next up</dt><dd id="next-ticket">{{NextTicketId}}</dd>
          </dl>
          <ul class="results">
            <li class="product"><span class="title">TCK-2314</span><span class="brand">assigned</span></li>
            <li class="product"><span class="title">TCK-2316</span><span class="brand">assigned</span></li>
            <li class="product"><span class="title">{{NextTicketId}}</span><span class="brand">unassigned</span></li>
          </ul>
        </body>
        </html>
        """);

    /// <summary>
    /// The ticket desk: type an id, press Look up, and the page reports who owns it and how urgent
    /// it is. Unknown ids say so rather than showing a blank card, because a pipeline handed the
    /// wrong value must fail loudly at the step that used it.
    /// </summary>
    private static DemoPage TicketLookup() => new("pipeline/ticket.html", $$"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Automata demo — ticket desk</title>{{Css}}</head>
        <body>
          <h1>Ticket desk</h1>
          <p class="lede">Look a ticket up by its id.</p>
          <label class="field" for="ticket-id"><span>Ticket</span>
            <input id="ticket-id" type="text" value=""></label>
          <button id="look-up">Look up</button>
          <p id="found">nothing looked up yet</p>
          <dl class="facts">
            <dt>Owner</dt><dd id="owner">—</dd>
            <dt>Priority</dt><dd id="priority">—</dd>
          </dl>
          <script>
            var TICKETS = {
              'TCK-2314': { owner: 'Devon Okafor', priority: 'low' },
              'TCK-2316': { owner: 'Mai Sorensen', priority: 'normal' },
              '{{NextTicketId}}': { owner: 'Priya Raman', priority: 'high' }
            };
            document.getElementById('look-up').addEventListener('click', function () {
              var id = document.getElementById('ticket-id').value.trim();
              var ticket = TICKETS[id];
              document.getElementById('found').textContent =
                ticket ? 'found: ' + id : 'no such ticket: ' + id;
              document.getElementById('owner').textContent = ticket ? ticket.owner : '—';
              document.getElementById('priority').textContent = ticket ? ticket.priority : '—';
            });
          </script>
        </body>
        </html>
        """);

    /// <summary>
    /// A page that has already been "redeployed": the button it offers is the one the example was
    /// recorded against, except its id changed. Everything a person would use to find it again —
    /// the words on it — is untouched, which is exactly the situation self-healing exists for.
    /// </summary>
    private static DemoPage Drift() => new("drift.html", $$"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Automata demo — the page moved</title>{{Css}}</head>
        <body>
          <h1>The page moved</h1>
          <p class="lede">This button's id is not the one the example was recorded against. Its
             words are, and that is enough to find it again.</p>
          <button id="place-order">Place order</button>
          <script>
            document.getElementById('place-order').addEventListener('click', function () {
              var marker = document.createElement('div');
              marker.className = 'clicked';
              marker.textContent = 'order placed';
              document.body.appendChild(marker);
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
          <p class="lede">The status goes through three states, the panel below does not exist yet
             at all, and the last state change happens LONG after anything has read the page.</p>
          <div id="status" class="pending">starting</div>
          <div id="slot"></div>
          <script>
            // Three states, not two, and the third one is the point. A run reads the status while
            // it still says "working"; anything that then waits on the value it READ is waiting on
            // a string that will never change again. Only a wait that goes back to the page sees
            // "ready" arrive.
            setTimeout(function () {
              document.getElementById('status').textContent = 'working';
            }, 300);

            setTimeout(function () {
              var late = document.createElement('div');
              late.id = 'late';
              late.className = 'summary';
              late.textContent = 'The late panel is here.';
              document.getElementById('slot').appendChild(late);
            }, 1200);

            setTimeout(function () {
              var status = document.getElementById('status');
              status.textContent = 'ready';
              status.className = '';
            }, 2600);
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
    /// listener — and that is not an accident. The headless browser renders into an off-screen window,
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
    /// A control inside an OPEN shadow root, and a same-origin iframe — the two places a selector
    /// run against the top document simply cannot see.
    /// <para>
    /// Both report what happened back into their OWN tree, not into the top document, so a step
    /// that asserts on the result has to reach in too. An example that clicked inside and then
    /// checked a flag on the outer page would only ever prove half of it.
    /// </para>
    /// </summary>
    private static DemoPage Shadow() => new("shadow.html", $$"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Automata demo — shadow DOM and a frame</title>{{Css}}</head>
        <body>
          <h1>Behind a boundary</h1>
          <p class="lede">Neither of the two controls below is reachable from the top document:
             one lives in a shadow root, the other in a frame of its own. Nor is the file input in
             the shadow root, nor the list in the frame — and every one of them is now.</p>

          <h2 class="section">In a shadow root</h2>
          <div id="host"></div>

          <h2 class="section">In an iframe</h2>
          <!-- srcdoc, not src="framed.html", and for a reason worth knowing: a page loaded from
               file:// gets an OPAQUE origin, so one local file embedding another is cross-origin
               even though they sit in the same folder — nothing can read into it. A srcdoc frame
               inherits its embedder's origin, which is what a real same-origin embed
               (shop.example embedding shop.example/cart) looks like, and is therefore the case
               this example is actually about. -->
          <iframe id="frame" title="An embedded page"
                  style="width: 420px; height: 240px; border: 1px solid #d4d4d4;"
                  srcdoc="<!doctype html><meta charset='utf-8'>
                          <style>body { font: 15px/1.5 system-ui, sans-serif; margin: 8px; }
                                 button { font: inherit; padding: 6px 14px; }</style>
                          <p>This page is inside an iframe on the page that embedded it.</p>
                          <button id='in-frame'>The button in the frame</button>
                          <p id='frame-said'></p>
                          <ul class='framed-list'>
                            <li class='framed-row' data-ref='F-1'>First, in the frame</li>
                            <li class='framed-row' data-ref='F-2'>Second, in the frame</li>
                            <li class='framed-row' data-ref='F-3'>Third, in the frame</li>
                          </ul>
                          <script>
                            document.getElementById('in-frame').addEventListener('click', function () {
                              document.getElementById('frame-said').textContent = 'the frame was clicked';
                            });
                          </script>"></iframe>

          <script>
            // OPEN, so script can see in. A closed root is invisible to everything by design, and
            // no automation can reach one without the page's cooperation.
            var shadow = document.getElementById('host').attachShadow({ mode: 'open' });
            shadow.innerHTML =
              '<style>p { font: inherit; } button { font: inherit; padding: 6px 14px; }</style>' +
              '<button id="in-shadow">The button in the shadow root</button>' +
              '<p id="shadow-said"></p>' +
              // A file input in here is the case that used to be unreachable for a different
              // reason from all the others: every action goes through the resolver, and this one
              // went through a selector run against the top document.
              '<input type="file" id="in-shadow-file" />';
            shadow.getElementById('in-shadow').addEventListener('click', function () {
              shadow.getElementById('shadow-said').textContent = 'the shadow root was clicked';
            });
          </script>
        </body>
        </html>
        """);

    /// <summary>
    /// The two boundaries nothing can be walked through: a CLOSED shadow root and a CROSS-ORIGIN
    /// iframe.
    /// <para>
    /// Both are the real thing rather than a stand-in. The root is opened with
    /// <c>mode: 'closed'</c>, and the page keeps the only reference the language hands out —
    /// <c>host.shadowRoot</c> is null forever after, so nothing that walks the DOM finds the way in.
    /// The frame is loaded with <c>src</c> and not <c>srcdoc</c>, which is what makes it
    /// cross-origin: a document loaded from <c>file://</c> gets an OPAQUE origin, so one local file
    /// embedding another is a genuine cross-origin embed even though the two sit in the same folder.
    /// The neighbouring shadow.html example relies on exactly the opposite fact, and says so.
    /// </para>
    /// <para>
    /// As there, each control writes its answer INTO ITS OWN TREE, so a step that asserts on the
    /// result has to get back in as well.
    /// </para>
    /// </summary>
    private static DemoPage Closed() => new("closed.html", $$"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Automata demo — a closed root and a cross-origin frame</title>{{Css}}</head>
        <body>
          <h1>Behind a boundary nothing can be walked through</h1>
          <p class="lede">The two below are not merely hidden from a selector — they are hidden from
             script itself. A closed shadow root hands its only reference to the component that made
             it, and a cross-origin document throws the moment anything reads it.</p>

          <h2 class="section">In a closed shadow root</h2>
          <div id="closed-host"></div>

          <h2 class="section">In a cross-origin frame</h2>
          <!-- src, not srcdoc, and that is the whole difference. A srcdoc frame inherits its
               embedder's origin (see shadow.html); a file:// document has an OPAQUE origin, so
               loading a sibling file by src produces a frame this page is not allowed to read. -->
          <iframe id="opaque-frame" title="A page from another origin"
                  style="width: 420px; height: 230px; border: 1px solid #d4d4d4;"
                  src="closed-frame.html"></iframe>

          <script>
            // Closed. The reference below is the only one there will ever be, and it never leaves
            // this function — which is exactly the shape a component library ships.
            (function () {
              var root = document.getElementById('closed-host').attachShadow({ mode: 'closed' });
              root.innerHTML =
                '<style>p { font: inherit; } button { font: inherit; padding: 6px 14px; }</style>' +
                '<button id="in-closed">The button in the closed root</button>' +
                '<p id="closed-said"></p>' +
                '<input type="file" id="in-closed-file" />';
              root.getElementById('in-closed').addEventListener('click', function () {
                root.getElementById('closed-said').textContent = 'the closed root was clicked';
              });
            })();
          </script>
        </body>
        </html>
        """);

    /// <summary>The page inside the cross-origin frame. Its own file, because being a separate
    /// <c>file://</c> document is the entire reason it is unreachable from the one that embeds
    /// it.</summary>
    private static DemoPage ClosedFrame() => new("closed-frame.html", $$"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Automata demo — the framed page</title>
        <style>
          body { font: 15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; margin: 8px; }
          button { font: inherit; padding: 6px 14px; }
          .clicked { margin-top: 8px; color: #157f3d; }
          .opaque-list { margin: 8px 0 0; padding-left: 18px; }
        </style></head>
        <body>
          <p>This page is its own origin. The page that embeds it cannot read a word of it.</p>
          <button id="in-opaque">The button in the cross-origin frame</button>
          <p id="opaque-said" class="clicked"></p>
          <ul class="opaque-list">
            <li class="opaque-row" data-ref="O-1">First, across the origin</li>
            <li class="opaque-row" data-ref="O-2">Second, across the origin</li>
          </ul>
          <script>
            document.getElementById('in-opaque').addEventListener('click', function () {
              document.getElementById('opaque-said').textContent = 'the cross-origin frame was clicked';
            });
          </script>
        </body>
        </html>
        """);

    /// <summary>
    /// A roster form with an Add and a Skip, and a tally of which happened.
    /// <para>
    /// The tally is written by the click handlers — on demand, triggered by the automation itself.
    /// A page in the headless browser is off-screen and therefore hidden, so anything on a timer, an
    /// animation frame or a resize reports its load-time answer forever while appearing to work.
    /// </para>
    /// </summary>
    private static DemoPage Roster() => new("roster.html", $$"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Automata demo — a list with gaps</title>{{Css}}</head>
        <body>
          <h1>Roster</h1>
          <p class="lede">Add the people who have a name; skip the ones who do not.</p>
          <label class="field" for="txtName"><span>Name</span>
            <input id="txtName" type="text" value=""></label>
          <button id="add">Add</button>
          <button id="skip">Skip</button>
          <p id="tally">added 0, skipped 0</p>
          <ul id="log" class="results"></ul>
          <script>
            var added = 0, skipped = 0;

            function record(text, cls) {
              var li = document.createElement('li');
              li.className = 'product' + (cls ? ' ' + cls : '');
              li.textContent = text;
              document.getElementById('log').appendChild(li);
              document.getElementById('tally').textContent =
                'added ' + added + ', skipped ' + skipped;
            }

            document.getElementById('add').addEventListener('click', function () {
              var box = document.getElementById('txtName');
              added++;
              record('added ' + box.value);
              box.value = '';
            });

            document.getElementById('skip').addEventListener('click', function () {
              skipped++;
              record('skipped a row with no name', 'pending');
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
