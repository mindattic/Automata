// Capture script for the TARGET pane. Installed dormant on every document (the host prepends
// Automata.Core's embedded fingerprint.js and injects both via AddScriptToExecuteOnDocumentCreated);
// the Record button enables it. Listeners sit on document in the CAPTURE phase so pages that
// stopPropagation can't hide events. This file is read from disk by the host — never served.
(function () {
    'use strict';
    if (window.__automataRecorder) return;

    var enabled = false;

    function post(payload) {
        try {
            payload.source = 'automata-recorder';
            payload.url = location.href;
            payload.ts = Date.now();
            window.chrome.webview.postMessage(payload);
        } catch (e) { /* host bridge unavailable — nothing to do */ }
    }

    function targetKind(el) {
        var tag = el.tagName.toLowerCase();
        if (tag === 'option') return 'option';
        if (tag === 'select') return 'select';
        if (tag === 'textarea') return 'text';
        if (tag === 'input') {
            var ty = (el.getAttribute('type') || 'text').toLowerCase();
            if (ty === 'checkbox') return 'checkbox';
            if (ty === 'radio') return 'radio';
            if (ty === 'file') return 'file';
            if (ty === 'submit' || ty === 'button' || ty === 'reset' || ty === 'image') return 'button';
            return 'text';
        }
        var role = el.getAttribute('role');
        if (role === 'checkbox') return 'checkbox';
        if (role === 'radio') return 'radio';
        if (tag === 'button' || role === 'button' || tag === 'a' || role === 'link') return 'button';
        if (el.isContentEditable) return 'text';
        return 'other';
    }

    function isMasked(el) {
        return el.tagName === 'INPUT' && (el.getAttribute('type') || '').toLowerCase() === 'password';
    }

    // Click targets are often a <span> deep inside the actual control — lift to the control.
    function actionTarget(el) {
        if (!el || !el.closest) return el;
        return el.closest('button, a, input, select, textarea, option, label, [role=button], [role=checkbox], [role=radio], [role=link]') || el;
    }

    function onClick(e) {
        if (!enabled) return;
        var el = actionTarget(e.target);
        if (!el || el.nodeType !== 1) return;
        if (el.tagName === 'LABEL') {
            var ctl = el.control ||
                (el.htmlFor ? document.getElementById(el.htmlFor) : null) ||
                el.querySelector('input, textarea, select');
            if (ctl) el = ctl;
        }
        var kind = targetKind(el);
        var payload = { kind: 'click', targetKind: kind, fingerprint: window.__automataFingerprint(el) };
        // Checkbox/radio activation flips `checked` BEFORE the click event fires — this is the
        // post-click state, exactly what the coalescer wants.
        if (kind === 'checkbox' || kind === 'radio') {
            payload.checked = el.tagName === 'INPUT' ? el.checked : el.getAttribute('aria-checked') === 'true';
        }
        if (kind === 'option') payload.value = (el.textContent || '').trim();
        post(payload);
    }

    function onInput(e) {
        if (!enabled) return;
        var el = e.target;
        if (!el || el.nodeType !== 1 || targetKind(el) !== 'text') return;
        var masked = isMasked(el);
        post({
            kind: 'input', targetKind: 'text', masked: masked,
            value: masked ? '' : (el.isContentEditable ? el.textContent : el.value),
            fingerprint: window.__automataFingerprint(el)
        });
    }

    function onChange(e) {
        if (!enabled) return;
        var el = e.target;
        if (!el || el.nodeType !== 1) return;
        var kind = targetKind(el);
        var masked = isMasked(el);
        var payload = { kind: 'change', targetKind: kind, masked: masked, fingerprint: window.__automataFingerprint(el) };
        if (kind === 'checkbox' || kind === 'radio') {
            payload.checked = el.tagName === 'INPUT' ? el.checked : el.getAttribute('aria-checked') === 'true';
        } else if (kind === 'select') {
            payload.selectedText = el.selectedIndex >= 0 ? (el.options[el.selectedIndex].textContent || '').trim() : '';
        } else if (kind === 'file') {
            // Only the NAME is readable from JS — the local path never is. The editor prompts
            // the user for a real path before this step can replay.
            payload.value = el.files && el.files.length ? el.files[0].name : '';
        } else {
            payload.value = masked ? '' : el.value;
        }
        post(payload);
    }

    document.addEventListener('click', onClick, true);
    document.addEventListener('input', onInput, true);
    document.addEventListener('change', onChange, true);

    window.__automataRecorder = {
        enable: function () { enabled = true; },
        disable: function () { enabled = false; },
        isEnabled: function () { return enabled; }
    };
})();
