// Lets the layout know which desktop system the browser runs on, so that it offers the download for it. Ids as in ClientPlatform (Contracts); "" = none (phones, unknown).
window.ppPlatform = {
    current: function () {
        var agent = navigator.userAgent || '';
        if (/android|iphone|ipad|ipod|mobile/i.test(agent)) return '';
        var platform = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || '';
        if (/win/i.test(platform) || /windows/i.test(agent)) return 'windows';
        if (/mac/i.test(platform)) return 'macos';
        if (/linux|x11/i.test(platform) || /linux|x11/i.test(agent)) return 'linux';
        return '';
    },
    // A phone-sized screen (the same breakpoint as the phone rules in app.css).
    isPhone: function () { return window.matchMedia('(max-width: 720px)').matches; }
};
