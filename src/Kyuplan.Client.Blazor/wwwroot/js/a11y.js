// Keyboard and screen-reader details that markup alone cannot give:
//  - ppA11y.focusMain: the skip link jumps to the page (<main id="main">).
//  - aria-current="page" on the navigation entry of the open page (Blazor's NavLink only sets a CSS class).
//  - Escape closes an open fly-out menu of the sidebar and returns to its entry; Enter/Space/ArrowDown on the entry opens it again and moves into it.
//  - Enter/Space operate the "dismiss" control of the error bar (an anchor with role="button").
window.ppA11y = {
    focusMain: function () {
        const main = document.getElementById('main');
        if (!main) return;
        main.focus({ preventScroll: false });
        main.scrollIntoView();
    },

    syncCurrent: function () {
        document.querySelectorAll('.sidebar a.nav-item').forEach(function (link) {
            if (link.classList.contains('active')) link.setAttribute('aria-current', 'page');
            else link.removeAttribute('aria-current');
        });
    },

    // The group (div.nav-group) that holds the element, if any.
    groupOf: function (element) {
        return element instanceof Element ? element.closest('.nav-group') : null;
    },

    onKeyDown: function (e) {
        if (e.key === 'Escape') {
            const group = ppA11y.groupOf(e.target);
            if (group && !group.hasAttribute('data-dismissed')) {
                group.setAttribute('data-dismissed', '');
                const entry = group.querySelector(':scope > button');
                if (entry) entry.focus();
                e.preventDefault();
            }
            return;
        }

        const target = e.target;
        if (!(target instanceof Element)) return;

        if (target.matches('#blazor-error-ui .dismiss') && (e.key === 'Enter' || e.key === ' ')) {
            e.preventDefault();
            target.click();
            return;
        }

        const group = ppA11y.groupOf(target);
        if (group && target.matches('.nav-group > button') && (e.key === 'ArrowDown' || e.key === 'ArrowRight' || e.key === 'Enter' || e.key === ' ')) {
            group.removeAttribute('data-dismissed');
            const first = group.querySelector('.flyout a, .flyout button');
            if (first) { first.focus(); e.preventDefault(); }
        }
    },

    // Opening again (click or pointer) clears the "closed with Escape" state; so does leaving the group.
    reopen: function (e) {
        const group = ppA11y.groupOf(e.target);
        if (group) group.removeAttribute('data-dismissed');
    },

    leave: function (e) {
        const group = ppA11y.groupOf(e.target);
        if (group && !group.contains(e.relatedTarget)) group.removeAttribute('data-dismissed');
    },

    start: function () {
        document.addEventListener('keydown', ppA11y.onKeyDown);
        document.addEventListener('click', ppA11y.reopen);
        document.addEventListener('mouseover', ppA11y.reopen);
        document.addEventListener('focusout', ppA11y.leave);
        ppA11y.syncCurrent();
        // The navigation is drawn by Blazor after this script ran, and the class changes with every navigation.
        new MutationObserver(ppA11y.syncCurrent).observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['class'] });
    }
};

ppA11y.start();
