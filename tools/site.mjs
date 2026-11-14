import { readFile, appendFile, mkdir, stat, cp } from 'node:fs/promises';
import { createServer } from 'node:http';
import { resolve, dirname, join, extname, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expectedDownloads, releaseIsReady } from './release-readiness.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'website');
const output = join(root, 'dist');
const allowed = ['index.html', '404.html', 'styles.css', 'app.js', '.nojekyll', 'assets'];
async function check() {
  const html = await readFile(join(root, 'index.html'), 'utf8');
  expectedDownloads(html);
  const ids = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map(match => match[1]));
  for (const [, target] of html.matchAll(/(?:href|src)="([^"]+)"/g)) {
    if (/^https:\/\//.test(target)) continue;
    if (target.startsWith('#')) {
      if (!ids.has(target.slice(1))) throw new Error(`Missing anchor: ${target}`);
    } else {
      const path = resolve(root, target);
      if (path !== root && !path.startsWith(root + sep)) throw new Error(`Escaped local reference: ${target}`);
      await stat(path);
    }
  }
  for (const required of ['lang="zh-CN"', 'name="viewport"', '未签名', 'DPAPI', 'GitHub', '预览版']) {
    if (!html.includes(required)) throw new Error(`Missing release/accessibility information: ${required}`);
  }
  if (/<(?:script|iframe)[^>]+src="https?:/i.test(html)) throw new Error('Unexpected third-party script or embed.');
  console.log('Website references, anchors and release information: PASS');
}
const command = process.argv[2] || 'check';
if (command === 'check') await check();
else if (command === 'release-ready') {
  const expected = expectedDownloads(await readFile(join(root, 'index.html'), 'utf8'));
  const headers = { Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28' };
  if (process.env.GH_TOKEN) headers.Authorization = `Bearer ${process.env.GH_TOKEN}`;
  const response = await fetch(`https://api.github.com/repos/canonliuliang/StealthWorkspace/releases/tags/${encodeURIComponent(expected[0].tag)}`, {
    headers, signal: AbortSignal.timeout(20000)
  });
  if (!response.ok && response.status !== 404) throw new Error(`Release verification returned HTTP ${response.status}`);
  const ready = releaseIsReady(response.ok ? await response.json() : null, expected);
  if (process.env.GITHUB_OUTPUT) await appendFile(process.env.GITHUB_OUTPUT, `ready=${ready}\n`);
  console.log(ready ? 'Release assets ready: both architectures and checksums.' : 'Deployment deferred: release assets are not all published yet.');
}
else if (command === 'build') {
  await check();
  await mkdir(output, { recursive: true });
  for (const name of allowed) await cp(join(root, name), join(output, name), { recursive: true });
  console.log(`Static website built: ${output}`);
} else if (command === 'serve') {
  await check();
  const mime = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.png': 'image/png', '.ico': 'image/x-icon' };
  const server = createServer(async (req, res) => {
    try {
      const pathname = decodeURIComponent(new URL(req.url, 'http://localhost').pathname);
      const file = resolve(root, '.' + (pathname === '/' ? '/index.html' : pathname));
      const rel = relative(root, file);
      if (!file.startsWith(root + sep) || !allowed.includes(rel.split(sep)[0])) { res.writeHead(404).end('Not found'); return; }
      const data = await readFile(file);
      res.writeHead(200, { 'Content-Type': mime[extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
      res.end(data);
    } catch { res.writeHead(404).end('Not found'); }
  });
  server.listen(4173, '127.0.0.1', () => console.log('Preview: http://127.0.0.1:4173'));
} else throw new Error(`Unknown command: ${command}`);
