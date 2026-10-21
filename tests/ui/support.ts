import { expect, type Page, type TestInfo } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

// Same defaults as playwright.config.ts (a spec must not import the config).
export const adminUser = process.env.KYUPLAN_UI_USER ?? 'admin';
const adminPassword = process.env.KYUPLAN_UI_PASSWORD ?? 'ui-smoke-test-password';

const screenshotRoot = path.join(import.meta.dirname, 'artifacts', 'screenshots');

/** What the page reported to the console while the test ran. */
export interface Diagnostics {
    pageErrors: string[];
    consoleErrors: string[];
}

export function watch(page: Page): Diagnostics {
    const found: Diagnostics = { pageErrors: [], consoleErrors: [] };
    page.on('pageerror', error => found.pageErrors.push(error.message));
    page.on('console', message => {
        if (message.type() === 'error') found.consoleErrors.push(`${message.text()} (${message.location().url})`);
    });
    return found;
}

/**
 * Opens the address and signs in on the login screen that stands in for the application until a login was accepted. The login lives in
 * memory only, so a direct visit of a page always starts here, and the page is shown at the same address afterwards.
 */
export async function openSignedIn(page: Page, address: string): Promise<void> {
    await page.goto(address);

    await page.locator('input[autocomplete="username"]').fill(adminUser);
    await page.locator('input[autocomplete="current-password"]').fill(adminPassword);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();

    await expect(page.locator('.app'), 'the application replaces the login screen').toBeVisible({ timeout: 60_000 });
}

/** Waits until the page has its content: the heading is there and nothing is loading any more. */
export async function waitUntilShown(page: Page): Promise<void> {
    await expect(page.locator('.content h1').first()).toBeVisible();
    await expect(page.locator('.content h1').first()).not.toBeEmpty();
    await expect(page.locator('.progress.on')).toHaveCount(0);
}

/** The colour scheme the page really shows (the background of the page), not just the one the browser was asked for. */
export async function shownScheme(page: Page): Promise<'light' | 'dark'> {
    const luminance = await page.evaluate(() => {
        for (const element of [document.querySelector('.main'), document.body, document.documentElement]) {
            if (!element) continue;
            const match = getComputedStyle(element).backgroundColor.match(/[\d.]+/g);
            if (!match || match.length < 3) continue;
            if (match.length > 3 && Number(match[3]) === 0) continue; // transparent: look at what is behind
            const [r, g, b] = match.slice(0, 3).map(Number);
            return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
        }
        return 1;
    });
    return luminance < 0.5 ? 'dark' : 'light';
}

export async function hasHorizontalScroll(page: Page): Promise<boolean> {
    return page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1);
}

/** The screenshots are the point of the run: files in artifacts/screenshots/<project>/ (CI uploads the folder) and attached to the report. */
export async function shoot(page: Page, testInfo: TestInfo, name: string): Promise<void> {
    const folder = path.join(screenshotRoot, testInfo.project.name);
    mkdirSync(folder, { recursive: true });
    const file = path.join(folder, `${name}.png`);
    await page.screenshot({ path: file, fullPage: true, animations: 'disabled' });
    await testInfo.attach(name, { path: file, contentType: 'image/png' });
}
