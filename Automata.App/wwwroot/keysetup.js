// A one-time nudge, on the first settings this session, for a fresh install that has never had a
// working LLM key: Automata is driven by an LLM of your choice, and free-text task authoring and
// self-heal repair have nothing to call until at least one provider is configured — this is the
// thing a brand-new user most needs to be told before they conclude the app is broken. Checked
// once — not persisted — so it asks again next launch if it is still true, the same way the
// first-run tutorial's own "have I checked yet" guard works.

import { $ } from './core.js';
import { openConfirmModal } from './modal.js';

var checked = false;

/// `payload` is the same object the host just pushed to onSettings — `anyConfigured` is computed
/// there, live, against the actual credential chain (never assumed), so this never nags about a
/// default that secretly does work.
export function maybeAskForKey(payload) {
    if (checked) return;
    checked = true;
    if (!payload || payload.anyConfigured) return;

    var provider = payload.provider || 'claude';
    openConfirmModal('Bring your own API key',
        "Automata is driven by the LLM of your choice — add an API key (Claude, OpenAI, Gemini, " +
        "or Kimi) to turn it on. Without one, free-text task authoring and self-heal repair have " +
        "nothing to call; recording, editing and replaying steps you've already built still works " +
        "either way. Add a key now, or later from Settings.",
        'Add a key',
        function () {
            $('btn-settings').click();
            var input = $('key-' + provider);
            if (input) input.focus();
        });
}
