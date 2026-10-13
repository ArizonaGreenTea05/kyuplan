// Applies the saved theme before the first paint (no flash) and exposes a toggle and the choice of a skin for the layout.
// Two independent things: the colour scheme (light/dark; no stored value = follow the operating system, see prefers-color-scheme in
// app.css) and the skin (data-skin: dracula, catppuccin-latte, catppuccin-mocha, liquid-glass; none = the original look). A skin
// that exists in one colour scheme only (Dracula is dark) forces it while it is on; the user's own light/dark choice is kept for later.
// The skin in effect is decided by the API (the user's own choice, else the instance's default); what is stored here is only a copy of
// it, so that the next visit paints in it before the page has asked.
(function () {
    var schemeKey = 'pp-theme';
    var skinKey = 'pp-skin';
    var root = document.documentElement;
    var forced = { 'dracula': 'dark', 'catppuccin-mocha': 'dark', 'catppuccin-latte': 'light', 'liquid-glass': null };

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

    function setSkin(id) {
        var known = Object.prototype.hasOwnProperty.call(forced, id);
        if (known) root.dataset.skin = id; else delete root.dataset.skin;

        var scheme = (known && forced[id]) || savedScheme();
        if (scheme) root.dataset.theme = scheme; else delete root.dataset.theme;
        write(skinKey, known ? id : 'default');
    }

    setSkin(read(skinKey));

    window.ppTheme = {
        // Light/dark; does nothing while the skin has only one.
        toggle: function () {
            if (root.dataset.skin && forced[root.dataset.skin]) return;
            var next = isDark() ? 'light' : 'dark';
            root.dataset.theme = next;
            write(schemeKey, next);
        },
        setSkin: setSkin
    };
})();
