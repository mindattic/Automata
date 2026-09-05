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
    //
    // Everything here was seen on a real page — tools/collect-names.mjs visits a spread of real
    // sites and sorts what they use into what this file would keep and what it would throw away,
    // which is how the last four entries were found. A rule invented at a desk catches what its
    // author imagined; these catch what shipped.
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
        // React useId, ANYWHERE rather than only at the front. react.dev ships
        // `react-collapsed-panel-:R24m6:` — a readable name with a generated id welded on the end,
        // and an anchored pattern sees a perfectly good name.
        /:[rR][0-9a-z]*:/,
        /^_[A-Za-z0-9]{4,}$/,         // CSS modules, when the hash is the whole name
        // React 19's useId, which changed shape: `_R_5knd_`, all over github.com.
        /^_R_[A-Za-z0-9]*_$/,
        // CSS Modules under its commonest template, `[name]__[local]__[hash]`. An entire
        // convention was walking through: every class on github.com looks like
        // `HeaderSearch-module__icon__wcrHX`, which has separators and so never even reached the
        // shape tests below.
        /-module__/,
        /_module__/,
    ];

    // Shapes, rather than names.
    //
    // FIVE digits, not four, and the difference is a year. `skin-vector-2022` is a class Wikipedia
    // wrote on purpose; `question-summary-80000853` is a row id Stack Overflow minted. Four was
    // catching both.
    var DIGIT_RUN = /\d{5,}/;

    // A name that is nothing but digits was minted by a counter — Hacker News item ids, Bing's
    // `5607`. Nobody writes one on purpose, so this is decisive where a digit RUN inside a longer
    // name is only a signal.
    var ALL_DIGITS = /^\d+$/;

    // A hex run has to contain a digit, or it is not a hex run — it is English. `feedback` is seven
    // characters of a-f, and Bing's `b_algo_feedback` and `feedback-binded` were both being thrown
    // away as machine-generated. So are `decade`, `facade` and `deface`.
    var HEX_RUNS = /[0-9a-f]{6,}/gi;

    var VOWELS = /[aeiouy]/gi;
    var SEPARATED = /[-_.:]/;
    var SEPARATORS = /[-_.:]+/;

    function hasHexRun(name) {
        HEX_RUNS.lastIndex = 0;
        var match;
        while ((match = HEX_RUNS.exec(name)) !== null) if (/\d/.test(match[0])) return true;
        return false;
    }

    /// How often the case changes along a name, ignoring the first character.
    ///
    /// camelCase changes twice per word boundary and no more: `navBar`, `myButton` and `MuiButton`
    /// all score 2. A token somebody's build tool shook out of a hat changes wherever it likes —
    /// Bing's `SAKrtYCw` scores 3. It is a SIGNAL and not a verdict, because three is not far from
    /// two and a two-word camelCase name with an initialism in it could reach it honestly.
    function caseChurn(name) {
        var letters = name.replace(/[^A-Za-z]/g, '');
        var changes = 0;
        for (var i = 2; i < letters.length; i++) {
            var wasUpper = letters[i - 1] === letters[i - 1].toUpperCase();
            var isUpper = letters[i] === letters[i].toUpperCase();
            if (wasUpper !== isUpper) changes++;
        }
        return changes;
    }

    /// Three or more digits, broken into two or more runs.
    ///
    /// Both halves earn their place. THREE, not two, because people number things in pairs and
    /// `nav2col3` is a name somebody could have written. And two RUNS, not just "a digit somewhere
    /// other than the end", because `sha256` and `base64` are three digits in one run at the end,
    /// which is what a person writing a name does. Digits threaded THROUGH a token is what a hash
    /// looks like: Stack Overflow's `--stacks-s-tooltip-a63su8lv` hangs on exactly this.
    function scatteredDigits(name) {
        var runs = name.match(/\d+/g);
        if (!runs || runs.length < 2) return false;
        return runs.join('').length >= 3;
    }

    function upperRunInside(token) {
        // Two capitals in a row, after the first character and NOT running to the end. `gLFyf` has
        // one; `navBar` and `MuiButton` do not, and neither does an ALLCAPS name, which is a choice
        // a person makes. Nor does a trailing initialism — `iconAnswerAI` and `parseURL` end that
        // way on purpose, and counting them cost Stack Overflow's `iconAnswerAI` its identity.
        return /[a-z][A-Z]{2,}[a-z0-9]/.test(token);
    }

    /// The longest run of consonants, measured within each camelCase WORD rather than across the
    /// whole token.
    ///
    /// Across the whole token the measure counts letters that are nowhere near each other in the
    /// reading: `inTextBlock` scores five on `xtBl`, which spans two words and one capital, and
    /// GitHub's `Link--inTextBlock` was being thrown away for it. Split first, and the longest run
    /// in any real word is two or three.
    function longestConsonantRun(letters) {
        var words = letters.replace(/([a-z0-9])([A-Z])/g, '$1 $2').split(' ');
        var longest = 0;
        for (var w = 0; w < words.length; w++) {
            var current = 0;
            for (var i = 0; i < words[w].length; i++) {
                if (/[aeiouy]/i.test(words[w][i])) { current = 0; continue; }
                current++;
                if (current > longest) longest = current;
            }
        }
        return longest;
    }

    /// True when a name looks like something a machine produced rather than something a person
    /// chose.
    ///
    /// The known names, an all-digit name, and a long digit or hex run are decisive on their own.
    /// Everything else needs TWO independent signals before it counts, because one signal on its
    /// own has too many honest counter-examples: `search`, `nav2`, `b_results` and `sb_form_q` all
    /// trip at most one, and all four are names a person wrote.
    function looksGenerated(name) {
        if (!name) return false;
        for (var i = 0; i < KNOWN.length; i++) if (KNOWN[i].test(name)) return true;
        if (ALL_DIGITS.test(name)) return true;
        if (DIGIT_RUN.test(name)) return true;
        if (hasHexRun(name)) return true;

        // A separated name is examined SEGMENT BY SEGMENT rather than dismissed. The shape tests
        // below describe a random token, and a random token is very often welded onto the end of a
        // perfectly good name rather than standing alone — `--stacks-s-tooltip-a63su8lv` is one
        // readable name and one hash, and reading it as a whole finds neither.
        if (SEPARATED.test(name)) {
            var parts = name.split(SEPARATORS);
            for (var p = 0; p < parts.length; p++) {
                // Six, because a short segment carries too little to judge: half the abbreviations
                // in a stylesheet would trip the vowel test on four characters.
                //
                // And a digit or a capital somewhere in it, because a hash generated by any of
                // these tools is base36 or base62 and effectively never comes out as unbroken
                // lowercase letters — while a squashed phrase always does. `element.innerhtml`,
                // `mw-watchlink` and `js-tagname-postgresql` are all names somebody wrote, and all
                // three trip the shape tests on a segment when nothing stops them being asked.
                if (parts[p].length >= 6 && /[0-9A-Z]/.test(parts[p]) && randomShape(parts[p])) return true;
            }
            return false;
        }
        if (name.length < 4 || name.length > 12) return false;
        return randomShape(name);
    }

    /// Two independent signals that a token was shaken out of a hat rather than typed.
    function randomShape(token) {
        var letters = token.replace(/[^A-Za-z]/g, '');
        var vowels = (letters.match(VOWELS) || []).length;
        var signals = 0;

        // A digit in the middle of a word. A trailing one is how people number things — `nav2`,
        // `tab1`, `h3` — and is kept.
        if (/\d/.test(token.slice(0, -1))) signals++;
        if (upperRunInside(token)) signals++;
        // Short tokens only. A random token is short by construction; a LONG run of letters with
        // few vowels is a squashed phrase, and `3dprinting` is nine letters with two of them.
        if (letters.length > 0 && letters.length <= 8 && vowels / letters.length < 0.25) signals++;
        if (longestConsonantRun(letters) >= 4) signals++;
        if (scatteredDigits(token)) signals++;
        if (caseChurn(token) >= 3) signals++;

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
