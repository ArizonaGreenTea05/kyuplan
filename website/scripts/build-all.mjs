// Builds the documentation: one Docusaurus site per audience into build/<audience> (the server serves them under /docs/<audience>/).
//   node scripts/build-all.mjs [audience ...]     all audiences when none is named
import { spawnSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { audiences } from './audiences.mjs';
import { prepare } from './prepare.mjs';

const site = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const wanted = process.argv.slice(2);
const names = wanted.length ? wanted : Object.keys(audiences);
for (const name of names) if (!audiences[name]) throw new Error(`Unknown audience "${name}"; known: ${Object.keys(audiences).join(', ')}`);

prepare();
rmSync(path.join(site, 'build'), { recursive: true, force: true });
mkdirSync(path.join(site, 'build'), { recursive: true });

for (const name of names) {
  console.log(`\n=== ${name} ===`);
  const result = spawnSync('npx', ['--no-install', 'docusaurus', 'build', '--out-dir', path.join('build', name)], {
    cwd: site,
    stdio: 'inherit',
    shell: true,
    env: { ...process.env, DOCS_AUDIENCE: name },
  });
  if (result.status !== 0) process.exit(result.status ?? 1);
}

// The archive of a release is a folder that any web server can serve at /docs/: the sites in it, and a page that leads into them.
writeFileSync(path.join(site, 'build', 'index.html'),
  '<!doctype html><meta charset="utf-8"><title>kyuplan documentation</title>' +
  names.map((n) => `<p><a href="${n}/">${audiences[n].title}</a></p>`).join(''));
