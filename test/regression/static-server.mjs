// Issue #565 — zero-dependency static file server rooted at the repo root.
// Playwright is blocked from navigating file:// URLs (and some of the
// harnesses' relative-path <script src> loads hit browser same-origin
// restrictions under file://), so the harnesses must be served over HTTP —
// same requirement documented in test/manual/561-formtaghelper-island.html
// and test/manual/470b-laydate-island.html ("python3 -m http.server").
// This is that same idea, but as a plain `node http.createServer` so the
// only new dependency this suite adds is @playwright/test itself.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
// test/regression/ -> repo root is two levels up.
const ROOT = path.resolve(__dirname, '..', '..');
const PORT = 4565;

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.svg': 'image/svg+xml',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
  '.ttf': 'font/ttf',
  '.ico': 'image/x-icon',
};

const server = http.createServer((req, res) => {
  try {
    const requestUrl = new URL(req.url, `http://${req.headers.host}`);
    // Path-traversal guard: resolve then verify the result is still under ROOT.
    const decoded = decodeURIComponent(requestUrl.pathname);
    const resolved = path.resolve(ROOT, '.' + decoded);
    if (!resolved.startsWith(ROOT)) {
      res.writeHead(403);
      res.end('Forbidden');
      return;
    }
    fs.stat(resolved, (err, stat) => {
      if (err || !stat.isFile()) {
        res.writeHead(404);
        res.end('Not found: ' + decoded);
        return;
      }
      const ext = path.extname(resolved).toLowerCase();
      res.writeHead(200, { 'Content-Type': MIME[ext] || 'application/octet-stream' });
      fs.createReadStream(resolved).pipe(res);
    });
  } catch (e) {
    res.writeHead(500);
    res.end('Internal error: ' + e);
  }
});

server.listen(PORT, '127.0.0.1', () => {
  // eslint-disable-next-line no-console
  console.log(`[wtm-regression] static server serving ${ROOT} on http://127.0.0.1:${PORT}`);
});
