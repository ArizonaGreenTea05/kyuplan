// How this instance presents itself (club name, accent colour, logos, tab icon) comes from the API, GET api/branding: the page
// carries nothing of its own, so one build serves any club. The API shows it to everybody, no login needed.
// This runs before Blazor starts and dresses the splash screen while the app downloads; the layout later calls apply() again with
// what the shared view model loaded (the same answer), and Blazor's own components take over the rest.
(function () {
    var root = document.documentElement;

    function whenReady(action) {
        if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', action);
        else action();
    }

    function setIcon(url) {
        var link = document.querySelector('link[rel="icon"]');
        if (!url) {
            if (link) link.remove();
            return;
        }
        if (!link) {
            link = document.createElement('link');
            link.rel = 'icon';
            document.head.appendChild(link);
        }
        link.href = url;
    }

    // --accent is what app.css derives every shade of the accent from; it holds the stylesheet's own colour until this runs.
    // The title is not touched: once Blazor runs, every page sets its own (BrandedTitle).
    function apply(accent, iconUrl) {
        if (accent) root.style.setProperty('--accent', accent);
        setIcon(iconUrl);
    }

    function showSplashLogo(selector, url, alt) {
        var logo = document.querySelector(selector);
        if (!logo || !url) return;
        logo.alt = alt;
        logo.src = url;
        logo.hidden = false;
    }

    function dress(branding, base) {
        var absolute = function (url) { return url ? new URL(url, base).href : null; };
        var title = branding.organizationName ? branding.productName + ' · ' + branding.organizationName : branding.productName;
        var name = branding.organizationName || branding.productName;

        apply(branding.accentColor, absolute(branding.iconUrl));
        whenReady(function () {
            // The title of the tab while the app downloads; once a page has replaced the splash it is the page's to set.
            if (document.querySelector('.splash')) document.title = title;
            showSplashLogo('.splash .logo-light', absolute(branding.logoUrl), name);
            showSplashLogo('.splash .logo-dark', absolute(branding.logoOnDarkUrl), '');
        });
    }

    // The address of the API is the one Blazor will use: wwwroot/appsettings.json, PlannerApi:BaseAddress (empty: the page's own server).
    fetch('appsettings.json')
        .then(function (response) { return response.json(); })
        .then(function (settings) {
            // Empty = the server this page came from (the API serves the client).
            var address = (settings.PlannerApi && settings.PlannerApi.BaseAddress) || document.baseURI;

            var base = address.charAt(address.length - 1) === '/' ? address : address + '/';
            return fetch(base + 'api/branding')
                .then(function (response) { return response.json(); })
                .then(function (branding) { dress(branding, base); });
        })
        .catch(function () { /* cosmetic: the layout asks again once Blazor runs, and shows the failure if the API is down */ });

    window.ppBrand = { apply: apply };
})();
