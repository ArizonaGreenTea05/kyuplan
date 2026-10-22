import { expect, test } from '@playwright/test';
import { adminUser, hasHorizontalScroll, openSignedIn, shoot, shownScheme, waitUntilShown, watch } from './support';

// Every page of the web client, opened as the administrator against the demo data. Each test checks that the page comes up at all (no
// unhandled error, no error bar of Blazor, not the "not allowed" text) and takes a screenshot of it in the project's colour scheme and size.
// This is not a visual regression test: nothing is compared. The screenshots are for people (CI uploads them) to look at.

interface Page {
    address: string;
    name: string;
    /** Also on the phone project. */
    mobile?: boolean;
    /** Rows the demo data must put on the page: a page that is up but empty (the sync did not run) fails. */
    rows?: string;
}

const pages: Page[] = [
    { address: '/', name: 'dashboard' },
    { address: '/dashboard', name: 'dashboard-route' },
    { address: '/statistics', name: 'statistics' },
    { address: '/calendar', name: 'calendar', mobile: true },
    { address: '/exams', name: 'exams', mobile: true },
    { address: '/members', name: 'members', mobile: true, rows: 'table.data tbody tr' },
    { address: '/belts', name: 'belts' },
    { address: '/prices', name: 'prices' },
    { address: '/tags', name: 'tags' },
    { address: '/manual-data', name: 'manual-data' },
    { address: '/users', name: 'users' },
    { address: '/roles', name: 'roles' },
    { address: '/settings', name: 'settings' },
    { address: '/branding', name: 'branding' },
    { address: '/calendar-feeds', name: 'calendar-feeds' },
    { address: '/backup', name: 'backup' },
    { address: '/sync-status', name: 'sync-status' },
    { address: '/account', name: 'account' },
    { address: '/account/preferences', name: 'preferences', mobile: true },
];

// KYUPLAN_UI_STRICT_CONSOLE=1: errors in the browser console fail the test too (they are always attached to it).
const strictConsole = !!process.env.KYUPLAN_UI_STRICT_CONSOLE;

for (const { address, name, mobile, rows } of pages) {
    test(`${name} page${mobile ? ' @mobile' : ''}`, async ({ page }, testInfo) => {
        const diagnostics = watch(page);

        await openSignedIn(page, address);
        await waitUntilShown(page);

        await expect(page.locator('#blazor-error-ui'), 'Blazor error bar').toBeHidden();
        await expect(page.locator('.chip.demo'), 'the API runs on demo data').toBeVisible();
        await expect(page.getByText(/not allowed/i), 'the administrator may use every page').toHaveCount(0);
        if (rows) await expect(page.locator(rows).first(), 'the demo data is on the page').toBeVisible();

        const expected = testInfo.project.use.colorScheme === 'dark' ? 'dark' : 'light';
        expect(await shownScheme(page), 'the colour scheme the page shows').toBe(expected);

        if (testInfo.project.name === 'mobile')
            expect.soft(await hasHorizontalScroll(page), 'the page scrolls sideways on a phone').toBe(false);

        await shoot(page, testInfo, name);

        if (diagnostics.consoleErrors.length > 0)
            await testInfo.attach('console-errors', { body: diagnostics.consoleErrors.join('\n'), contentType: 'text/plain' });

        expect(diagnostics.pageErrors, 'unhandled errors of the page').toEqual([]);
        if (strictConsole) expect(diagnostics.consoleErrors, 'errors in the browser console').toEqual([]);
    });
}

// Once is enough: every failed login counts towards the lock-out of the address (LoginThrottle), which an API that already runs keeps.
test('a wrong password is refused on the login screen', async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'desktop-light', 'one failed login per run');

    await page.goto('/');
    // The form can be rebuilt once the client has started and then loses what was typed: fill until the input survives (the button enables).
    await expect(async () => {
        await page.locator('input[autocomplete="username"]').fill(adminUser);
        await page.locator('input[autocomplete="current-password"]').fill('definitely-not-the-password');
        await expect(page.getByRole('button', { name: 'Sign in', exact: true })).toBeEnabled({ timeout: 1000 });
    }).toPass({ timeout: 60_000 });
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();

    await expect(page.getByText('The user name or the password is wrong.')).toBeVisible();
    await expect(page.locator('.app')).toHaveCount(0);

    await shoot(page, testInfo, 'login-rejected');
});

// The page of one event (the banner of an event in progress links to it): opened by clicking the first event of the month.
test('event detail page @mobile', async ({ page }, testInfo) => {
    const diagnostics = watch(page);

    await openSignedIn(page, '/calendar');
    await waitUntilShown(page);
    await page.getByRole('button', { name: 'Month', exact: true }).click();

    await page.locator('.cal-event').first().click();
    await expect(page.locator('.event-detail')).toBeVisible();
    await expect(page.locator('#blazor-error-ui'), 'Blazor error bar').toBeHidden();
    await expect(page).toHaveURL(/\/calendar/);
    await expect(page.locator('.progress.on')).toHaveCount(0);

    await shoot(page, testInfo, 'event-detail');
    expect(diagnostics.pageErrors, 'unhandled errors of the page').toEqual([]);
});
