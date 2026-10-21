import { defineConfig, devices } from '@playwright/test';
import { mkdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Browser smoke tests of the web client (Blazor WebAssembly), served by the API in demo mode.
//
// By default this starts the API itself (fresh databases in a temp folder, demo data, one known administrator) on a port of its own.
// To test an API that is already running, set KYUPLAN_UI_BASE_URL (and KYUPLAN_UI_PASSWORD for its administrator "admin"): nothing is started then.
// CI builds the API first and sets KYUPLAN_UI_NO_BUILD=1 so that the server starts from that build.

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..');

const externalUrl = process.env.KYUPLAN_UI_BASE_URL;
const port = Number(process.env.KYUPLAN_UI_PORT ?? 5199);
const baseURL = externalUrl ?? `http://127.0.0.1:${port}`;

const adminUser = process.env.KYUPLAN_UI_USER ?? 'admin';
const adminPassword = process.env.KYUPLAN_UI_PASSWORD ?? 'ui-smoke-test-password';

// One folder per port, so that two runs on one machine do not share databases.
const dataDir = path.join(tmpdir(), `kyuplan-ui-smoke-${port}`);
const artifacts = path.join(here, 'artifacts');

// Workers load this file again; only the process that starts the server prepares the (empty) data folder.
if (!externalUrl && process.env.TEST_WORKER_INDEX === undefined) {
    rmSync(dataDir, { recursive: true, force: true });
    mkdirSync(dataDir, { recursive: true });
}

const serverEnv: Record<string, string> = {
    ...Object.fromEntries(Object.entries(process.env).filter((e): e is [string, string] => e[1] !== undefined)),
    // Development, because only there does `dotnet run` serve the static files of the referenced web client project.
    ASPNETCORE_ENVIRONMENT: 'Development',
    Spond__UseDemoData: 'true',
    // On: the demo source fills members and exams into the database by the sync at start; without it the pages would be empty.
    Sync__Enabled: 'true',
    Database__ConnectionString: `Data Source=${path.join(dataDir, 'kyuplan.db')}`,
    Accounts__ConnectionString: `Data Source=${path.join(dataDir, 'accounts.db')}`,
    Accounts__InitialAdminUserName: adminUser,
    Accounts__InitialAdminPassword: adminPassword,
    // Every test signs in from 127.0.0.1: the lock-out after failed logins must not stop the run.
    Api__MaxLoginFailuresPerAddress: '0',
    // A file that does not exist: what a developer saved on the settings page must not change the run (like the xUnit hosts).
    Setup__SettingsFile: path.join(dataDir, 'no-settings', 'settings.json'),
    Logging__File__Path: path.join(dataDir, 'logs', 'kyuplan-.log'),
};

const runArgs = process.env.KYUPLAN_UI_NO_BUILD ? '--no-build' : '';

export default defineConfig({
    testDir: here,
    testMatch: '*.spec.ts',
    globalSetup: path.join(here, 'global-setup.ts'),
    outputDir: path.join(artifacts, 'test-results'),
    // The first start of the WebAssembly client downloads the framework; every test signs in again (the login lives in memory only).
    timeout: 90_000,
    expect: { timeout: 20_000 },
    fullyParallel: true,
    workers: process.env.CI ? 2 : undefined,
    retries: process.env.CI ? 1 : 0,
    forbidOnly: !!process.env.CI,
    reporter: [
        ['list'],
        ['html', { outputFolder: path.join(artifacts, 'report'), open: 'never' }],
    ],
    use: {
        baseURL,
        locale: 'en-GB', // the interface follows the browser language; the tests look for the English texts
        timezoneId: 'Europe/Berlin',
        trace: 'retain-on-failure',
        screenshot: 'only-on-failure', // the screenshots of the pages are taken by the tests themselves (artifacts/screenshots)
    },
    projects: [
        { name: 'desktop-light', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 }, colorScheme: 'light' } },
        { name: 'desktop-dark', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 }, colorScheme: 'dark' } },
        // A phone: only the pages tagged @mobile (the client has a layout of its own for it).
        { name: 'mobile', grep: /@mobile/, use: { ...devices['Pixel 7'], colorScheme: 'light' } },
    ],
    webServer: externalUrl
        ? undefined
        : {
              command: `dotnet run --project src/Kyuplan.Api -c Release --no-launch-profile ${runArgs} --urls ${baseURL}`,
              cwd: repoRoot,
              env: serverEnv,
              url: `${baseURL}/healthz`,
              timeout: 300_000,
              reuseExistingServer: false,
              stdout: 'pipe',
              stderr: 'pipe',
          },
});
