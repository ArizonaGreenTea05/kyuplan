// Lets the layout know which system the browser runs on, so that it offers the download of the app for it. Ids as in ClientPlatform (Contracts); "" = none (iPhones, unknown).
window.ppPlatform = {
    current: function () {
        var agent = navigator.userAgent || '';
        if (/android/i.test(agent)) return 'android';
        if (/iphone|ipad|ipod|mobile/i.test(agent)) return '';
        var platform = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || '';
        if (/win/i.test(platform) || /windows/i.test(agent)) return 'windows';
        if (/mac/i.test(platform)) return 'macos';
        if (/linux|x11/i.test(platform) || /linux|x11/i.test(agent)) return 'linux';
        return '';
    },
    // A phone-sized screen (the same breakpoint as the phone rules in app.css).
    isPhone: function () { return window.matchMedia('(max-width: 720px)').matches; }
};
