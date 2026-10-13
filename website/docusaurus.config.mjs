// One site per audience (see scripts/audiences.mjs): DOCS_AUDIENCE picks the pages (.generated/<audience>, made by scripts/prepare.mjs) and
// the address (/docs/<audience>/), which is where the API serves it, to the users who may read it.
import { audiences } from './scripts/audiences.mjs';

const audience = process.env.DOCS_AUDIENCE ?? 'user';
if (!audiences[audience]) throw new Error(`Unknown DOCS_AUDIENCE "${audience}"`);

/** @type {import('@docusaurus/types').Config} */
export default {
  title: `kyuplan: ${audiences[audience].title}`,
  tagline: 'Exam planner for karate clubs',
  url: process.env.DOCS_URL ?? 'http://localhost',
  baseUrl: `/docs/${audience}/`,
  trailingSlash: true, // every page is a folder with an index.html, which is what the API serves
  onBrokenLinks: 'warn',
  future: { v4: true },

  // The pages are written for GitHub, where braces and angle brackets are text: plain Markdown, not MDX.
  markdown: { format: 'detect' },

  i18n: { defaultLocale: 'en', locales: ['en'] },

  presets: [
    [
      'classic',
      {
        docs: { path: `.generated/${audience}`, routeBasePath: '/' },
        blog: false,
        pages: false,
        theme: {},
      },
    ],
  ],

  themeConfig: {
    colorMode: { respectPrefersColorScheme: true },
    navbar: { title: `kyuplan: ${audiences[audience].title}` },
    docs: { sidebar: { hideable: false } },
  },
};
