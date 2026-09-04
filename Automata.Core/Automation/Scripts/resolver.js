// Resolves an ElementFingerprint against the live DOM via a fixed path-of-least-resistance
// cascade; the first strategy yielding exactly ONE visible match wins. When nothing is unique,
// candidates are pooled and scored — a clear leader wins, a near-tie is reported as ambiguous
// rather than guessed at. Embedded resource in Automata.Core; requires fingerprint.js to be
// evaluated first (for __automataFingerprint) when refingerprint is requested.
//
// Every query runs across every REACHABLE ROOT, not just the top document: each open shadow root,
// and each same-origin iframe's document, recursively. A component library that renders its button
// inside a shadow root is not exotic any more, and a document.querySelector that stops at the
// boundary simply reports "element not found by any strategy" — which reads as a broken recording
// rather than as a limit of the tool.
//
// Two things stay out of reach, and the reason is the same for both: the page cannot see in.
// A CLOSED shadow root exposes nothing to script by design, and a CROSS-ORIGIN iframe's document
// throws on access. Reaching those means evaluating in each frame's own context over CDP rather
// than running one script in the top document, which is a different mechanism, not a longer
// selector.
(function () {
    'use strict';

    function norm(t) { return (t || '').replace(/\s+/g, ' ').trim(); }
    function lower(t) { return norm(t).toLowerCase(); }

    function isVisible(el) {
        var r = el.getBoundingClientRect();
        return r.width > 0 && r.height > 0;
    }

    // Walking for roots means a querySelectorAll('*') per root, so it is done ONCE per resolve and
    // shared by all eight strategies rather than eight times each — a resolve polls every half
    // second until its element appears, and on a large page that difference is the whole cost.
    var currentRoots = null;
    function activeRoots() { return currentRoots || (currentRoots = roots()); }

    // Every root a query should look in: the document, every open shadow root inside it, and every
    // same-origin iframe's document — recursively, because components nest.
    function roots() {
        var found = [];
        walk(document);
        return found;

        function walk(root) {
            found.push(root);
            var all;
            try { all = root.querySelectorAll('*'); } catch (e) { return; }
            for (var i = 0; i < all.length; i++) {
                var el = all[i];
                // Only OPEN roots have a shadowRoot to read; a closed one is null here, which is
                // exactly what "closed" means and not something to work around.
                if (el.shadowRoot) walk(el.shadowRoot);
                if (el.tagName === 'IFRAME') {
                    var doc = null;
                    // Cross-origin: the access itself throws, and that is the whole answer.
                    try { doc = el.contentDocument; } catch (e) { doc = null; }
                    if (doc) walk(doc);
                }
            }
        }
    }

    // A rect inside an iframe is measured against THAT iframe's viewport, but a click is dispatched
    // in the top document's — so walk out through every enclosing frame and add each one's own
    // position. Shadow roots need no adjustment: a shadow tree shares its host document's
    // coordinate space.
    window.__automataViewportRect = function (el) {
        var r = el.getBoundingClientRect();
        var dx = 0, dy = 0;
        var doc = el.ownerDocument;
        var guard = 0;
        while (doc && doc !== document && guard++ < 20) {
            var frame = doc.defaultView && doc.defaultView.frameElement;
            if (!frame) break;
            var fr = frame.getBoundingClientRect();
            dx += fr.left + (frame.clientLeft || 0);
            dy += fr.top + (frame.clientTop || 0);
            doc = frame.ownerDocument;
        }
        return { left: r.left + dx, top: r.top + dy, width: r.width, height: r.height };
    };

    // The root an element actually lives in, for the lookups that are id-scoped rather than
    // document-scoped — a label's `for` and an aria-labelledby both mean "in my own tree".
    function ownRoot(el) {
        var root = el.getRootNode ? el.getRootNode() : document;
        return root && root.getElementById ? root : el.ownerDocument || document;
    }

    function q(sel) {
        var all = activeRoots(), out = [];
        for (var i = 0; i < all.length; i++) {
            var hits;
            try { hits = all[i].querySelectorAll(sel); } catch (e) { continue; }
            for (var j = 0; j < hits.length; j++) if (isVisible(hits[j])) out.push(hits[j]);
        }
        return out;
    }

    /// Every element matching `sel` in every root, visible or not — for strategies that filter
    /// on something other than visibility first.
    function qRaw(sel) {
        var all = activeRoots(), out = [];
        for (var i = 0; i < all.length; i++) {
            var hits;
            try { hits = all[i].querySelectorAll(sel); } catch (e) { continue; }
            for (var j = 0; j < hits.length; j++) out.push(hits[j]);
        }
        return out;
    }

    function tagOk(el, fp) { return !fp.tag || el.tagName.toLowerCase() === fp.tag; }

    function accessibleName(el) {
        var lbl = el.getAttribute('aria-label');
        if (lbl) return norm(lbl);
        var by = el.getAttribute('aria-labelledby');
        if (by) {
            var parts = [];
            var ids = by.split(/\s+/);
            var root = ownRoot(el);
            for (var i = 0; i < ids.length; i++) {
                var ref = root.getElementById ? root.getElementById(ids[i]) : null;
                if (ref) parts.push(norm(ref.textContent));
            }
            return norm(parts.join(' '));
        }
        return '';
    }

    function elText(el) {
        return el.tagName === 'INPUT' ? norm(el.value) : norm(el.textContent);
    }

    window.__automataHighlight = function (el, color) {
        try {
            var r = window.__automataViewportRect(el);
            var box = document.createElement('div');
            box.style.cssText = 'position:fixed;pointer-events:none;z-index:2147483647;' +
                'border:2px solid ' + (color || '#3c82ff') + ';border-radius:3px;' +
                'background:rgba(60,130,255,0.15);transition:opacity .3s;' +
                'left:' + (r.left - 3) + 'px;top:' + (r.top - 3) + 'px;' +
                'width:' + (r.width + 6) + 'px;height:' + (r.height + 6) + 'px;';
            document.body.appendChild(box);
            setTimeout(function () { box.style.opacity = '0'; }, 900);
            setTimeout(function () { if (box.parentNode) box.parentNode.removeChild(box); }, 1200);
        } catch (e) { /* cosmetic only — never let a highlight failure break a resolve */ }
    };

    window.__automataResolve = function (fp, opts) {
        opts = opts || {};
        // Fresh for every attempt: a poll is waiting for the page to CHANGE, so a root list held
        // over from the last one would be exactly the wrong thing to look in.
        currentRoots = null;

        var strategies = [
            ['id', function () {
                if (!fp.id) return [];
                // Every root, because an id is only unique within the tree that holds it — two
                // instances of the same component each have their own #submit.
                var all = activeRoots(), out = [];
                for (var i = 0; i < all.length; i++) {
                    var el = all[i].getElementById ? all[i].getElementById(fp.id) : null;
                    if (el && isVisible(el) && tagOk(el, fp)) out.push(el);
                }
                return out;
            }],
            ['css', function () {
                if (!fp.cssSelector) return [];
                var out = q(fp.cssSelector), keep = [];
                for (var i = 0; i < out.length; i++) if (tagOk(out[i], fp)) keep.push(out[i]);
                return keep;
            }],
            ['name', function () {
                if (!fp.nameAttr || !fp.tag) return [];
                return q(fp.tag + '[name="' + fp.nameAttr.replace(/"/g, '\\"') + '"]');
            }],
            ['class', function () {
                if (!fp.tag || !fp.classList || !fp.classList.length) return [];
                var esc = (window.CSS && CSS.escape) ? CSS.escape : function (s) { return s; };
                var sel = fp.tag;
                for (var i = 0; i < fp.classList.length; i++) sel += '.' + esc(fp.classList[i]);
                return q(sel);
            }],
            ['xpath', function () {
                if (!fp.xPath) return [];
                // Documents only: XPath has no way to cross a shadow boundary, so a shadow root is
                // not a thing this strategy can be asked about. The frames still are.
                var all = activeRoots(), out = [];
                for (var r = 0; r < all.length; r++) {
                    if (!all[r].evaluate) continue;
                    try {
                        var res = all[r].evaluate(
                            fp.xPath, all[r], null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null);
                        for (var i = 0; i < res.snapshotLength; i++) {
                            var el = res.snapshotItem(i);
                            if (el && isVisible(el) && tagOk(el, fp)) out.push(el);
                        }
                    } catch (e) { /* a selector this document cannot answer is simply no match */ }
                }
                return out;
            }],
            ['aria', function () {
                if (!fp.ariaLabel) return [];
                var want = lower(fp.ariaLabel);
                // Union of explicit-role matches and tag matches: a native <button> has the
                // implicit role and never matches [role=button], so the tag half catches it.
                var sel = fp.ariaRole ? '[role="' + fp.ariaRole + '"], ' + (fp.tag || '*') : (fp.tag || '*');
                var all = qRaw(sel);
                var out = [];
                for (var i = 0; i < all.length; i++) {
                    if (isVisible(all[i]) && lower(accessibleName(all[i])) === want) out.push(all[i]);
                }
                // A wrapper (div role=button) and its inner control often share the accessible
                // name — when several match, prefer the ones with the RECORDED tag so a still-
                // unchanged page resolves uniquely instead of failing as ambiguous. When none
                // match the tag (markup changed button→div), keep the full set.
                if (out.length > 1 && fp.tag) {
                    var exactTag = [];
                    for (var j = 0; j < out.length; j++) if (tagOk(out[j], fp)) exactTag.push(out[j]);
                    if (exactTag.length) out = exactTag;
                }
                return out;
            }],
            ['label', function () {
                if (!fp.nearbyLabelText) return [];
                var want = lower(fp.nearbyLabelText);
                var out = [];
                var labels = qRaw('label');
                for (var i = 0; i < labels.length; i++) {
                    if (lower(labels[i].textContent) !== want) continue;
                    var labelRoot = ownRoot(labels[i]);
                    var ctl = labels[i].control ||
                        (labels[i].htmlFor && labelRoot.getElementById
                            ? labelRoot.getElementById(labels[i].htmlFor) : null) ||
                        labels[i].querySelector('input, textarea, select');
                    if (ctl && isVisible(ctl) && tagOk(ctl, fp)) out.push(ctl);
                }
                return out;
            }],
            ['text', function () {
                if (!fp.visibleText || !fp.tag) return [];
                var want = lower(fp.visibleText);
                var all = q(fp.tag);
                var exact = [], fuzzy = [];
                for (var i = 0; i < all.length; i++) {
                    var t = lower(elText(all[i]));
                    if (t === want) exact.push(all[i]);
                    else if (t && t.indexOf(want) !== -1) fuzzy.push(all[i]);
                }
                return exact.length ? exact : fuzzy;
            }]
        ];

        var pool = [];
        function poolAdd(el) {
            for (var i = 0; i < pool.length; i++) if (pool[i] === el) return;
            pool.push(el);
        }

        var winner = null, winnerStrategy = null, winnerScore = 0;
        for (var s = 0; s < strategies.length && !winner; s++) {
            var matches = strategies[s][1]();
            if (matches.length === 1) {
                winner = matches[0];
                winnerStrategy = strategies[s][0];
            } else {
                for (var m = 0; m < matches.length; m++) poolAdd(matches[m]);
            }
        }

        function score(el) {
            var sc = 0;
            if (tagOk(el, fp)) sc += 1;
            if (fp.typeAttr && (el.getAttribute('type') || '').toLowerCase() === fp.typeAttr.toLowerCase()) sc += 1;
            if (fp.visibleText) {
                var t = lower(elText(el)), want = lower(fp.visibleText);
                if (t === want) sc += 3; else if (t && t.indexOf(want) !== -1) sc += 1.5;
            }
            if (fp.ariaLabel && lower(accessibleName(el)) === lower(fp.ariaLabel)) sc += 3;
            if (fp.nameAttr && el.getAttribute('name') === fp.nameAttr) sc += 2;
            if (fp.classList && fp.classList.length) {
                var overlap = 0;
                for (var i = 0; i < fp.classList.length; i++) if (el.classList.contains(fp.classList[i])) overlap++;
                sc += (overlap / fp.classList.length) * 2;
            }
            if (fp.nearbyLabelText) {
                var l = el.closest && el.closest('label');
                var lt = l ? lower(l.textContent) : '';
                if (!lt && el.id) {
                    var f = ownRoot(el).querySelector('label[for="' + el.id + '"]');
                    if (f) lt = lower(f.textContent);
                }
                if (lt && lt === lower(fp.nearbyLabelText)) sc += 2;
            }
            if (fp.placeholder && el.getAttribute('placeholder') === fp.placeholder) sc += 1;
            return sc;
        }

        if (!winner && pool.length) {
            var scored = [];
            for (var p = 0; p < pool.length; p++) scored.push({ el: pool[p], s: score(pool[p]) });
            scored.sort(function (a, b) { return b.s - a.s; });
            var top = scored[0], second = scored[1];
            // Accept only a clear leader — guessing between near-ties clicks the wrong thing.
            if (top.s >= 4 && (!second || top.s - second.s >= 1.5)) {
                winner = top.el;
                winnerStrategy = 'scored';
                winnerScore = top.s;
            } else {
                return JSON.stringify({ found: false, ambiguous: pool.length > 1, candidateCount: pool.length });
            }
        }
        if (!winner) return JSON.stringify({ found: false, ambiguous: false, candidateCount: 0 });

        winner.scrollIntoView({ block: 'center', inline: 'center' });
        // Translated out of any enclosing frame: the click that follows is dispatched against the
        // top document, so a rect measured inside an iframe would aim at the wrong place entirely.
        var rect = window.__automataViewportRect(winner);
        if (opts.highlight) window.__automataHighlight(winner);
        // Handed to the follow-up act script — resolve and act are separate EvalAsync calls.
        window.__automataLastResolved = winner;

        var refreshed = null;
        if (opts.refingerprint && winnerStrategy !== 'id' && winnerStrategy !== 'css' && window.__automataFingerprint) {
            refreshed = window.__automataFingerprint(winner);
        }

        return JSON.stringify({
            found: true,
            unique: winnerStrategy !== 'scored',
            strategy: winnerStrategy,
            score: winnerScore,
            ambiguous: false,
            candidateCount: pool.length || 1,
            centerX: rect.left + rect.width / 2,
            centerY: rect.top + rect.height / 2,
            tag: winner.tagName.toLowerCase(),
            text: elText(winner).slice(0, 120) || null,
            refreshedFingerprint: refreshed
        });
    };
})();
