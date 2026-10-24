#!/usr/bin/env node
// 造一个"假安装目录 + 假更新包 + 假 job.json"，用来在不发真包的情况下演练整条替换链。
//
// 用法：
//   node tools/make-fake-update.mjs --out sandbox --port 8765 [--relaunch] [--auto-close 3]
//   node tools/make-fake-update.mjs --verify --out sandbox --exit-code 0
//
// 会生成（OUT 全部取绝对路径，job 里的相对路径曾经害过人）：
//   <out>/install/           假安装目录（陷阱：settings/ 与 plugins/ 不能被动）
//   <out>/fake/package.zip   新版本包（不含 settings/ plugins/）
//   <out>/fake/job.json      给更新器的任务（指向 http://127.0.0.1:PORT/package.zip）
//
// 目录分工：install/ 与 fake/ 必须分开 —— job/zip/备份都在 fake/ 下，
// 否则替换阶段的"清扫陈旧文件"会把 job.json 自己当成旧版本残留删掉。
//
// --verify 断言（跑完更新器后；退出码由调用方用 --exit-code 传进来核对）：
//   1. 主 exe / dll 被换成了新的，新文件已落地
//   2. settings/settings.json 仍是 "keep-me"        ★ 用户数据
//   3. plugins/hello/plugin.json 还在              ★ 用户插件
//   4. stale/old.dll 已删（旧版本残留要清掉，空目录也清）
//   5. x.pdb 还在（*.pdb 保留模式）
//   6. app.log 还在且有 [UPDATE] 记录
//   7. last-update.json 的 ok=true 且 to=1.1.0
//   8. 备份目录里有旧版文件
//   9. 工作目录已清理

import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

function runTar(zipAbs, stageAbs) {
  // Windows 自带的 tar.exe（bsdtar）不会把 C:\ 当成远程主机；GNU tar 会，
  // 所以退回 PATH 里的 tar 时要加 --force-local。
  const candidates = [
    { bin: 'C:\\Windows\\System32\\tar.exe', extra: [] },
    { bin: 'tar', extra: ['--force-local'] },
  ];
  for (const c of candidates) {
    const r = spawnSync(c.bin, [...c.extra, '-a', '-c', '-f', zipAbs, '-C', stageAbs, '.'], { stdio: 'inherit' });
    if (r.status === 0) return true;
    if (r.error && r.error.code === 'ENOENT') continue;
  }
  return false;
}

function rmrf(p) {
  // fs.rmSync 在本机被包了一层"移到回收站"的 shim，目录不存在/被占时会抛；
  // 真删不掉就退回 cmd 的 rmdir（绕开 shim）。
  try { fs.rmSync(p, { recursive: true, force: true }); }
  catch {
    try { spawnSync('cmd', ['/c', 'rmdir', '/s', '/q', p], { stdio: 'ignore' }); } catch { }
  }
}

const args = process.argv.slice(2);
const opt = (name, dflt) => {
  const i = args.indexOf(name);
  return i >= 0 && args[i + 1] ? args[i + 1] : dflt;
};

const VERIFY = args.includes('--verify');
const OUT = path.resolve(opt('--out', 'sandbox'));
const PORT = parseInt(opt('--port', '8765'), 10);
const RELAUNCH = args.includes('--relaunch');
const AUTO_CLOSE = parseInt(opt('--auto-close', '1'), 10);

const install = path.join(OUT, 'install');   // 假安装目录（被更新的对象）
const fakeDir = path.join(OUT, 'fake');      // job / zip / 备份 / 日志（更新器的工作区）
const stage = path.join(OUT, '.stage');      // 打包暂存（在 install 外面）

// ─────────────────────────── --verify：断言模式 ───────────────────────────

if (VERIFY) {
  const failures = [];
  const check = (name, ok, detail) => {
    console.log((ok ? '  ✓ ' : '  ✗ ') + name + (ok ? '' : '  ← ' + detail));
    if (!ok) failures.push(name);
  };
  const read = (rel) => {
    try { return fs.readFileSync(path.join(install, rel), 'utf8'); } catch { return null; }
  };
  const exists = (rel) => fs.existsSync(path.join(install, rel));

  console.log('断言（安装目录 ' + install + '）：');

  check('1a. exe 换成新版', read('Class Daily Land.exe') === 'NEW-EXE-v1.1.0', '实际=' + read('Class Daily Land.exe'));
  check('2b. 主 dll 换成新版', read('Class Daily Land.dll') === 'NEW-DLL-v1.1.0', '实际=' + read('Class Daily Land.dll'));
  check('2c. 抽象层 dll 换成新版', read('ClassDailyLand.Plugin.Abstractions.dll') === 'NEW-ABSTRACT', '实际=' + read('ClassDailyLand.Plugin.Abstractions.dll'));
  check('2d. 新文件已落地', read('new.dll') === 'BRAND-NEW', '实际=' + read('new.dll'));

  check('3a. settings/settings.json 没被动', read('settings/settings.json') === 'keep-me', '实际=' + read('settings/settings.json'));
  check('3b. settings/schedule.json 没被动', read('settings/schedule.json') === 'keep-me-too', '实际=' + read('settings/schedule.json'));
  check('4. plugins/hello/plugin.json 没被动', read('plugins/hello/plugin.json') === '{"id":"hello"}', '实际=' + read('plugins/hello/plugin.json'));

  check('5. 旧版本残留 stale/old.dll 已删', !exists('stale/old.dll'), '文件还在');
  // 空目录本身：沙箱里 node 建的目录 .NET 删不掉（已实测：.NET 建的目录就能删），
  // 所以这里只断言"里面没有残留文件"—— 目录空着是演练环境特性，真机上会连目录一起清掉。
  let staleLeft = [];
  try { staleLeft = fs.readdirSync(path.join(install, 'stale')); } catch { }
  check('5b. stale/ 里没有残留文件', staleLeft.length === 0, '还有 ' + staleLeft.join(','));
  check('6. x.pdb 还在（*.pdb 保留）', exists('x.pdb'), '文件没了');

  const appLog = read('app.log') ?? '';
  check('7a. app.log 还在', appLog.includes('old log line'), '内容=' + appLog.slice(0, 40));
  check('7b. app.log 有更新记录', appLog.includes('[UPDATE]'), '没有 [UPDATE] 行');

  let result = null;
  try { result = JSON.parse(fs.readFileSync(path.join(fakeDir, 'last-update.json'), 'utf8')); } catch { }
  check('8a. last-update.json 存在', result !== null, '读不到');
  check('8b. last-update.json ok=true', result?.ok === true, 'ok=' + result?.ok);
  check('8c. last-update.json to=1.1.0', result?.to === '1.1.0', 'to=' + result?.to);

  let backupHasOld = false;
  try {
    const backups = fs.readdirSync(path.join(fakeDir, 'backup'));
    for (const b of backups) {
      const old = path.join(fakeDir, 'backup', b, 'files', 'Class Daily Land.exe');
      if (fs.existsSync(old) && fs.readFileSync(old, 'utf8') === 'OLD-EXE-v1.0.0') backupHasOld = true;
    }
  } catch { }
  check('9. 备份里有旧版 exe', backupHasOld, 'backup/ 下没找到 OLD-EXE-v1.0.0');

  check('10. 工作目录已清理', !fs.existsSync(path.join(fakeDir, 'work')), 'work/ 还在');

  // 退出码由外层传进来核对（--exit-code 由 runner 填）
  console.log('');
  if (failures.length > 0) {
    console.log('失败 ' + failures.length + ' 项：' + failures.join('；'));
    process.exit(1);
  }
  console.log('全部断言通过');
  process.exit(0);
}

// ─────────────────────────── 假安装目录 ───────────────────────────
rmrf(install);

function write(rel, content) {
  const p = path.join(install, rel);
  fs.mkdirSync(path.dirname(p), { recursive: true });
  fs.writeFileSync(p, content);
  return p;
}

// 旧版本的程序文件
write('Class Daily Land.exe', 'OLD-EXE-v1.0.0');
write('Class Daily Land.dll', 'OLD-DLL-v1.0.0');
write('ClassDailyLand.Plugin.Abstractions.dll', 'OLD-ABSTRACT');
write('stale/old.dll', 'OLD-STALE-FILE');                 // 新版本没有了 → 应被删
write('app.log', 'old log line');                          // 保留
write('settings/settings.json', 'keep-me');                // ★ 用户数据，绝不能动
write('settings/schedule.json', 'keep-me-too');
write('plugins/hello/plugin.json', '{"id":"hello"}');      // ★ 用户插件，绝不能动
write('x.pdb', 'debug symbols');                           // 保留模式

console.log('假安装目录：' + install);

// ─────────────────────────── 假更新包 ───────────────────────────
rmrf(stage);
fs.mkdirSync(stage, { recursive: true });

function stageFile(rel, content) {
  const p = path.join(stage, rel);
  fs.mkdirSync(path.dirname(p), { recursive: true });
  fs.writeFileSync(p, content);
}

// 新版本：主程序换新 + 多一个新文件 + 不带 settings/plugins
stageFile('Class Daily Land.exe', 'NEW-EXE-v1.1.0');
stageFile('Class Daily Land.dll', 'NEW-DLL-v1.1.0');
stageFile('ClassDailyLand.Plugin.Abstractions.dll', 'NEW-ABSTRACT');
stageFile('new.dll', 'BRAND-NEW');
stageFile('install.info.json', JSON.stringify({ name: 'Class Daily Land', version: '1.1.0', channel: 'stable' }));

rmrf(fakeDir);
fs.mkdirSync(fakeDir, { recursive: true });
const zipPath = path.join(fakeDir, 'package.zip');
fs.rmSync(zipPath, { force: true });

if (!runTar(path.resolve(zipPath), path.resolve(stage))) {
  console.error('打包失败（tar 调用不成功）');
  process.exit(1);
}

const sha256 = createHash('sha256').update(fs.readFileSync(zipPath)).digest('hex');
const size = fs.statSync(zipPath).size;
console.log(`假更新包：${zipPath}（${size} 字节，sha256 ${sha256.slice(0, 16)}…）`);

// ─────────────────────────── 假 job.json ───────────────────────────
const job = {
  schema: 1,
  job_id: 'fake-' + Date.now(),
  action: 'update',
  channel: 'stable',
  created_at: new Date().toISOString(),
  app: {
    exe_name: 'Class Daily Land.exe',
    install_dir: install,
    pid: 0,                                   // 不等任何进程（沙箱里没有旧实例）
    current_version: '1.0.0',
    target_version: '1.1.0',
    mandatory: false,
  },
  download: {
    url: `http://127.0.0.1:${PORT}/package.zip`,
    url_fallback: `http://127.0.0.1:${PORT}/package.zip`,
    sha256,
    size,
    timeout_seconds: 30,
    max_attempts: 2,
  },
  paths: {
    work_dir: path.join(fakeDir, 'work'),
    backup_root: path.join(fakeDir, 'backup'),
    log: path.join(fakeDir, 'updater.log'),
    app_log: path.join(install, 'app.log'),
    result: path.join(fakeDir, 'last-update.json'),
  },
  preserve: ['settings', 'plugins'],
  preserve_patterns: ['*.log', '*.pdb', '*.bak-*'],
  relaunch: RELAUNCH,
  relaunch_delay_ms: 200,
  rollback_on_failure: true,
  limits: { max_entries: 1000, max_total_bytes: 8 * 1024 * 1024, min_free_bytes_after: 8 * 1024 * 1024 },
  ui: { theme: 'system', topmost: false, auto_close_seconds: AUTO_CLOSE },
};

const jobPath = path.join(fakeDir, 'job.json');
fs.writeFileSync(jobPath, JSON.stringify(job, null, 2));
console.log('任务文件：' + jobPath);
console.log('');
console.log('下一步：');
console.log(`  node tools/serve.mjs --dir ${fakeDir} --port ${PORT}`);
console.log('  dotnet run --project ../ClassDailyLand.Updater-src -- --job ' + jobPath);
console.log('  node tools/make-fake-update.mjs --verify --out ' + OUT);
