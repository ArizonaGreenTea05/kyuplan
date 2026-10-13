// Copies the pages of every audience from ../docs into .generated/<audience>, with the front matter Docusaurus needs and the links made to fit:
//  - a link to another page of the same audience becomes a link between the generated pages,
//  - a link to a page of another audience becomes the address of that audience's site (/docs/<audience>/..., which the server only serves to those who may read it),
//  - every other link into the repository (source code, deploy files, ...) points at GitHub.
// Run by build-all.mjs; the sources stay the ones people read on GitHub.
import { existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { audiences } from './audiences.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '../..');
const repo = (process.env.DOCS_REPO_URL ?? 'https://github.com/ArizonaGreenTea05/PruefungsPlanner').replace(/\/$/, '');
const ref = process.env.DOCS_REPO_REF ?? 'main';

const pages = new Map(); // absolute source path -> { audience, page, start }
for (const [audience, { files }] of Object.entries(audiences)) {
  files.forEach((f, i) => pages.set(path.join(root, f.source), { audience, page: f.page, start: i === 0 }));
}

const hasScheme = (target) => /^[a-z][a-z0-9+.-]*:/i.test(target);

function rewrite(target, source, audience, image) {
  if (target.startsWith('#') || hasScheme(target) || target.startsWith('/')) return target;

  const [file, ...rest] = target.split('#');
  const anchor = rest.length ? '#' + rest.join('#') : '';
  const resolved = path.resolve(path.dirname(source), decodeURI(file));

  const page = pages.get(resolved);
  if (page) {
    if (page.audience === audience) return `./${page.page}.md${anchor}`;
    // The developer guide is not served by the application: from the other sites its pages are the ones on GitHub. (pathname:// keeps Docusaurus from adding its own base path.)
    if (page.audience !== 'developer' || audience === 'developer') return `pathname:///docs/${page.audience}/${page.start ? '' : page.page + '/'}${anchor}`;
  }

  const relative = path.relative(root, resolved).split(path.sep).join('/');
  if (relative.startsWith('..')) return target;
  if (image) return `${repo}/raw/${ref}/${relative}`;
  const kind = existsSync(resolved) && statSync(resolved).isDirectory() ? 'tree' : 'blob';
  return `${repo}/${kind}/${ref}/${relative}${anchor}`;
}

function convert(text, source, audience) {
  let inFence = false;
  return text
    .split('\n')
    .map((line) => {
      if (/^\s*(```|~~~)/.test(line)) inFence = !inFence;
      if (inFence) return line;
      return line.replace(/(!?)\[([^\]]*)\]\(([^)\s]+)((?:\s+"[^"]*")?)\)/g, (_, bang, label, target, title) =>
        `${bang}[${label}](${rewrite(target, source, audience, bang === '!')}${title})`);
    })
    .join('\n');
}

export function prepare() {
  const out = path.resolve(here, '../.generated');
  rmSync(out, { recursive: true, force: true });

  for (const [audience, { files }] of Object.entries(audiences)) {
    mkdirSync(path.join(out, audience), { recursive: true });
    files.forEach((f, i) => {
      const source = path.join(root, f.source);
      const body = convert(readFileSync(source, 'utf8').replace(/\r\n/g, '\n'), source, audience);
      const front = `---\nslug: ${i === 0 ? '/' : '/' + f.page}\nsidebar_position: ${i + 1}\n---\n\n`;
      writeFileSync(path.join(out, audience, `${f.page}.md`), front + body);
    });
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) prepare();
