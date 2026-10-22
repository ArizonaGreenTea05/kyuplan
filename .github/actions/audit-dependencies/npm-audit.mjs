// npm audit with an accept-list: `node npm-audit.mjs <level> <ignore file>`, run in the folder of the package.json.
// npm audit itself cannot ignore an advisory. The ignore file holds one GHSA id per line (comments with #); an advisory that is not
// in it and has at least the given severity fails the check. Anything unreadable fails too, so a broken audit never looks like a pass.
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';

const [level = 'high', ignoreFile] = process.argv.slice(2);
const rank = { info: 0, low: 1, moderate: 2, high: 3, critical: 4 };
if (!(level in rank)) {
  console.error(`::error::Unknown severity "${level}".`);
  process.exit(1);
}

const accepted = new Set(
  ignoreFile && existsSync(ignoreFile)
    ? readFileSync(ignoreFile, 'utf8').split('\n').map((l) => l.replace(/#.*/, '').trim()).filter(Boolean)
    : []);

const run = spawnSync('npm', ['audit', '--json'], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
let report;
try {
  report = JSON.parse(run.stdout);
} catch {
  console.error(`::error::npm audit gave no readable report.\n${run.stdout}${run.stderr}`);
  process.exit(1);
}
if (report.error || !report.vulnerabilities) {
  console.error(`::error::npm audit failed: ${JSON.stringify(report.error ?? report)}`);
  process.exit(1);
}

const found = new Map(); // advisory id -> { package, severity, title }
for (const [name, vulnerability] of Object.entries(report.vulnerabilities)) {
  for (const via of vulnerability.via) {
    if (typeof via === 'string') continue; // a package that is only vulnerable through another one
    const id = via.url?.split('/').pop() ?? String(via.source);
    found.set(id, { package: via.name ?? name, severity: via.severity, title: via.title });
  }
}

const failing = [...found].filter(([id, a]) => rank[a.severity] >= rank[level] && !accepted.has(id));
for (const [id, a] of found) {
  if (accepted.has(id)) console.log(`accepted ${id} (${a.severity}) in ${a.package}: ${a.title}`);
}
for (const id of accepted) {
  if (!found.has(id)) console.log(`::warning::${id} is in ${ignoreFile} but no longer reported, remove it.`);
}
for (const [id, a] of failing) {
  console.error(`::error::${a.severity} ${id} in ${a.package}: ${a.title}`);
}
if (failing.length > 0) process.exit(1);
console.log(`npm audit: no advisory of severity ${level} or higher outside ${ignoreFile ?? 'the accept-list'}.`);
