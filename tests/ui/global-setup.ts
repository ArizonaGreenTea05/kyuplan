import { chromium, type FullConfig } from '@playwright/test';
import { openSignedIn } from './support';

// Signs in once before the tests: the first visit makes the server compile and the browser download the WebAssembly framework, which
// would otherwise eat into the time of whichever tests happen to run first.
export default async function globalSetup(config: FullConfig): Promise<void> {
    const baseURL = config.projects[0].use.baseURL;
    const browser = await chromium.launch();
    try {
        const page = await browser.newPage({ baseURL, locale: 'en-GB' });
        await openSignedIn(page, '/');
    } finally {
        await browser.close();
    }
}
