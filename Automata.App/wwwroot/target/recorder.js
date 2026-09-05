// Capture script for the TARGET pane. Installed dormant on every document — the top one AND every
// frame inside it, at any depth and any origin, because the host injects it with
// AddScriptToExecuteOnDocumentCreated; the Record button enables it. Listeners sit on document in
// the CAPTURE phase so pages that stopPropagation can't hide events. This file is read from disk by
// the host — never served.
//
// Being in every frame is not the same as WORKING in every frame, and the two things that had to be
// arranged are both about which document a message can reach:
//
//   * A frame's `chrome.webview` posts to that frame's own WebMessageReceived, which nothing is
//     listening to. So an event captured in a frame travels OUT through frames.js, parent by
//     parent, and only the top document hands it to the host.
//   * Each document has its own copy of this script and its own `enabled` flag. Arming the top one
//     arms one document, so the Record button's command travels IN the same way, to every frame
//     below, at any depth.
//
// A CLOSED shadow root is still out of reach for recording, and unlike the rest of that family it
// is not a plumbing problem: an event leaving a closed root is retargeted to the host element with
// an EMPTY composedPath, so there is nothing to read. Replaying into one works; watching someone
// click inside one does not.
(function () {
    'use strict';
    if (window.__automataRecorder) return;

    var enabled = false;
    var inFrame = window.top !== window;

    function post(payload) {
        payload.source = 'automata-recorder';
        payload.url = location.href;
        payload.ts = Date.now();
        // In a frame, up the bridge; the top document is the only one with a host to talk to.
        if (inFrame && window.__automataPostUp && window.__automataPostUp(payload)) return;
        try {
            window.chrome.webview.postMessage(payload);
        } catch (e) { /* host bridge unavailable — nothing to do */ }
    }

    /// What a frame's event does when it finally reaches the top document. Set only here, because
    /// only here is there a host to hand it to.
    if (!inFrame) {
        window.__automataOnFrameEvent = function (payload) {
            try { window.chrome.webview.postMessage(payload); } catch (e) { /* nothing to do */ }
        };
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

    // ---- harvest picking ---------------------------------------------------------------------
    // A one-shot mode, separate from recording: the next click is CONSUMED rather than replayed,
    // because the whole point is to indicate an element, and letting a product tile's link
    // navigate would take the page out from under the thing being picked. Answered by harvest.js,
    // which rides along on the same injection.
    var pending = null;   // { mode: 'row' | 'field', itemSelector }

    function onPickClick(e) {
        if (!pending) return false;
        e.preventDefault();
        e.stopPropagation();
        e.stopImmediatePropagation();

        var request = pending;
        pending = null;
        var el = realTarget(e);
        var answer;
        try {
            answer = request.mode === 'row'
                ? window.__automataPickSet(el)
                : window.__automataPickField(el, request.itemSelector);
        } catch (err) {
            answer = JSON.stringify({ ok: false, error: String(err && err.message || err) });
        }

        var payload = JSON.parse(answer);
        payload.kind = 'pick';
        payload.mode = request.mode;
        // The field's own fingerprint comes along so a picked field can also be used as an
        // ordinary step target later, without asking the user to point at it a second time.
        try { payload.fingerprint = window.__automataFingerprint(el); } catch (err) { /* optional */ }
        post(payload);
        return true;
    }

    // What was really clicked. An event that crosses an open shadow boundary is RETARGETED on the
    // way out — e.target becomes the component's host element, so recording it would produce a step
    // that clicks the wrapper and never the control inside. composedPath()[0] is the element the
    // user actually hit; for an ordinary page it is e.target and nothing changes.
    //
    // An iframe never delivers its events here either, and the answer to that one was to be in
    // there as well rather than to reach further from here — see the header. A CLOSED root remains
    // out of reach: it retargets with an empty path, so there is nothing to read.
    function realTarget(e) {
        var path = e.composedPath ? e.composedPath() : null;
        return (path && path.length ? path[0] : e.target);
    }

    function onClick(e) {
        if (onPickClick(e)) return;
        if (!enabled) return;
        var el = actionTarget(realTarget(e));
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
        var el = realTarget(e);
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
        var el = realTarget(e);
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

    function onKeydown(e) {
        if (!enabled || e.key !== 'Enter') return;
        var el = realTarget(e);
        if (!el || el.nodeType !== 1 || targetKind(el) !== 'text') return;
        post({ kind: 'key', value: 'Enter', targetKind: 'text', fingerprint: window.__automataFingerprint(el) });
    }

    document.addEventListener('click', onClick, true);
    document.addEventListener('input', onInput, true);
    document.addEventListener('change', onChange, true);
    document.addEventListener('keydown', onKeydown, true);

    // A pick has to swallow the mousedown too, or a link follows before the click ever arrives.
    document.addEventListener('mousedown', function (e) {
        if (!pending) return;
        e.preventDefault();
        e.stopPropagation();
        e.stopImmediatePropagation();
    }, true);

    // ---- one command, every document ------------------------------------------------------------
    //
    // The host only ever talks to the top document, so everything the Record button does has to be
    // repeated in every frame below. Applied here first, then passed on down by frames.js — which
    // is what makes a frame's recorder arm at the same moment as the top one's, rather than
    // whenever somebody remembers to arm it.
    function apply(command) {
        if (command.what === 'enable') enabled = true;
        else if (command.what === 'disable') enabled = false;
        else if (command.what === 'pick') {
            pending = { mode: command.mode === 'field' ? 'field' : 'row', itemSelector: command.itemSelector || '' };
        } else if (command.what === 'cancelPick') pending = null;
    }

    window.__automataOnFrameCommand = apply;

    function command(payload) {
        apply(payload);
        if (window.__automataPostDown) window.__automataPostDown(payload);
    }

    window.__automataRecorder = {
        enable: function () { command({ what: 'enable' }); },
        disable: function () { command({ what: 'disable' }); },
        isEnabled: function () { return enabled; },

        /// Arms a one-shot pick, here and in every frame. 'row' generalises the click into
        /// "everything like this"; 'field' locates it relative to the row set that a previous 'row'
        /// pick found. Armed everywhere because the user points at a thing on screen and has no
        /// reason to know, or care, which document drew it.
        pick: function (mode, itemSelector) {
            command({ what: 'pick', mode: mode, itemSelector: itemSelector || '' });
        },
        cancelPick: function () { command({ what: 'cancelPick' }); },
        isPicking: function () { return pending !== null; }
    };
})();
