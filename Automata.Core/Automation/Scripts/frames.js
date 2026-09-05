// Reaches into iframes whose documents this document is not allowed to read. Embedded resource in
// Automata.Core; requires resolver.js (for __automataResolveLocal and __automataReachableRoots).
//
// resolver.js walks every root it CAN see — the document, open shadow roots, same-origin frames.
// A cross-origin frame is not one of those: `frame.contentDocument` throws, and no selector, however
// long, gets past that. The usual answer is to stop running one script in the top document and start
// evaluating in each frame's own execution context over CDP, reconciling coordinates afterwards.
//
// This takes the other road, and it is worth saying why. The host injects its scripts with
// AddScriptToExecuteOnDocumentCreated, which applies to CHILD FRAMES as well as the top document —
// so the resolver is already running inside that cross-origin page. Nothing needs to reach in. The
// two copies only need to be able to TALK, and `postMessage` crosses origins by design; it is the
// one channel the same-origin policy leaves open on purpose.
//
// So the shape is: the top document resolves locally, and if that fails it asks each child frame it
// cannot see into, "do you have this element?". Each child answers for itself and, if it cannot,
// asks ITS unreachable children the same question. The answer comes back up the tree, and every hop
// adds its own frame's position — which is the coordinate reconciliation, done by the only party
// that can do it. A cross-origin child cannot know where it sits on the page (`window.frameElement`
// throws), but its parent knows exactly, because it owns the <iframe> element.
//
// What this costs, stated once:
//
//   * A frame with NO scripting — `sandbox` without `allow-scripts` — never runs our copy and
//     therefore never answers. Nothing else could reach in there either; it has no script at all.
//   * Forwarding an ACTION means shipping a function body into the frame and calling `new Function`
//     on it. A frame whose CSP forbids `unsafe-eval` refuses that, and the step fails saying so.
//     The RESOLVE half never evals anything, so finding an element in such a frame still works —
//     which is exactly the information a person needs to know where the wall is. The top document's
//     own path never goes near this: its action body is inlined into the evaluated script, as it
//     always was, so a strict CSP on the page itself changes nothing that used to work.
//   * Messages go out with a targetOrigin of '*', because the whole point is that we do not know
//     the child's origin. The payload is the fingerprint the user recorded off that very page.
(function () {
    'use strict';
    // Installed ONCE per document, unlike the rest of the bundle. The others are pure redefinitions
    // and survive being injected on every poll; this one owns state that outlives a single call —
    // the frame the last resolve went through, and the calls still waiting for an answer — and a
    // second copy would take over the globals while the first copy held the in-flight promises.
    if (window.__automataFrames) return;

    // Version the key: a message shape that changed under a page holding an old copy would be worse
    // than no answer at all.
    var KEY = 'automata-frame/1';

    // How long a child gets to answer. Generous enough for a frame that is still parsing, short
    // enough that several dead frames do not eat a step's whole timeout — the caller polls, so a
    // miss here costs one poll and not the step.
    var ANSWER_MS = 1200;

    var pending = {};
    var seq = 0;

    /// Every child frame of this document that our own script CANNOT read into.
    ///
    /// Searched across every reachable root rather than just `document`, so a cross-origin frame
    /// nested inside a same-origin one — or inside a shadow root — is found too. The test is the
    /// access itself: a same-origin frame hands over its document, a cross-origin one throws or
    /// answers null, and that IS the question being asked.
    ///
    /// That is a second root walk on top of the resolve's own, and it is paid only by a resolve that
    /// has already FAILED locally — which is the slow path anyway, polling every half second. A
    /// cached list would save one walk and cost the ability to notice a frame that has just
    /// appeared, which is precisely what a poll is waiting for.
    function unreachable() {
        var out = [];
        var roots = window.__automataReachableRoots ? window.__automataReachableRoots() : [document];
        for (var r = 0; r < roots.length; r++) {
            var frames;
            try { frames = roots[r].querySelectorAll('iframe, frame'); } catch (e) { continue; }
            for (var i = 0; i < frames.length; i++) {
                var el = frames[i], doc = null, win = null;
                try { doc = el.contentDocument; } catch (e) { doc = null; }
                try { win = el.contentWindow; } catch (e) { win = null; }
                if (!doc && win) out.push({ el: el, win: win });
            }
        }
        return out;
    }

    function send(win, payload) {
        var id = KEY + '#' + (++seq);
        payload.__automata = KEY;
        payload.id = id;
        return new Promise(function (resolve, reject) {
            var timer = setTimeout(function () {
                delete pending[id];
                reject(new Error('the frame did not answer within ' + ANSWER_MS + 'ms'));
            }, ANSWER_MS);
            // The window is recorded alongside the callback so a reply can be checked against the
            // frame it was asked of. A page that guessed an id could otherwise answer for its
            // neighbour, and the resolver would click whatever it was told to.
            pending[id] = {
                win: win,
                settle: function (msg) { clearTimeout(timer); delete pending[id]; resolve(msg.result); }
            };
            try {
                win.postMessage(payload, '*');
            } catch (e) {
                clearTimeout(timer);
                delete pending[id];
                reject(e);
            }
        });
    }

    /// The frame element the last successful deep resolve went through — the DIRECT child on the
    /// path, not the frame the element ended up in. Each hop knows only its own next step, which is
    /// all it needs: an action is forwarded the same way the resolve came back.
    var resolvedFrame = null;

    /// Ask every unreachable child for this fingerprint, in parallel. Resolves to a result object in
    /// THIS document's coordinates, or null when no child has it.
    function askResolve(fp, opts) {
        var kids = unreachable();
        if (!kids.length) return Promise.resolve(null);

        var calls = kids.map(function (kid) {
            return send(kid.win, { op: 'resolve', fp: fp, opts: opts || {} }).then(
                function (r) { return (r && r.found) ? { kid: kid, result: r } : null; },
                function () { return null; });
        });

        return Promise.all(calls).then(function (answers) {
            var hits = answers.filter(function (a) { return a !== null; });
            if (!hits.length) return null;
            // Two frames both holding the element is the frame-level shape of the same near-tie the
            // scoring pass refuses to guess at. Report it rather than pick the first one.
            if (hits.length > 1) {
                return { found: false, unique: false, ambiguous: true, candidateCount: hits.length };
            }

            var hit = hits[0];
            // The element scrolled itself into the middle of ITS OWN viewport, which says nothing
            // about where that viewport sits in this one — so bring the frame into view too, and
            // only then measure where it landed.
            try { hit.kid.el.scrollIntoView({ block: 'center', inline: 'center' }); } catch (e) { /* fixed-position frames */ }

            var box = window.__automataViewportRect(hit.kid.el);
            var dx = box.left + (hit.kid.el.clientLeft || 0);
            var dy = box.top + (hit.kid.el.clientTop || 0);

            resolvedFrame = hit.kid.el;
            var out = {};
            for (var k in hit.result) if (Object.prototype.hasOwnProperty.call(hit.result, k)) out[k] = hit.result[k];
            out.centerX = hit.result.centerX + dx;
            out.centerY = hit.result.centerY + dy;
            out.frameDepth = (hit.result.frameDepth || 0) + 1;
            return out;
        });
    }

    /// Forward one action to the frame the last resolve went through. Resolves to the JSON STRING
    /// the action produced — the same text an action run in this document would have returned.
    function askAct(spec) {
        var frameEl = resolvedFrame;
        if (!frameEl) return Promise.reject(new Error('nothing was resolved in a frame'));
        var win = null;
        try { win = frameEl.contentWindow; } catch (e) { win = null; }
        if (!win) return Promise.reject(new Error('the frame the element was in has gone'));
        return send(win, { op: 'act', target: spec.target, body: spec.body });
    }

    /// Run an action body here, with `el` bound. Only ever reached inside a frame that was asked to
    /// act; the top document inlines its body instead — see the header for why that distinction
    /// matters under a Content-Security-Policy.
    function actHere(target, body) {
        var el = target === 'active' ? document.activeElement : window.__automataLastResolved;
        if (!el || !el.getBoundingClientRect) {
            return JSON.stringify({ ok: false, error: 'no element resolved in that frame' });
        }
        try {
            var out = new Function('el', body)(el);
            return typeof out === 'string' ? out : JSON.stringify({ ok: false, error: 'the action returned nothing' });
        } catch (e) {
            // A frame whose CSP forbids eval lands here, and saying so is the point: the element WAS
            // found, and only the acting is refused.
            return JSON.stringify({ ok: false, error: 'the frame refused the action: ' + String(e && e.message || e) });
        }
    }

    // ---- the responder ---------------------------------------------------------------------------

    window.addEventListener('message', function (e) {
        var msg = e.data;
        if (!msg || msg.__automata !== KEY) return;

        // A reply to something WE asked. Checked against the window it was asked of, so a page that
        // guessed an id cannot answer on another frame's behalf.
        if (msg.re) {
            var call = pending[msg.re];
            if (call && call.win === e.source) call.settle(msg);
            return;
        }

        // A request. Only our own parent may ask — a request arriving from anywhere else is a page
        // trying to drive us, not the host.
        if (!msg.op || e.source !== window.parent) return;
        var reply = function (result) {
            try { e.source.postMessage({ __automata: KEY, re: msg.id, result: result }, '*'); }
            catch (err) { /* the asker has gone; nothing to answer to */ }
        };

        if (msg.op === 'resolve') {
            var local = window.__automataResolveLocal(msg.fp, msg.opts || {});
            if (local.found) { resolvedFrame = null; reply(local); return; }
            // Not here — but perhaps in one of MY unreachable children. The same walk, one level
            // down, and the answer comes back through this frame with this frame's offsets added.
            askResolve(msg.fp, msg.opts || {}).then(
                function (deep) { reply(deep || local); },
                function () { reply(local); });
            return;
        }

        if (msg.op === 'act') {
            if (!resolvedFrame) { reply(actHere(msg.target, msg.body)); return; }
            askAct({ target: msg.target, body: msg.body }).then(reply, function (err) {
                reply(JSON.stringify({ ok: false, error: String(err && err.message || err) }));
            });
        }
    }, false);

    /// What an action script calls when the element it means to act on is not in this document.
    ///
    /// Same shape as the resolve above and for the same reason: an answer that has to travel through
    /// a frame cannot come back inside one synchronous call, so the first call starts the errand and
    /// says it is waiting, and the caller's next attempt collects it. The caller here is
    /// BrowserActions, which polls a few times a second rather than twice.
    window.__automataActInFrame = function (target, body) {
        var sig = JSON.stringify([target, body]);
        var state = window.__automataDeepAct;
        if (!state || state.sig !== sig) {
            state = window.__automataDeepAct = { sig: sig, done: false, result: null };
            askAct({ target: target, body: body }).then(
                function (r) { state.result = r; state.done = true; },
                function (err) {
                    state.result = JSON.stringify({ ok: false, error: String(err && err.message || err) });
                    state.done = true;
                });
            return JSON.stringify({ ok: false, waitingOnFrames: true });
        }
        if (!state.done) return JSON.stringify({ ok: false, waitingOnFrames: true });
        window.__automataDeepAct = null;
        return state.result;
    };

    window.__automataFrames = {
        unreachable: unreachable,
        askResolve: askResolve,
        askAct: askAct,
        get resolvedFrame() { return resolvedFrame; },
        set resolvedFrame(v) { resolvedFrame = v; }
    };
})();
