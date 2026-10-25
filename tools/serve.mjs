#!/usr/bin/env node
// 演练用的本地静态服务（只发 zip / json）。
//
//   node tools/serve.mjs --dir sandbox/fake --port 8765
//
// 更新器走的是真实 HTTP 下载，所以演练也要真起一个服务 ——
// 不然验的就不是"下载 → 校验 → 解压"这条链了。

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const args = process.argv.slice(2);
const opt = (name, dflt) => {
  const i = args.indexOf(name);
  return i >= 0 && args[i + 1] ? args[i + 1] : dflt;
};

const DIR = opt('--dir', 'sandbox/fake');
const PORT = parseInt(opt('--port', '8765'), 10);

const TYPES = {
  '.zip': 'application/zip',
  '.json': 'application/json',
  '.txt': 'text/plain',
};

const server = http.createServer((req, res) => {
  const url = (req.url || '/').split('?')[0];
  const rel = decodeURIComponent(url.replace(/^\/+/, '')) || 'index.html';
  const file = path.join(path.resolve(DIR), rel);

  // 防目录穿越（演练服务也要守规矩）
  if (!file.startsWith(path.resolve(DIR))) {
    res.writeHead(403); res.end('forbidden'); return;
  }
  if (!fs.existsSync(file) || !fs.statSync(file).isFile()) {
    res.writeHead(404); res.end('not found: ' + rel); return;
  }

  const stat = fs.statSync(file);
  const type = TYPES[path.extname(file).toLowerCase()] || 'application/octet-stream';

  // 支持 Range（更新器的续传会用）
  const range = req.headers.range;
  if (range) {
    const m = /bytes=(\d*)-(\d*)/.exec(range);
    const start = m && m[1] ? parseInt(m[1], 10) : 0;
    const end = m && m[2] ? parseInt(m[2], 10) : stat.size - 1;
    res.writeHead(206, {
      'Content-Type': type,
      'Content-Length': end - start + 1,
      'Content-Range': `bytes ${start}-${end}/${stat.size}`,
      'Accept-Ranges': 'bytes',
    });
    fs.createReadStream(file, { start, end }).pipe(res);
    return;
  }

  res.writeHead(200, { 'Content-Type': type, 'Content-Length': stat.size, 'Accept-Ranges': 'bytes' });
  fs.createReadStream(file).pipe(res);
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`演练服务已启动：http://127.0.0.1:${PORT}/  （目录 ${path.resolve(DIR)}）`);
});
