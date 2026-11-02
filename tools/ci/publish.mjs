#!/usr/bin/env node
// ============================================================================
//  publish.mjs — 插件市场收件箱处理器（CI 用，也可本地手跑）
//  ---------------------------------------------------------------------------
//  输入：inbox/*.zip
//  输出：packages/<id>/<id>-<ver>.cblplugin + entries/<id>.json + index.json
//
//  三种 zip 形态都收（这是"作者不用改交付方式"的关键）：
//    · 源码型   —— 与 plugin.json 同目录有 .csproj，先 dotnet build 再打包
//    · 预编译   —— 目录里直接是 plugin.json + dll
//    · 成品包   —— zip 里就是一个 .cblplugin，直接登记
//
//  纪律（缺一条就会把坏数据推进市场）：
//    1. zip 解压前过了 zip-slip / 条目数 / 总量三道闸；
//    2. 同 id 同版本**不许改内容**，同 id 不许退版本；
//    3. 先 verify → build → 再 verify，三步全过才算成功（索引永不半成品）；
//    4. 单个包失败只影响它自己，其余照常发布，失败证据落 failed/<时间戳>/。
//
//  用法：
//    node tools/ci/publish.mjs --inbox inbox --root . --upstream upstream \
//      --work "$RUNNER_TEMP/mb" --result "$RUNNER_TEMP/mb/result.json" \
//      [--rebuild] [--dry-run] [--actor <github.actor>] [--official-actors a,b]
// ============================================================================

import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { listZip, readZipEntry } from '../zip.mjs';

// ————————————————————— 参数 —————————————————————

const argv = process.argv.slice(2);
const opt = (name, dflt) => {
  const i = argv.indexOf(name);
  return i >= 0 && argv[i + 1] !== undefined ? argv[i + 1] : dflt;
};
const has = (name) => argv.includes(name);

const ROOT = path.resolve(opt('--root', '.'));
const PKG_DIR = '已完成插件';   // 编译产物落地目录（客户端 index 里的 path 前缀）
const INBOX_DIR = '上传的zip';  // 收件箱：往里丢 zip 就触发
const INBOX = path.resolve(opt('--inbox', path.join(ROOT, INBOX_DIR)));
const UPSTREAM = path.resolve(opt('--upstream', path.join(ROOT, 'upstream')));
const WORK = path.resolve(opt('--work', path.join(os.tmpdir(), 'market-build')));
const RESULT = path.resolve(opt('--result', path.join(WORK, 'result.json')));
const REBUILD = has('--rebuild');
const DRY = has('--dry-run');
const ACTOR = (opt('--actor', '') || '').trim();
const OFFICIAL_ACTORS = (opt('--official-actors', '') || '')
  .split(',').map((s) => s.trim()).filter(Boolean);

const LIMITS = {
  pkg: 20 * 1024 * 1024,        // 单包上限（客户端硬上限 64MB，这里留余量）
  unzipped: 128 * 1024 * 1024,  // 解压总量（防 zip 炸弹）
  entries: 2000,                // 单 zip 条目数
  keepVersions: 3,              // 每个插件保留最近 3 个版本
  totalWarn: 300 * 1024 * 1024,
  totalHard: 500 * 1024 * 1024,
  sourcesMax: 2 * 1024 * 1024,  // 源码快照上限
};

const results = { published: [], skipped: [], failed: [] };

/**
 * 已处理文件的指纹（zip 的 sha256 → 发布结果）。
 *
 * 为什么需要它：CI 处理完会把 zip 从 inbox 删掉，但**作者本地那份还在**，
 * 下次 `git add inbox/` 又会把它推上来。只看「id@版本 是否已存在」只能避免重复发布，
 * 却仍要白编译一遍（源码型 1-2 分钟）。用 zip 自身的哈希做幂等键，
 * 同一份文件第二次到达时连解压都不做，直接消费掉。
 */
const PROCESSED_FILE = path.join(INBOX, '.processed.json');
const PROCESSED_KEEP = 500;

function loadProcessed() {
  try {
    return readJson(PROCESSED_FILE).items ?? {};
  } catch {
    return {};
  }
}

function saveProcessed(items) {
  const keys = Object.keys(items).slice(-PROCESSED_KEEP);
  const trimmed = {};
  for (const k of keys) trimmed[k] = items[k];
  fs.mkdirSync(path.dirname(PROCESSED_FILE), { recursive: true });
  fs.writeFileSync(
    PROCESSED_FILE,
    JSON.stringify({ version: 1, note: '已处理文件指纹，删掉某条即可强制重发', items: trimmed }, null, 2) + '\n',
    'utf8');
}

// ————————————————————— 小工具 —————————————————————

class UserError extends Error {}

const log = (msg) => console.log(msg);
const mb = (n) => (n / 1048576).toFixed(2) + ' MB';

function run(cmd, args, options = {}) {
  const r = spawnSync(cmd, args, { encoding: 'utf8', ...options });
  const stdout = r.stdout ?? '';
  const stderr = r.stderr ?? '';
  if (r.status !== 0) {
    const e = new Error(`${path.basename(cmd)} 退出码 ${r.status}\n${stderr || stdout}`);
    e.stdout = stdout;
    e.stderr = stderr;
    throw e;
  }
  return { stdout, stderr };
}

/** 调仓库里的 market.mjs（pack/add/build/verify）。 */
function market(...args) {
  return run(process.execPath, [path.join(ROOT, 'tools', 'market.mjs'), ...args], { cwd: ROOT });
}

const readJson = (file) => JSON.parse(fs.readFileSync(file, 'utf8'));
const sha256 = (file) =>
  crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex').toUpperCase();
const today = () => new Date().toISOString().slice(0, 10);

/** 语义化版本比较，够用即可（不处理预发布段的完整规则）。 */
function compareVersion(a, b) {
  const parse = (s) => String(s ?? '').replace(/^v/i, '').split(/[-+]/)[0]
    .split('.').map((n) => parseInt(n, 10) || 0);
  const x = parse(a);
  const y = parse(b);
  for (let i = 0; i < 4; i++) {
    const l = x[i] ?? 0;
    const r = y[i] ?? 0;
    if (l !== r) return l > r ? 1 : -1;
  }
  return 0;
}

const slugify = (s) => (s || 'pkg').replace(/[^\w.-]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 48) || 'pkg';

function stamp() {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}`;
}

// ————————————————————— zip 解压（三闸） —————————————————————

function unzipTo(zipFile, destDir) {
  const entries = listZip(zipFile);
  if (entries.length > LIMITS.entries)
    throw new UserError(`zip 条目过多（${entries.length} > ${LIMITS.entries}）`);

  let total = 0;
  for (const e of entries) {
    const name = e.name.replace(/\\/g, '/');
    if (name.includes('..') || name.startsWith('/') || /^[a-zA-Z]:/.test(name))
      throw new UserError(`zip 内含非法路径「${e.name}」（疑似 zip-slip），拒绝解压`);
    total += e.size;
  }
  if (total > LIMITS.unzipped)
    throw new UserError(`解压后约 ${mb(total)}，超过 ${mb(LIMITS.unzipped)} 上限（疑似 zip 炸弹）`);

  for (const e of entries) {
    const name = e.name.replace(/\\/g, '/');
    if (name.endsWith('/')) {
      fs.mkdirSync(path.join(destDir, name), { recursive: true });
      continue;
    }
    const target = path.join(destDir, name);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, readZipEntry(zipFile, e.name));
  }
}

/** 只有一层目录时下钻（应对"右键压缩整个文件夹"的常见产物）。 */
function normalizeDir(dir) {
  for (let i = 0; i < 3; i++) {
    const items = fs.readdirSync(dir, { withFileTypes: true })
      .filter((d) => d.name !== '__MACOSX' && !d.name.startsWith('.'));
    if (items.length === 1 && items[0].isDirectory()) {
      dir = path.join(dir, items[0].name);
      continue;
    }
    break;
  }
  return dir;
}

function walk(dir, pred, skip = new Set(['obj', 'bin', '.git', 'node_modules']), out = []) {
  for (const item of fs.readdirSync(dir, { withFileTypes: true })) {
    if (item.isDirectory()) {
      if (skip.has(item.name.toLowerCase())) continue;
      walk(path.join(dir, item.name), pred, skip, out);
    } else if (pred(item.name)) out.push(path.join(dir, item.name));
  }
  return out;
}

// ————————————————————— 形态识别 —————————————————————

function classify(dir) {
  const projects = walk(dir, (n) => n.toLowerCase().endsWith('.csproj'));
  const manifests = walk(dir, (n) => n.toLowerCase() === 'plugin.json');
  const packed = walk(dir, (n) => n.toLowerCase().endsWith('.cblplugin'));

  if (projects.length) {
    const project = projects.find((p) =>
      manifests.some((m) => path.dirname(m) === path.dirname(p)));
    if (!project)
      throw new UserError('找到 .csproj，但同目录下没有 plugin.json —— 无法确定插件根目录');
    if (projects.length > 1)
      log(`   ⚠ zip 里有 ${projects.length} 个 csproj，使用与 plugin.json 同级的那一个`);
    return { kind: 'source', project, sourceDir: path.dirname(project) };
  }
  if (packed.length) {
    if (packed.length > 1) throw new UserError('zip 里有多于一个 .cblplugin，一次只能发一个插件');
    return { kind: 'packed', file: packed[0] };
  }
  if (manifests.length) return { kind: 'prebuilt', dir: path.dirname(manifests[0]) };

  throw new UserError('zip 里既没有 .csproj，也没有 plugin.json，也不是 .cblplugin —— 不知道要发什么');
}

// ————————————————————— 契约工程 & 编译 —————————————————————

/**
 * 插件 csproj 里写的是 `..\..\ClassDailyLand2.Plugin.Abstractions\`，这是相对路径，
 * 无法从外部覆盖 —— 只能把契约工程**物理放到 csproj 的"上两级目录"**。
 *
 * ★ 注意方向：`..\..` 是从 **csproj 所在目录**出发上跳两次，
 *   等价于 resolve(projectDir, '..', '..')。写成 dirname(dirname(csproj)) 会少跳一级，
 *   结果是 dotnet 报 MSB9008「引用的项目 … 不存在」。
 */
/**
 * 契约工程的相对路径（`..\..\ClassDailyLand2.Plugin.Abstractions\`）只在仓库布局里成立。
 * CI 里插件是解压到临时目录的，套了几层目录不可控 —— 与其猜，不如把 csproj 里的
 * ProjectReference 直接改指上游的绝对路径。改的是 workDir 里的一次性副本，
 * `sources/` 快照仍然是作者原样提交的内容。
 */
function pointContractAtUpstream(projectFile) {
  const upstream = path.join(UPSTREAM, 'ClassDailyLand2.Plugin.Abstractions', 'ClassDailyLand2.Plugin.Abstractions.csproj');
  if (!fs.existsSync(upstream))
    throw new UserError(`找不到契约工程：${upstream}（workflow 里的 sparse checkout 没跑？）`);

  const text = fs.readFileSync(projectFile, 'utf8');
  const re = /(<ProjectReference\s+Include=")([^"]*ClassDailyLand2\.Plugin\.Abstractions\.csproj)(")/i;
  if (!re.test(text)) {
    log('   ⚠ csproj 里没有对契约工程的 ProjectReference，保持原样');
    return false;
  }

  const abs = upstream.replace(/\\/g, '/');
  fs.writeFileSync(projectFile, text.replace(re, (_m, a, _old, c) => a + abs + c), 'utf8');
  log(`   契约引用已改指上游：${abs}`);
  return true;
}

function ensureAbstractions(projectFile) {
  const projectDir = path.dirname(projectFile);
  const anchor = path.resolve(projectDir, '..', '..');
  const target = path.join(anchor, 'ClassDailyLand2.Plugin.Abstractions');
  if (fs.existsSync(target)) return target;

  const source = path.join(UPSTREAM, 'ClassDailyLand2.Plugin.Abstractions');
  if (!fs.existsSync(source))
    throw new UserError(`找不到契约工程：${source}（workflow 里的 sparse checkout 没跑？）`);

  fs.cpSync(source, target, { recursive: true });
  log(`   契约工程已放到 ${path.relative(projectDir, target)}（csproj 的 ..\\..\\）`);
  return target;
}

/**
 * 清理编译输出里不该进包的东西。
 *
 * 契约 dll 是最常见的意外：csproj 里明明写了 Private=false，`dotnet build -o <dir>`
 * 仍可能把它复制过去（不同 SDK/参数组合行为不一致）。包里有它就等于把宿主契约
 * 又分发了一份，`market.mjs pack` 会直接拒绝 —— 与其让作者去改工程，不如这里剔掉。
 */
function sanitizeOutput(dir) {
  const unwanted = [
    /^ClassDailyLand2\.Plugin\.Abstractions\.(dll|pdb|xml)$/i,
    /\.pdb$/i,
    /^market\.json$/i,
  ];
  for (const name of fs.readdirSync(dir)) {
    if (!unwanted.some((re) => re.test(name))) continue;
    const full = path.join(dir, name);
    if (!fs.statSync(full).isFile()) continue;
    fs.rmSync(full, { force: true });
    log(`   − 从输出目录剔除 ${name}`);
  }
}

function buildProject(projectFile, outDir, workDir) {
  const build = () => run('dotnet', [
    'build', projectFile, '-c', 'Release', '-o', outDir, '--nologo', '-v', 'minimal',
  ], { cwd: workDir });

  try {
    build();
  } catch (e) {
    // 兜底：错误里若点名缺失的 Abstractions.csproj，按它要的绝对路径再放一次。
    // ★ 目标目录必须不存在才放：往已存在的工程目录里再复制一份同名子目录，
    //   项目的默认递归 glob 会把两份 .cs 都编进去 → CS0101 重复定义，
    //   把真正的错误盖掉（CI 上真实发生过）。
    const text = `${e.stdout || ''}\n${e.stderr || ''}`;
    const missing = text.match(/([A-Za-z]:[\\/][^\r\n]*?ClassDailyLand2\.Plugin\.Abstractions\.csproj)/);
    if (!missing) throw e;
    const wantedDir = path.dirname(missing[1]);
    const source = path.join(UPSTREAM, 'ClassDailyLand2.Plugin.Abstractions');
    if (!fs.existsSync(source) || fs.existsSync(wantedDir)) throw e;
    log(`   契约工程路径不符，按报错要求放到 ${wantedDir}`);
    fs.cpSync(source, wantedDir, { recursive: true });
    build();
  }
}

// ————————————————————— 元数据 —————————————————————

function readMeta(sourceDir) {
  const file = path.join(sourceDir, 'market.json');
  if (!fs.existsSync(file)) return {};
  try {
    return readJson(file);
  } catch (e) {
    throw new UserError(`market.json 不是合法 JSON：${e.message}`);
  }
}

function removeMeta(dir) {
  const file = path.join(dir, 'market.json');
  if (fs.existsSync(file)) fs.rmSync(file, { force: true });
}

function resolveTrust(meta) {
  const declared = (meta.trust || 'community').toLowerCase();
  if (declared !== 'official') return declared === 'verified' ? 'verified' : 'community';
  if (OFFICIAL_ACTORS.length === 0) {
    log('   ⚠ 未配置 OFFICIAL_ACTORS，official 自动降级为 community');
    return 'community';
  }
  if (!OFFICIAL_ACTORS.some((a) => a.toLowerCase() === ACTOR.toLowerCase())) {
    log(`   ⚠ ${ACTOR || '匿名提交者'} 不在 OFFICIAL_ACTORS，official 自动降级为 community`);
    return 'community';
  }
  return 'official';
}

/** 客户端会静默丢条的那些约束，必须在提交前拦住。 */
function gate(manifest, meta, pkgSize) {
  const api = Number(manifest.apiVersion ?? 1);
  if (!Number.isFinite(api) || api <= 0) throw new UserError('plugin.json 的 apiVersion 不合法');
  if (api > 2) throw new UserError(`插件声明的契约 v${api} 比宿主支持的 v2 新，装上也加载不了`);

  const sdkNeed = meta.target_sdk_version || manifest.target_sdk_version;
  if (sdkNeed) {
    const csproj = path.join(UPSTREAM, 'ClassDailyLand2.Plugin.Abstractions', 'ClassDailyLand2.Plugin.Abstractions.csproj');
    if (fs.existsSync(csproj)) {
      const text = fs.readFileSync(csproj, 'utf8');
      const current = (text.match(/<Version>([^<]+)<\/Version>/) || [])[1];
      if (current && compareVersion(sdkNeed, current) > 0)
        throw new UserError(`要求 SDK ${sdkNeed}，但 master 的契约工程还是 ${current} —— 请先升契约版本再发插件`);
    }
  }

  const minApp = meta.min_app_version || manifest.min_app_version;
  if (minApp && !/^\d+(\.\d+){0,3}([-+][0-9A-Za-z.-]+)?$/.test(String(minApp)))
    throw new UserError(`min_app_version「${minApp}」不是合法版本号`);

  if (pkgSize > LIMITS.pkg)
    throw new UserError(`包体积 ${mb(pkgSize)} 超过 ${mb(LIMITS.pkg)} 上限`);
}

// ————————————————————— 退版 / 重复守卫 —————————————————————

function checkVersion(manifest, pkgPath) {
  const entryFile = path.join(ROOT, 'entries', `${manifest.id}.json`);
  if (!fs.existsSync(entryFile)) return {};

  const entry = readJson(entryFile);
  const cmp = compareVersion(manifest.version, entry.version);

  if (cmp < 0)
    throw new UserError(`版本倒退：已发布 ${entry.version}，本次 ${manifest.version}。客户端本来就拦降级，发了等于白推`);

  if (cmp === 0) {
    const existing = path.join(ROOT, PKG_DIR, manifest.id, entry.package || '');
    if (!fs.existsSync(existing))
      throw new UserError(`entries 里记着 ${entry.version}，但包不在 ${PKG_DIR}/${manifest.id}/ 下 —— 分支状态不一致，请先修好`);

    const same = sha256(existing) === sha256(pkgPath);
    if (same) return { skipped: true, version: manifest.version };
    throw new UserError(`同版本内容不同：已发布 ${entry.version} 的 sha256 与本次不一致。改内容请升版本号`);
  }

  return {};
}

// ————————————————————— 单个包的处理 —————————————————————

function publishOne(zipFile, workDir) {
  log(`\n▶ ${path.basename(zipFile)}`);

  const extractDir = path.join(workDir, 'extract');
  fs.mkdirSync(extractDir, { recursive: true });
  unzipTo(zipFile, extractDir);
  const rootDir = normalizeDir(extractDir);
  const shape = classify(rootDir);
  log(`   形态：${shape.kind}`);

  // 成品包：直接登记，不重新打包（重打会改变字节，sha 与作者给的不一致）
  if (shape.kind === 'packed') {
    const manifest = JSON.parse(String(readZipEntry(shape.file, 'plugin.json') || '{}'));
    if (!manifest.id || !manifest.version)
      throw new UserError('zip 里的 .cblplugin 缺 plugin.json 或缺 id/version');

    const meta = {};
    const targetDir = path.join(ROOT, PKG_DIR, manifest.id);
    const target = path.join(targetDir, `${manifest.id}-${manifest.version}.cblplugin`);
    fs.mkdirSync(targetDir, { recursive: true });
    fs.copyFileSync(shape.file, target);
    return finish(manifest, meta, target, null);
  }

  // 源码型用 csproj 所在目录，预编译目录型用它自己的目录（两者都可能带 market.json）
  let sourceDir = shape.sourceDir ?? shape.dir;
  let outDir;
  let builtFromSource = false;

  if (shape.kind === 'source') {
    // 首选：把引用改成绝对路径（与目录层级无关）；改不动才退回"物理放置"的老办法
    if (!pointContractAtUpstream(shape.project)) ensureAbstractions(shape.project);
    outDir = path.join(workDir, 'out');
    log('   dotnet build …');
    buildProject(shape.project, outDir, workDir);
    if (!fs.existsSync(path.join(outDir, 'plugin.json'))) {
      fs.copyFileSync(path.join(sourceDir, 'plugin.json'), path.join(outDir, 'plugin.json'));
      log('   ⚠ 输出目录没有 plugin.json（csproj 少写了 CopyToOutputDirectory），已从源码目录补一份');
    }
    builtFromSource = true;
  } else {
    outDir = shape.dir;
  }

  removeMeta(outDir);   // 别把 market.json 打进插件包
  if (builtFromSource) sanitizeOutput(outDir);
  const manifest = readJson(path.join(outDir, 'plugin.json'));
  const meta = readMeta(sourceDir);

  // 目标路径先建好（pack 会写进去；体积闸门在 finish 里按实际大小判断）
  const targetDir = path.join(ROOT, PKG_DIR, manifest.id);
  const target = path.join(targetDir, `${manifest.id}-${manifest.version}.cblplugin`);
  fs.mkdirSync(targetDir, { recursive: true });

  log('   打包 …');
  // ★ 必须是 `--o`（双横线）：market.mjs 的解析器只认 `--` 开头的 flag，
  //   写成 `-o` 会被当成第二个位置参数，包会落到默认路径上。
  market('pack', outDir, '--o', target);

  const fallback = path.join(targetDir, `${manifest.id}-${manifest.version}.cblplugin`);
  const pkgPath = fs.existsSync(target) ? target
    : fs.existsSync(fallback) ? fallback
    : null;
  if (!pkgPath) throw new UserError(`打包后找不到产物（期望 ${target}）`);

  return finish(manifest, meta, pkgPath, {
    sourceDir: builtFromSource ? sourceDir : null,
    builtFromSource,
    workDir,
  });
}

/** 校验 + add + 源码归档 + 旧版清理，成功返回发布记录。 */
function finish(manifest, meta, pkgPath, ctx) {
  const size = fs.statSync(pkgPath).size;
  gate(manifest, meta, size);

  const guard = checkVersion(manifest, pkgPath);
  if (guard.skipped) {
    // 同 id 同版本同内容：包已经在正确位置了，**什么都不用动**。
    // 早先这里会 rmSync(pkgPath)，但那个路径正是 entries 指向的文件 ——
    // 删掉之后索引立刻指向一个不存在的包，verify 当场失败。
    log(`   = 已存在 ${manifest.id}@${manifest.version}（内容一致），跳过`);
    return { skipped: true, id: manifest.id, version: manifest.version, reason: '已存在且内容一致' };
  }

  const summary = (meta.summary || manifest.description || manifest.name || '')
    .toString().trim().slice(0, 60);
  if (!summary) throw new UserError('缺少 summary：market.json 里补一个，或给 plugin.json 写 description');

  const trust = resolveTrust(meta);
  const addArgs = [
    'add', pkgPath, '--root', ROOT,
    '--id', manifest.id,
    '--summary', summary,
    '--name', (manifest.name || manifest.id).toString().slice(0, 40),
    '--author', (meta.author || manifest.author || '未知作者').toString(),
    '--category', (meta.category || 'other').toString(),
    '--trust', trust,
    '--published-at', today(),
  ];
  const minApp = meta.min_app_version || manifest.min_app_version;
  if (minApp) addArgs.push('--min-app-version', String(minApp));
  if (meta.license) addArgs.push('--license', String(meta.license));
  if (meta.homepage) addArgs.push('--homepage', String(meta.homepage));
  if (meta.tags?.length) addArgs.push('--tags', meta.tags.join(','));
  if (meta.changelog?.length)
    addArgs.push('--changelog', meta.changelog.map((c) => String(c).replace(/\|/g, '/')).join('|'));
  if (ctx?.builtFromSource)
    addArgs.push('--repo', `https://gitee.com/lchxxxx/class-daily-land-2/tree/plugins/sources/${manifest.id}`);
  else if (meta.repo) addArgs.push('--repo', String(meta.repo));

  // 图标：放进 icons/<id>.png，market.mjs 的 build 会自动带上 icon 字段（add 没有这个参数）
  if (meta.icon && ctx?.sourceDir) {
    const iconFile = path.join(ctx.sourceDir, String(meta.icon));
    if (fs.existsSync(iconFile)) {
      const iconDir = path.join(ROOT, 'icons');
      fs.mkdirSync(iconDir, { recursive: true });
      fs.copyFileSync(iconFile, path.join(iconDir, `${manifest.id}.png`));
    } else {
      log(`   ⚠ market.json 指定的图标不存在：${meta.icon}`);
    }
  }

  market(...addArgs);

  // 源码快照（供审计与复现构建）：剔除 bin/obj 与 market.json
  if (ctx?.builtFromSource && ctx.sourceDir) {
    const dest = path.join(ROOT, 'sources', manifest.id);
    fs.rmSync(dest, { recursive: true, force: true });
    fs.cpSync(ctx.sourceDir, dest, {
      recursive: true,
      filter: (src) => {
        const name = path.basename(src).toLowerCase();
        return !['bin', 'obj', 'market.json'].includes(name);
      },
    });
    const total = walk(dest, () => true).reduce((s, f) => s + fs.statSync(f).size, 0);
    if (total > LIMITS.sourcesMax) {
      log(`   ⚠ 源码快照 ${mb(total)} 超过 ${mb(LIMITS.sourcesMax)}，不归档`);
      fs.rmSync(dest, { recursive: true, force: true });
    }
  }

  pruneOldVersions(manifest.id, manifest.version);

  const record = {
    id: manifest.id,
    version: manifest.version,
    sha256: sha256(pkgPath),
    size,
    trust,
  };
  log(`   ✔ ${record.id}@${record.version}  ${mb(size)}  ${record.sha256.slice(0, 12)}…`);
  return record;
}

/** 保留最近 N 个版本（索引只列最新，旧包对用户没意义，但会永久占 git 历史）。 */
function pruneOldVersions(id, currentVersion) {
  const dir = path.join(ROOT, PKG_DIR, id);
  if (!fs.existsSync(dir)) return;

  const files = fs.readdirSync(dir).filter((f) => f.toLowerCase().endsWith('.cblplugin'));
  const versions = files
    .map((f) => ({ file: f, version: (f.match(/-(\d[\w.+-]*)\.cblplugin$/i) || [])[1] || '0' }))
    .sort((a, b) => compareVersion(b.version, a.version));

  for (const item of versions.slice(LIMITS.keepVersions)) {
    if (compareVersion(item.version, currentVersion) >= 0) continue;   // 绝不删当前版本
    fs.rmSync(path.join(dir, item.file), { force: true });
    log(`   − 清理旧版本 ${item.file}`);
  }
}

// ————————————————————— 索引 —————————————————————

function totalPackagesSize() {
  const dir = path.join(ROOT, PKG_DIR);
  if (!fs.existsSync(dir)) return 0;
  return walk(dir, () => true).reduce((s, f) => s + fs.statSync(f).size, 0);
}

function rebuildIndex() {
  const total = totalPackagesSize();
  if (total > LIMITS.totalHard)
    throw new UserError(`${PKG_DIR}/ 总量 ${mb(total)} 超过 ${mb(LIMITS.totalHard)} 硬上限，拒绝发布`);
  if (total > LIMITS.totalWarn)
    log(`⚠ ${PKG_DIR}/ 总量 ${mb(total)}，已超过 ${mb(LIMITS.totalWarn)} 建议值`);

  // 只兜底 index.json 这一个文件：build 失败时可能留下半成品。
  // ★ 绝不在这里做 `git clean` —— entries/packages 在分支上本来就可能是"本次新增"的
  //   未跟踪文件，clean 会把它们连根删掉（本机实测过，数据直接没了）。
  //   工作树是 CI 的一次性 checkout；本地手跑时由作者自己看 diff。
  const indexPath = path.join(ROOT, 'index.json');
  const backup = path.join(WORK, 'index.json.before');
  const hadIndex = fs.existsSync(indexPath);
  if (hadIndex) fs.copyFileSync(indexPath, backup);

  try {
    market('verify', '--root', ROOT);
    market('build', '--root', ROOT);
    market('verify', '--root', ROOT);
    log('✔ index.json 已重建并通过校验');
  } catch (error) {
    if (hadIndex && fs.existsSync(backup)) fs.copyFileSync(backup, indexPath);
    else fs.rmSync(indexPath, { force: true });
    throw error;
  }
}

// ————————————————————— 错误转人话 —————————————————————

function explain(error) {
  const raw = `${error?.stdout || ''}\n${error?.stderr || ''}\n${error?.message || ''}`;
  const hints = [];

  if (/NU1201|CS0246.*ClassDailyLand2\.Plugin/i.test(raw))
    hints.push('TargetFramework 必须是 net10.0-windows10.0.19041.0 —— docs/plugins.md 里写的 net10.0 是过时的');
  if (/MSB3202/.test(raw))
    hints.push('找不到契约工程：检查 csproj 里 `..\\..\\ClassDailyLand2.Plugin.Abstractions\\` 的相对层级');
  if (error instanceof UserError) return error.message;
  if (/这些 dll 在子目录里/.test(raw))
    hints.push('把 runtimes/ 之类子目录里的 dll 提到插件根目录（宿主只在一级目录找依赖）');
  if (/包里有不该带的东西/.test(raw))
    hints.push('包里不要带 ClassDailyLand2.Plugin.Abstractions.dll、*.pdb、data/');
  if (/entry 只能是插件目录下的文件名/.test(raw))
    hints.push('plugin.json 的 entry 只能写文件名，不能带路径');

  const head = (error?.message || '未知错误').split('\n').slice(0, 6).join('\n');
  return hints.length ? `${head}\n\n提示：${hints.join('；')}` : head;
}

// ————————————————————— 主流程 —————————————————————

function main() {
  fs.mkdirSync(WORK, { recursive: true });
  fs.mkdirSync(path.dirname(RESULT), { recursive: true });

  const zips = fs.existsSync(INBOX)
    ? fs.readdirSync(INBOX).filter((f) => f.toLowerCase().endsWith('.zip')).sort()
    : [];

  log(`收件箱：${zips.length} 个 zip${REBUILD ? '，并且要求重建索引' : ''}${DRY ? '（dry-run）' : ''}`);

  if (zips.length === 0 && !REBUILD) {
    log('没有要处理的包，也没有要求重建索引 —— 直接退出');
    writeResult();
    return;
  }

  const processed = loadProcessed();
  /** 本批待登记的指纹：**索引校验通过后**才写盘（见 main 末尾）。 */
  const pendingPrints = new Map();

  for (const zipName of zips) {
    const source = path.join(INBOX, zipName);
    const zipSha = sha256(source);

    // ① 同一份文件之前处理过（作者本地残留又推了一次）：不解压、不编译、不打包
    if (processed[zipSha]) {
      const done = processed[zipSha];
      log(`\n▶ ${zipName}\n   = 与已发布的 ${done.id}@${done.version} 是同一份文件（sha ${zipSha.slice(0, 12)}…），跳过，不重新打包`);
      fs.rmSync(source, { force: true });
      results.skipped.push({
        id: done.id, version: done.version, zip: zipName,
        reason: '该文件之前已发布过，未重新打包',
      });
      continue;
    }

    const workDir = path.join(WORK, slugify(path.basename(zipName, path.extname(zipName))));
    try {
      const record = publishOne(source, workDir);
      if (record.skipped) results.skipped.push(record);
      else results.published.push(record);

      // 先记在内存里；索引重建并校验通过后才真正落盘（否则"发布失败"会被误记成"处理过"）
      pendingPrints.set(zipSha, {
        id: record.id, version: record.version, at: new Date().toISOString(),
      });
      fs.rmSync(source, { force: true });          // 处理完就消费掉
    } catch (error) {
      // 失败**不记指纹**：作者改好后重投同一份文件应该能重试
      // 失败也要移出收件箱，否则每次 push 都会重跑同一个坏包，收件箱永久堵塞
      const dir = path.join(ROOT, 'failed', stamp());
      fs.mkdirSync(dir, { recursive: true });
      fs.copyFileSync(source, path.join(dir, zipName));
      fs.writeFileSync(path.join(dir, `${zipName}.reason.txt`), explain(error) + '\n', 'utf8');
      fs.rmSync(source, { force: true });
      results.failed.push({ zip: zipName, error: explain(error) });
      log(`   ✖ 失败，证据已放入 failed/${path.basename(dir)}/`);
    } finally {
      fs.rmSync(workDir, { recursive: true, force: true });
    }
  }

  // 指纹统一在索引校验通过后写（见本函数末尾），这里先不动盘

  // 只有真的新增了包（或要求重建）才重算索引；全是"已存在"时不动索引，避免无意义的 diff
  if (results.published.length === 0 && !REBUILD) {
    log('本批没有新发布，索引保持不变');
    writeResult();
    return;
  }

  try {
    rebuildIndex();
  } catch (error) {
    // 索引这一步失败 = 这一批不可信：只还原 index.json（见 rebuildIndex 里的注释），
    // 其余改动留在工作树里给人看，workflow 侧不会提交（publish.mjs 会以非零码退出）。
    log('✖ 索引重建失败');
    results.failed.push({ zip: '(索引)', error: explain(error) });
    writeResult();
    process.exit(1);
  }

  // ★ 索引校验通过 = 这批包真的进市场了，这时才把指纹落盘。
  //   如果在 publishOne 里就写，一旦索引失败，同一份文件下次会被误判成"处理过"而拒绝重试。
  for (const [sha, info] of pendingPrints) processed[sha] = info;
  saveProcessed(processed);

  writeResult();
}

function writeResult() {
  fs.writeFileSync(RESULT, JSON.stringify(results, null, 2), 'utf8');
  fs.writeFileSync(path.join(WORK, 'commit-msg.txt'), buildCommitMessage(), 'utf8');

  log('\n===== 结果 =====');
  for (const p of results.published) log(`发布 ${p.id}@${p.version}`);
  for (const s of results.skipped) log(`跳过 ${s.id}@${s.version}（已存在）`);
  for (const f of results.failed) log(`失败 ${f.zip}：${f.error.split('\n')[0]}`);

  if (results.failed.length) process.exitCode = 0;   // 失败由 workflow 的步骤决定是否置红（推送已完成）
}

function buildCommitMessage() {
  const lines = [];
  const total = results.published.length;
  lines.push(total ? `market: 自动发布 ${total} 个插件` : 'market: 重建索引');
  lines.push('');
  if (results.published.length) {
    lines.push('published:');
    for (const p of results.published) lines.push(`- ${p.id}@${p.version} ${p.sha256.slice(0, 12)}…`);
  }
  if (results.skipped.length) {
    lines.push('skipped（已存在，内容一致）:');
    for (const s of results.skipped) lines.push(`- ${s.id}@${s.version}`);
  }
  if (results.failed.length) {
    lines.push('failed:');
    for (const f of results.failed) lines.push(`- ${f.zip} → ${f.error.split('\n')[0]}`);
  }
  lines.push('');
  lines.push('[skip ci]');   // 防自触发的兜底之一
  return lines.join('\n') + '\n';
}

try {
  main();
} catch (error) {
  console.error('✖ 编排器异常：' + explain(error));
  try { writeResult(); } catch { /* 结果写不出来也别掩盖原始错误 */ }
  process.exit(1);
}
