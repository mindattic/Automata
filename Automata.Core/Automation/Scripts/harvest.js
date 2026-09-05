// Reads many rows off a page in one pass, and works out the "all the things like this one"
// selector that makes such a pass possible from a single example click.
//
// The two halves are deliberately separate. __automataPickSet runs once, at authoring time, while
// a human is looking at the page: it generalises one clicked element into a repeating-row
// selector and reports how many rows that matches, so the count is confirmed on screen before
// anything is stored. __automataHarvest runs at replay time and only executes what was stored.
// Embedded resource in Automata.Core.
(function () {
    'use strict';

    function norm(t) { return (t || '').replace(/\s+/g, ' ').trim(); }

    function isVisible(el) {
        var r = el.getBoundingClientRect();
        return r.width > 0 && r.height > 0;
    }

    function esc(s) {
        return (window.CSS && CSS.escape) ? CSS.escape(s) : String(s).replace(/([^\w-])/g, '\\$1');
    }

    // A class list minus the noise that varies per row. Framework hash classes and state classes
    // are exactly what makes two otherwise identical tiles look different, so they are dropped:
    // keeping them would generalise one tile into one tile.
    //
    // The rule lives in stability.js, prepended to this file, because a fingerprint wants the
    // identical answer — these two lists had drifted apart, and a class could be unstable enough to
    // spoil a harvest while still being recorded as part of an element's identity.
    function stableClasses(el) {
        return window.__automataStability.stableClasses(el.classList);
    }

    // The signature that decides "these two elements are the same KIND of thing".
    function signature(el) {
        var tag = el.tagName.toLowerCase();
        var classes = stableClasses(el);
        if (classes.length) return tag + '.' + classes.map(esc).join('.');

        var role = el.getAttribute('role');
        if (role) return tag + '[role="' + role + '"]';

        // Attribute-marked rows are common in real markup (data-asin, data-testid) and are the
        // most reliable signature of all when present, because they were put there on purpose.
        for (var i = 0; i < el.attributes.length; i++) {
            var name = el.attributes[i].name;
            if (/^data-(id|asin|sku|item|index|key|testid|test-id|qa)$/.test(name)) return tag + '[' + name + ']';
        }
        return tag;
    }

    /// Every visible element matching `selector`, in the FIRST root that has any.
    ///
    /// Roots come from resolver.js's own walk — the document, every shadow root open or closed,
    /// every same-origin frame — so a harvest reaches wherever a resolve reaches. The document
    /// comes first in that walk, which settles the awkward case by giving it the obvious answer:
    /// the page as written wins, and a component's insides only answer when the page itself has
    /// nothing. Rows are never mixed across roots, because "all the things like this one" means one
    /// list, and two lists in two places is a different harvest with the same selector.
    function matchAll(selector) {
        var roots = window.__automataReachableRoots ? window.__automataReachableRoots() : [document];
        for (var r = 0; r < roots.length; r++) {
            var hits;
            try { hits = Array.prototype.filter.call(roots[r].querySelectorAll(selector), isVisible); }
            catch (e) { continue; }
            if (hits.length) return hits;
        }
        return [];
    }

    /// Generalises one clicked element into a repeating-row selector.
    ///
    /// Walks OUTWARD from the click rather than starting at the top, because the thing a user
    /// clicks is almost never the row — it is a title or an image inside the row. The first
    /// ancestor that has siblings of its own kind IS the row, and stopping at the first one
    /// matters: keep walking and "product tile" becomes "the results grid", one row, no loop.
    window.__automataPickSet = function (el) {
        if (!el) return JSON.stringify({ ok: false, error: 'nothing selected' });

        var node = el;
        var depth = 0;
        while (node && node !== document.body && depth < 12) {
            var parent = node.parentElement;
            if (parent) {
                var sig = signature(node);
                var siblings = 0;
                for (var i = 0; i < parent.children.length; i++) {
                    if (signature(parent.children[i]) === sig) siblings++;
                }
                if (siblings >= 2) {
                    // Scoped to the parent so a sibling set in some other part of the page is not
                    // swept in. The parent's own signature is the scope.
                    var scoped = signature(parent) + ' > ' + sig;
                    var hits = matchAll(scoped);
                    if (hits.length >= 2) {
                        return JSON.stringify({
                            ok: true,
                            selector: scoped,
                            count: hits.length,
                            sample: norm(hits[0].textContent).slice(0, 120),
                            depth: depth
                        });
                    }
                    var bare = matchAll(sig);
                    if (bare.length >= 2) {
                        return JSON.stringify({
                            ok: true, selector: sig, count: bare.length,
                            sample: norm(bare[0].textContent).slice(0, 120), depth: depth
                        });
                    }
                }
            }
            node = parent;
            depth++;
        }

        return JSON.stringify({
            ok: false,
            error: 'nothing on this page repeats around what you picked — a harvest needs a list, ' +
                   'a grid or a table of similar items'
        });
    };

    /// Builds a selector for a field, RELATIVE to the row that contains it. Returns null when the
    /// picked element is the row itself, which the harvester reads as "the row's own text".
    window.__automataPickField = function (el, itemSelector) {
        if (!el) return JSON.stringify({ ok: false, error: 'nothing selected' });
        var rows = matchAll(itemSelector);
        var row = null;
        for (var i = 0; i < rows.length && !row; i++) if (rows[i].contains(el)) row = rows[i];
        if (!row) {
            return JSON.stringify({
                ok: false,
                error: 'that is outside every row this harvest matched — pick something inside one of them'
            });
        }
        if (row === el) return JSON.stringify({ ok: true, selector: null, resolves: rows.length });

        // Try the plain signature first; it reads well and survives re-ordering. Fall back to a
        // child-index path only when the signature is ambiguous inside its own row.
        var sig = signature(el);
        var within = 0;
        try { within = row.querySelectorAll(sig).length; } catch (e) { within = 0; }
        var selector = within === 1 ? sig : indexPath(row, el);

        var resolves = 0;
        for (var r = 0; r < rows.length; r++) {
            try { if (rows[r].querySelector(selector)) resolves++; } catch (e) { /* bad path */ }
        }
        return JSON.stringify({
            ok: true,
            selector: selector,
            resolves: resolves,
            total: rows.length,
            text: norm(el.textContent).slice(0, 120),
            href: el.tagName === 'A' ? el.href : null
        });
    };

    function indexPath(root, el) {
        var parts = [];
        var node = el;
        while (node && node !== root) {
            var parent = node.parentElement;
            if (!parent) break;
            var index = 1;
            for (var i = 0; i < parent.children.length; i++) {
                if (parent.children[i] === node) break;
                if (parent.children[i].tagName === node.tagName) index++;
            }
            parts.unshift(node.tagName.toLowerCase() + ':nth-of-type(' + index + ')');
            node = parent;
        }
        return parts.join(' > ');
    }

    function readField(row, field) {
        var el = row;
        if (field.selector) {
            try { el = row.querySelector(field.selector); } catch (e) { el = null; }
        }
        if (!el) return null;

        if (field.source === 'href') return el.tagName === 'A' ? el.href : (el.getAttribute('href') || null);
        if (field.source === 'attribute') {
            return field.attributeName ? el.getAttribute(field.attributeName) : null;
        }
        return el.tagName === 'INPUT' ? norm(el.value) : norm(el.textContent);
    }

    /// Executes a stored harvest. Returns every matched row with every declared field, and says
    /// which fields came back empty — an empty column across every row means the page changed
    /// under the selector, and reporting it is the difference between a failed run and a run that
    /// quietly harvests blanks.
    window.__automataHarvest = function (spec) {
        var rows = matchAll(spec.itemSelector);
        if (!rows.length) {
            // Nothing in any root this document can walk. A cross-origin frame is not one of those,
            // and the copy of this script running inside it can be asked — see the deep pass below.
            return JSON.stringify({ ok: false, count: 0, error: 'no rows matched', rows: [] });
        }

        var out = [];
        var filled = {};
        for (var f = 0; f < spec.fields.length; f++) filled[spec.fields[f].name] = 0;

        for (var i = 0; i < rows.length; i++) {
            var record = {};
            for (var j = 0; j < spec.fields.length; j++) {
                var field = spec.fields[j];
                var value = readField(rows[i], field);
                record[field.name] = value == null ? '' : String(value);
                if (record[field.name] !== '') filled[field.name]++;
            }
            out.push(record);
        }

        var empty = [];
        for (var name in filled) if (filled[name] === 0) empty.push(name);

        return JSON.stringify({ ok: true, count: out.length, rows: out, emptyFields: empty });
    };

    /// What the engine calls. One pass here; if nothing matched and this document holds frames it
    /// cannot read into, one round of asking them — which cannot finish inside this call, so it
    /// reports itself as waiting and the caller's next attempt collects the answer.
    ///
    /// Same shape as the resolver's deep pass, and for the same reason: an answer that has to cross
    /// a frame boundary does not exist yet when the call that asked for it returns. Unlike the
    /// resolver's action path this needs no `eval` in the frame — the function is looked up by name
    /// on the frame's own `window`, where the host already put it.
    window.__automataHarvestDeep = function (spec) {
        var here = window.__automataHarvest(spec);
        var frames = window.__automataFrames;
        if (here.indexOf('"ok":true') >= 0 || !frames || !frames.unreachable().length) {
            window.__automataHarvestAnswer = null;
            return here;
        }

        var sig = JSON.stringify(spec);
        var state = window.__automataHarvestAnswer;
        if (!state || state.sig !== sig) {
            state = window.__automataHarvestAnswer = { sig: sig, done: false, result: null };
            frames.askCall('__automataHarvest', [spec], function (answer) {
                return answer.indexOf('"ok":true') >= 0;
            }).then(
                function (r) { state.result = r; state.done = true; },
                function () { state.result = null; state.done = true; });
            return JSON.stringify({ ok: false, waitingOnFrames: true, count: 0, rows: [] });
        }
        if (!state.done) return JSON.stringify({ ok: false, waitingOnFrames: true, count: 0, rows: [] });

        window.__automataHarvestAnswer = null;
        return state.result !== null ? state.result : here;
    };
})();
