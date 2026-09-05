// Keeps a list of the CLOSED shadow roots a document creates, so the resolver can look inside
// them. Embedded resource in Automata.Core.
//
// A closed root is unreachable by design: `attachShadow({ mode: 'closed' })` returns the root to
// whoever asked for it and leaves `host.shadowRoot` null forever after, so no amount of walking
// the DOM finds the way in. There is exactly one moment when the root is visible to anyone but the
// component — the moment it is handed back — and this file is there for it.
//
// Which is why this script, alone among the injected ones, MUST run at document-creation time,
// before a single line of the page's own script. Injected after load it patches nothing that has
// not already happened, and every root the page built during startup stays shut. That is the
// honest limit and it is worth stating plainly: a closed root created before we arrive is not
// reachable, and never becomes reachable.
//
// The registry is per DOCUMENT (it hangs off that document's window), because a frame's roots
// belong to the frame. resolver.js collects them from every document it can reach, and asks the
// frames it cannot reach to collect their own.
(function () {
    'use strict';
    if (window.__automataClosedRoots) return;

    // A plain array, not a WeakSet: this has to be ITERABLE — the whole point is to enumerate the
    // roots nothing else can find. Detached ones are filtered at read time (by host.isConnected)
    // rather than removed here, because there is no event that fires when a host leaves the page.
    var registry = [];
    window.__automataClosedRoots = registry;

    var native = Element.prototype.attachShadow;
    if (typeof native !== 'function') return;

    Element.prototype.attachShadow = function (init) {
        var root = native.call(this, init);
        // Only the closed ones. An open root is already reachable through `host.shadowRoot`, and
        // holding a second reference to it would be a leak bought for nothing.
        try { if (init && init.mode === 'closed') registry.push(root); } catch (e) { /* never break the page */ }
        return root;
    };
})();
