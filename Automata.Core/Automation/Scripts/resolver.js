// Resolves an ElementFingerprint against the live DOM via a fixed path-of-least-resistance
// cascade; the first strategy yielding exactly ONE visible match wins. When nothing is unique,
// candidates are pooled and scored — a clear leader wins, a near-tie is reported as ambiguous
// rather than guessed at. Embedded resource in Automata.Core; requires fingerprint.js to be
// evaluated first (for __automataFingerprint) when refingerprint is requested.
(function () {
    'use strict';

    function norm(t) { return (t || '').replace(/\s+/g, ' ').trim(); }
    function lower(t) { return norm(t).toLowerCase(); }

    function isVisible(el) {
        var r = el.getBoundingClientRect();
        return r.width > 0 && r.height > 0;
    }

    function q(sel) {
        try { return Array.prototype.filter.call(document.querySelectorAll(sel), isVisible); }
        catch (e) { return []; }
    }

    function tagOk(el, fp) { return !fp.tag || el.tagName.toLowerCase() === fp.tag; }

    function accessibleName(el) {
        var lbl = el.getAttribute('aria-label');
        if (lbl) return norm(lbl);
        var by = el.getAttribute('aria-labelledby');
        if (by) {
            var parts = [];
            var ids = by.split(/\s+/);
            for (var i = 0; i < ids.length; i++) {
                var ref = document.getElementById(ids[i]);
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
            var r = el.getBoundingClientRect();
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

        var strategies = [
            ['id', function () {
                if (!fp.id) return [];
                var el = document.getElementById(fp.id);
                return el && isVisible(el) && tagOk(el, fp) ? [el] : [];
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
                try {
                    var res = document.evaluate(fp.xPath, document, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null);
                    var out = [];
                    for (var i = 0; i < res.snapshotLength; i++) {
                        var el = res.snapshotItem(i);
                        if (el && isVisible(el) && tagOk(el, fp)) out.push(el);
                    }
                    return out;
                } catch (e) { return []; }
            }],
            ['aria', function () {
                if (!fp.ariaLabel) return [];
                var want = lower(fp.ariaLabel);
                // Union of explicit-role matches and tag matches: a native <button> has the
                // implicit role and never matches [role=button], so the tag half catches it.
                var sel = fp.ariaRole ? '[role="' + fp.ariaRole + '"], ' + (fp.tag || '*') : (fp.tag || '*');
                var all = document.querySelectorAll(sel);
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
                var labels = document.querySelectorAll('label');
                for (var i = 0; i < labels.length; i++) {
                    if (lower(labels[i].textContent) !== want) continue;
                    var ctl = labels[i].control ||
                        (labels[i].htmlFor ? document.getElementById(labels[i].htmlFor) : null) ||
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
                    var f = document.querySelector('label[for="' + el.id + '"]');
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
        var rect = winner.getBoundingClientRect();
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
