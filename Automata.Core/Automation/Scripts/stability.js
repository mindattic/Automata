// Decides whether an id or a class is worth writing down.
//
// Recording a generated name poisons the strongest strategies in the cascade: the fingerprint keeps
// an id that will never match again, `id` and `css` miss on every later run, and — because a heal
// only fires when something OTHER than id/css won — the refreshed fingerprint records the NEXT
// generated name, and the task file is rewritten every single run. That is not a hypothetical:
// pointing the acceptance profiles at Google produced exactly it, healing the search box into
// `#ti6dpd` with the class `gLFyf`, neither of which the old patterns caught.
//
// This lived in two places that had drifted apart — fingerprint.js rejected `^css-|^sc-|[0-9a-f]{6,}`
// while harvest.js separately rejected state prefixes, state words and a stricter hash shape — so a
// class could be unstable enough to spoil a harvest and stable enough to record in a fingerprint.
// One file now, prepended to both. Embedded resource in Automata.Core.
(function () {
    'use strict';

    // Names that say what generated them. Cheap, exact, and no risk of catching a real one.
    var KNOWN = [
        /^css-/,                      // emotion
        /^sc-/,                       // styled-components
        /^emotion-/,
        /^jsx-\d/,                    // styled-jsx
        /^svelte-/,
        /^ember\d*/,
        /^radix-/,
        /^headlessui-/,
        /^react-aria-/,
        /^:r[0-9a-z]*:?/,             // React useId
        /^_[A-Za-z0-9]{4,}$/,         // CSS modules, when the hash is the whole name
    ];

    // Shapes, rather than names. A long digit run and a long hex run are what almost every build
    // tool reaches for when it needs a unique suffix.
    var DIGIT_RUN = /\d{4,}/;
    var HEX_RUN = /[0-9a-f]{6,}/i;

    var VOWELS = /[aeiouy]/gi;
    var SEPARATED = /[-_.:]/;

    function upperRunInside(token) {
        // Two capitals in a row, after the first character. `gLFyf` has one; `navBar` and `MuiButton`
        // do not, and neither does an ALLCAPS name, which is a choice a person makes.
        return /[a-z][A-Z]{2,}/.test(token);
    }

    function longestConsonantRun(letters) {
        var longest = 0, current = 0;
        for (var i = 0; i < letters.length; i++) {
            if (/[aeiouy]/i.test(letters[i])) { current = 0; continue; }
            current++;
            if (current > longest) longest = current;
        }
        return longest;
    }

    /// True when a name looks like something a machine produced rather than something a person
    /// chose.
    ///
    /// The known prefixes and the digit/hex runs are decisive on their own. Everything else needs
    /// TWO independent signals before it counts, and only applies to a short name with no separator
    /// at all — because that is the only shape a random token takes, and because one signal on its
    /// own has too many honest counter-examples. `search`, `nav2`, `b_results` and `sb_form_q` all
    /// trip at most one of these, and all four are names a person wrote.
    function looksGenerated(name) {
        if (!name) return false;
        for (var i = 0; i < KNOWN.length; i++) if (KNOWN[i].test(name)) return true;
        if (DIGIT_RUN.test(name)) return true;
        if (HEX_RUN.test(name)) return true;

        if (SEPARATED.test(name)) return false;
        if (name.length < 4 || name.length > 12) return false;

        var letters = name.replace(/[^A-Za-z]/g, '');
        var vowels = (letters.match(VOWELS) || []).length;
        var signals = 0;

        // A digit in the middle of a word. A trailing one is how people number things — `nav2`,
        // `tab1`, `h3` — and is kept.
        if (/\d/.test(name.slice(0, -1))) signals++;
        if (upperRunInside(name)) signals++;
        if (letters.length > 0 && vowels / letters.length < 0.25) signals++;
        if (longestConsonantRun(letters) >= 4) signals++;

        return signals >= 2;
    }

    /// State classes: not generated, but not identity either. A row that is `.selected` right now
    /// will not be next run, and generalising one tile into "the selected tile" finds one tile.
    function isStateClass(name) {
        return /^(is-|has-|js-)/.test(name)
            || /^(active|selected|current|hover|focus|open|disabled|checked|expanded)$/i.test(name);
    }

    window.__automataStability = {
        /// An id worth recording, or null.
        stableId: function (id) {
            return id && !looksGenerated(id) ? id : null;
        },

        /// The classes worth recording, in order. Both callers want the same answer: a class that
        /// varies per render spoils a harvest's row selector and a fingerprint's class strategy in
        /// exactly the same way.
        stableClasses: function (list) {
            var out = [];
            for (var i = 0; i < list.length; i++) {
                var c = list[i];
                if (!c) continue;
                if (isStateClass(c)) continue;
                if (looksGenerated(c)) continue;
                out.push(c);
            }
            return out;
        },

        looksGenerated: looksGenerated,
        isStateClass: isStateClass,
    };
})();
