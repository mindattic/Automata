// Computes a multi-strategy identity (ElementFingerprint) for a DOM element. Embedded resource
// in Automata.Core; prepended to both the recorder (capture) and the resolver (replay) scripts.
// Keys mirror the C# ElementFingerprint through AutomataJson's camelCase policy (XPath → xPath).
(function () {
    'use strict';

    // Ids/classes that look machine-generated (hashed CSS-in-JS, framework counters) change on
    // every build or render — recording them would poison the fingerprint's strongest strategies.
    var AUTO_ID = /\d{4,}|^ember|^radix|^:r/;
    var AUTO_CLASS = /^css-|^sc-|[0-9a-f]{6,}/;

    function norm(t) { return (t || '').replace(/\s+/g, ' ').trim(); }
    function esc(s) { return (window.CSS && CSS.escape) ? CSS.escape(s) : s.replace(/([^a-zA-Z0-9_-])/g, '\\$1'); }

    function stableId(el) {
        var id = el.getAttribute && el.getAttribute('id');
        return id && !AUTO_ID.test(id) ? id : null;
    }

    function stableClasses(el) {
        var out = [];
        for (var i = 0; i < el.classList.length; i++) {
            var c = el.classList[i];
            if (!AUTO_CLASS.test(c)) out.push(c);
        }
        return out;
    }

    function isUnique(sel) {
        try { return document.querySelectorAll(sel).length === 1; } catch (e) { return false; }
    }

    function cssSelector(el) {
        var id = stableId(el);
        if (id) return '#' + esc(id);
        var tag = el.tagName.toLowerCase();
        var name = el.getAttribute('name');
        if (name) {
            var byName = tag + '[name="' + name.replace(/"/g, '\\"') + '"]';
            if (isUnique(byName)) return byName;
        }
        var classes = stableClasses(el).slice(0, 2);
        if (classes.length) {
            var byClass = tag;
            for (var i = 0; i < classes.length; i++) byClass += '.' + esc(classes[i]);
            if (isUnique(byClass)) return byClass;
        }
        // Positional path up to the nearest stable-id ancestor (or body).
        var parts = [], node = el;
        while (node && node.nodeType === 1 && node !== document.body) {
            var nid = stableId(node);
            if (nid) { parts.unshift('#' + esc(nid)); break; }
            var idx = 1, sib = node;
            while ((sib = sib.previousElementSibling)) if (sib.tagName === node.tagName) idx++;
            parts.unshift(node.tagName.toLowerCase() + ':nth-of-type(' + idx + ')');
            node = node.parentElement;
        }
        return parts.join(' > ');
    }

    function xPath(el) {
        var parts = [];
        for (var node = el; node && node.nodeType === 1; node = node.parentElement) {
            var idx = 1, sib = node.previousElementSibling;
            for (; sib; sib = sib.previousElementSibling) if (sib.tagName === node.tagName) idx++;
            parts.unshift(node.tagName.toLowerCase() + '[' + idx + ']');
        }
        return '/' + parts.join('/');
    }

    function visibleText(el) {
        var t;
        if (el.tagName === 'INPUT') {
            // A text input's value is user data, not identity; only button-ish values label the element.
            var ty = (el.getAttribute('type') || '').toLowerCase();
            t = (ty === 'submit' || ty === 'button' || ty === 'reset') ? el.value : '';
        } else {
            t = el.textContent;
        }
        t = norm(t);
        return t ? t.slice(0, 120) : null;
    }

    var IMPLICIT_ROLES = { a: 'link', button: 'button', select: 'combobox', textarea: 'textbox' };
    var INPUT_ROLES = {
        checkbox: 'checkbox', radio: 'radio', submit: 'button', button: 'button', reset: 'button',
        text: 'textbox', search: 'searchbox', email: 'textbox', password: 'textbox',
        tel: 'textbox', url: 'textbox', number: 'spinbutton', range: 'slider'
    };

    function ariaRole(el) {
        var explicit = el.getAttribute('role');
        if (explicit) return explicit;
        var tag = el.tagName.toLowerCase();
        if (tag === 'input') return INPUT_ROLES[(el.getAttribute('type') || 'text').toLowerCase()] || 'textbox';
        if (tag === 'a') return el.hasAttribute('href') ? 'link' : null;
        return IMPLICIT_ROLES[tag] || null;
    }

    function ariaLabel(el) {
        var lbl = el.getAttribute('aria-label');
        if (lbl) return norm(lbl).slice(0, 120) || null;
        var by = el.getAttribute('aria-labelledby');
        if (by) {
            var texts = [];
            var ids = by.split(/\s+/);
            for (var i = 0; i < ids.length; i++) {
                var ref = document.getElementById(ids[i]);
                if (ref) { var t = norm(ref.textContent); if (t) texts.push(t); }
            }
            if (texts.length) return texts.join(' ').slice(0, 120);
        }
        return null;
    }

    function nearbyLabelText(el) {
        var l = el.closest && el.closest('label');
        if (l) { var lt = norm(l.textContent); if (lt) return lt.slice(0, 120); }
        if (el.id) {
            var f = document.querySelector('label[for="' + esc(el.id) + '"]');
            if (f) { var ft = norm(f.textContent); if (ft) return ft.slice(0, 120); }
        }
        // Same nearest-first ancestor walk the engine's checkbox helpers use.
        var node = el;
        for (var d = 0; d < 5 && node.parentElement; d++) {
            node = node.parentElement;
            var cand = node.querySelector('label, legend, h1, h2, h3, h4, h5, h6');
            if (cand && cand !== el) {
                var ct = norm(cand.textContent);
                if (ct) return ct.slice(0, 120);
            }
        }
        return null;
    }

    window.__automataFingerprint = function (el) {
        return {
            id: stableId(el),
            cssSelector: cssSelector(el) || null,
            xPath: xPath(el),
            tag: el.tagName.toLowerCase(),
            classList: stableClasses(el),
            nameAttr: el.getAttribute('name') || null,
            typeAttr: el.getAttribute('type') || null,
            visibleText: visibleText(el),
            ariaRole: ariaRole(el),
            ariaLabel: ariaLabel(el),
            nearbyLabelText: nearbyLabelText(el),
            placeholder: el.getAttribute('placeholder') || null
        };
    };
})();
