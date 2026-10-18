// Applies the saved theme before the first paint (no flash) and exposes a toggle and the choice of a skin for the layout.
// Two independent things: the colour scheme (light/dark; no stored value = follow the operating system, see prefers-color-scheme in
// app.css) and the skin (data-skin: dracula, catppuccin-latte, catppuccin-mocha, liquid-glass, or "pack"; none = the original look). A skin
// that exists in one colour scheme only (Dracula is dark) forces it while it is on; the user's own light/dark choice is kept for later.
// A theme pack (the API's /api/themes: the built-in ones such as the high contrast themes, and the ones administrators uploaded) is a
// set of colours: they are written as CSS variables into one <style> (data-skin="pack"), after being checked here again, since what
// reaches a stylesheet must be nothing but colours. A high contrast pack also sets data-contrast="high" (stronger borders, focus rings).
// The skin in effect is decided by the API (the user's own choice, else the instance's default); what is stored here is only a copy of
// it, so that the next visit paints in it before the page has asked.
(function () {
    var schemeKey = 'pp-theme';
    var skinKey = 'pp-skin';
    var packKey = 'pp-pack';
    var root = document.documentElement;
    var forced = { 'dracula': 'dark', 'catppuccin-mocha': 'dark', 'catppuccin-latte': 'light', 'liquid-glass': null };
    var forcedScheme = null;

    // The colours of a pack and the variables of app.css they set.
    var variables = {
        ground: '--ground', sheet: '--sheet', sheetAlt: '--sheet-2', sidebar: '--sidebar', text: '--text', muted: '--muted',
        rule: '--rule', ruleStrong: '--rule-strong', amber: '--amber', amberWash: '--amber-wash', red: '--red', redWash: '--red-wash'
    };
    var hex = /^#[0-9a-fA-F]{6}$/;

    function read(key) {
        try { return localStorage.getItem(key); } catch (e) { return null; }
    }

    function write(key, value) {
        try { localStorage.setItem(key, value); } catch (e) { /* private mode: keep for this session only */ }
    }

    function savedScheme() {
        var saved = read(schemeKey);
        return saved === 'light' || saved === 'dark' ? saved : null;
    }

    function isDark() {
        var explicit = root.dataset.theme;
        return explicit ? explicit === 'dark' : window.matchMedia('(prefers-color-scheme: dark)').matches;
    }

    // Black or white, whichever reads better on this colour (the text of something drawn in the accent colour).
    function onColor(color) {
        var channels = [1, 3, 5].map(function (i) {
            var c = parseInt(color.substr(i, 2), 16) / 255;
            return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
        });
        var luminance = 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
        return luminance > 0.179 ? '#000000' : '#ffffff';
    }

    // The pack as it may be used: every colour a #rrggbb, or nothing at all.
    function clean(scheme, highContrast, accent, colors) {
        if (!colors || (scheme !== 'light' && scheme !== 'dark')) return null;

        var safe = {};
        for (var key in variables) {
            if (!Object.prototype.hasOwnProperty.call(variables, key)) continue;
            if (typeof colors[key] !== 'string' || !hex.test(colors[key])) return null;
            safe[key] = colors[key];
        }
        return {
            scheme: scheme,
            highContrast: highContrast === true,
            accent: typeof accent === 'string' && hex.test(accent) ? accent : null,
            colors: safe
        };
    }

    function packStyle(pack) {
        var css = ':root[data-skin="pack"] {';
        for (var key in variables) {
            if (Object.prototype.hasOwnProperty.call(variables, key)) css += variables[key] + ':' + pack.colors[key] + ';';
        }
        // The accent of the pack is the pack's (a high contrast theme needs one that fits its surfaces); it wins over the inline one of the branding.
        if (pack.accent) css += '--accent:' + pack.accent + ' !important;--on-accent:' + onColor(pack.accent) + ';';
        return css + '}';
    }

    function setPackStyle(pack) {
        var style = document.getElementById('pp-pack');
        if (!pack) {
            if (style) style.remove();
            return;
        }
        if (!style) {
            style = document.createElement('style');
            style.id = 'pp-pack';
            document.head.appendChild(style);
        }
        style.textContent = packStyle(pack);
    }

    // id: the theme; for a theme pack also its scheme, whether it is high contrast, its accent colour (or null) and its colours.
    function setSkin(id, scheme, highContrast, accent, colors) {
        var pack = clean(scheme, highContrast, accent, colors);
        var known = Object.prototype.hasOwnProperty.call(forced, id);

        setPackStyle(pack);
        if (pack) {
            root.dataset.skin = 'pack';
            forcedScheme = pack.scheme;
        } else {
            if (known) root.dataset.skin = id; else delete root.dataset.skin;
            forcedScheme = known ? forced[id] : null;
        }
        if (pack && pack.highContrast) root.dataset.contrast = 'high'; else delete root.dataset.contrast;

        var applied = forcedScheme || savedScheme();
        if (applied) root.dataset.theme = applied; else delete root.dataset.theme;

        write(skinKey, pack ? id : known ? id : 'default');
        write(packKey, pack ? JSON.stringify(pack) : '');
    }

    var savedPack = null;
    try { savedPack = JSON.parse(read(packKey) || 'null'); } catch (e) { /* damaged copy: the layout paints the right one soon */ }
    if (savedPack) setSkin(read(skinKey), savedPack.scheme, savedPack.highContrast, savedPack.accent, savedPack.colors);
    else setSkin(read(skinKey));

    window.ppTheme = {
        // Light/dark; does nothing while the skin has only one.
        toggle: function () {
            if (forcedScheme) return;
            var next = isDark() ? 'light' : 'dark';
            root.dataset.theme = next;
            write(schemeKey, next);
        },
        setSkin: setSkin
    };
})();
