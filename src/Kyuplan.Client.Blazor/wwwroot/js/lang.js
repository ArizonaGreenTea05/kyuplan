// The language of the interface. Runs before Blazor starts, because the culture Blazor starts with decides which
// translations and which date/number data it downloads. No stored choice = follow the browser language.
// Keep the supported languages in step with LanguageService.Supported (Client.Shared).
(function () {
    var key = 'pp-lang';
    var root = document.documentElement;

    function read() {
        try { return localStorage.getItem(key); } catch (e) { return null; }
    }

    var saved = read();
    var wanted = saved || navigator.language || 'en';
    var code = wanted.toLowerCase().indexOf('de') === 0 ? 'de' : 'en';

    // Plain-HTML texts (splash, error bar) show the matching language through html[lang] in app.css.
    root.lang = code;

    window.ppLang = {
        code: code,
        culture: code === 'de' ? 'de-DE' : 'en-GB',
        // Reloading is how Blazor WebAssembly switches culture: translations are loaded once, at start-up.
        set: function (next) {
            try { localStorage.setItem(key, next); } catch (e) { /* private mode: lasts until the tab is closed */ }
            location.reload();
        }
    };
})();
