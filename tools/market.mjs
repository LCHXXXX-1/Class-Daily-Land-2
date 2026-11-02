#!/usr/bin/env node
// ============================================================================
//  market.mjs — 插件市场工具链（打包 / 上架 / 生成索引）
//  ---------------------------------------------------------------------------
//  插件市场的三种文件，各管一件事：
//    entries/<id>.json                     作者提交的「输入」，一插件一文件（PR 友好）
//    index.json                            工具生成的「输出」，一文件装全部插件（客户端只请求它）
//    已完成插件/<id>/<id>-<ver>.cblplugin  整包（含入口 dll）
//
//  用法：
//    node tools/market.mjs pack  <插件输出目录> [-o 包路径]
//    node tools/market.mjs add   <包路径> --id <id> [--name ..] [--summary ..] ...
//    node tools/market.mjs build                       # 生成 index.json
//    node tools/market.mjs verify                      # 只校验不写文件（CI 用）
//    node tools/market.mjs list                        # 打印当前市场内容
//
//  通用参数：--root <市场根目录>（默认 ./market）
//
//  ★ 打包规则（三条都是硬约束，检查不过直接失败）：
//    1. plugin.json 必须在包根，entry 指向的 dll 必须在包内；
//    2. 所有 dll 必须平铺在包根 —— 宿主的加载器只在插件目录一级找依赖，
//       放进 runtimes/ 或子目录运行时就是找不到；
//    3. 不带 ClassDailyLand2.Plugin.Abstractions.dll、不带 *.pdb、不带 data/。
// ============================================================================

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { writeZip, listZip, readZipEntry } from './zip.mjs';

// 契约程序集由宿主提供，任何情况下都不随包分发
const PKG_DIR = '已完成插件';
const CONTRACT_DLL = 'classdailyland2.plugin.abstractions.dll';
const EXCLUDE_DIRS = new Set(['data', 'obj', 'bin', '.git', '.vs', 'node_modules']);
const EXCLUDE_EXT = new Set(['.pdb', '.cs', '.csproj', '.sln', '.slnx', '.user', '.log']);
const CATEGORIES = ['timetable', 'weather', 'reminder', 'tools', 'media', 'theme', 'data', 'other'];
const TRUST = ['official', 'verified', 'community'];
const ID_RE = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

// ————————————————————— 小工具 —————————————————————

const fail = (message) => {
  console.error('✖ ' + message);
  process.exit(1);
};
const ok = (message) => console.log('✔ ' + message);
const warn = (message) => console.warn('⚠ ' + message);

function parseArgs(argv) {
  const positional = [];
  const flags = {};
  for (let i = 0; i < argv.length; i++) {
    const token = argv[i];
    if (token.startsWith('--')) {
      const key = token.slice(2);
      const next = argv[i + 1];
      if (next === undefined || next.startsWith('--')) flags[key] = true;
      else {
        flags[key] = next;
        i++;
      }
    } else positional.push(token);
  }
  return { positional, flags };
}

function isoLocal(date = new Date()) {
  const pad = (n) => String(n).padStart(2, '0');
  const offset = -date.getTimezoneOffset();
  const sign = offset >= 0 ? '+' : '-';
  const abs = Math.abs(offset);
  return (
    `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}` +
    `T${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}` +
    `${sign}${pad(Math.floor(abs / 60))}:${pad(abs % 60)}`
  );
}

const today = () => new Date().toISOString().slice(0, 10);
const sha256 = (file) => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex').toUpperCase();
const readJson = (file) => JSON.parse(fs.readFileSync(file, 'utf8'));
const writeJson = (file, value) => {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(value, null, 2) + '\n', 'utf8');
};

function marketRoot(flags) {
  const root = path.resolve(flags.root ?? 'market');
  for (const dir of ['entries', PKG_DIR]) fs.mkdirSync(path.join(root, dir), { recursive: true });
  return root;
}

// ————————————————————— pack：把插件目录打成 .cblplugin —————————————————————

function collectFiles(dir, base = '') {
  const result = [];
  for (const item of fs.readdirSync(dir, { withFileTypes: true })) {
    const rel = base ? `${base}/${item.name}` : item.name;
    if (item.isDirectory()) {
      if (EXCLUDE_DIRS.has(item.name.toLowerCase())) continue;
      result.push(...collectFiles(path.join(dir, item.name), rel));
    } else if (item.isFile()) {
      result.push(rel);
    }
  }
  return result;
}

function pack(sourceDir, outFile) {
  const manifestPath = path.join(sourceDir, 'plugin.json');
  if (!fs.existsSync(manifestPath)) fail(`这个目录里没有 plugin.json：${sourceDir}`);

  let manifest;
  try {
    manifest = readJson(manifestPath);
  } catch (error) {
    fail(`plugin.json 不是合法 JSON：${error.message}`);
  }

  if (!ID_RE.test(manifest.id ?? '')) fail(`plugin.json 的 id 不合法：${manifest.id}`);
  if (!manifest.entry || !manifest.entry.toLowerCase().endsWith('.dll')) fail('plugin.json 的 entry 必须是 .dll 文件名');
  if (manifest.entry.includes('/') || manifest.entry.includes('\\')) fail('entry 只能是插件目录下的文件名，不能带路径');

  const all = collectFiles(sourceDir);

  // 规则 1：入口 dll 必须在
  if (!all.includes(manifest.entry)) fail(`找不到入口程序集 ${manifest.entry}（entry 与实际文件不一致）`);

  // 规则 2：dll 必须平铺
  const nested = all.filter((f) => f.toLowerCase().endsWith('.dll') && f.includes('/'));
  if (nested.length) fail(`这些 dll 在子目录里，宿主加载不到，请让它们平铺在插件根目录：\n   ${nested.join('\n   ')}`);

  // 规则 3：契约 dll / data 目录是硬错误（不是"省体积"的问题）
  const fatal = all.filter((rel) => {
    const name = path.basename(rel).toLowerCase();
    return name === CONTRACT_DLL || rel.split('/').some((seg) => seg.toLowerCase() === 'data');
  });
  if (fatal.length) fail(`包里有不该带的东西：\n   ${fatal.map((f) => '   · ' + f).join('\n')}`);

  // 其余（*.pdb / 源码 / 日志）静默剔除：少一份体积，不影响加载
  const skipped = [];
  const kept = all.filter((rel) => {
    if (EXCLUDE_EXT.has(path.extname(rel).toLowerCase())) {
      skipped.push(rel);
      return false;
    }
    return true;
  });

  const entries = kept
    .sort()
    .map((rel) => ({ name: rel, data: fs.readFileSync(path.join(sourceDir, rel)) }));

  fs.mkdirSync(path.dirname(outFile), { recursive: true });
  writeZip(outFile, entries);

  const size = fs.statSync(outFile).size;
  ok(`已打包 ${path.relative(process.cwd(), outFile)}（${entries.length} 个文件，${(size / 1024).toFixed(1)} KB）`);
  for (const entry of entries) console.log(`   · ${entry.name}  ${(entry.data.length / 1024).toFixed(1)} KB`);
  if (skipped.length) console.log(`   已剔除：${skipped.join('、')}`);
  return { outFile, manifest, size };
}

// ————————————————————— add：上架 / 更新一个版本 —————————————————————

const PACKAGE_FORBIDDEN = [
  { test: (n) => path.basename(n).toLowerCase() === CONTRACT_DLL, why: '包内带了契约 dll（由宿主提供，不能随包分发）' },
  { test: (n) => n.toLowerCase().endsWith('.pdb'), why: '包内带了 pdb 调试符号' },
  { test: (n) => n.toLowerCase().startsWith('data/'), why: '包内带了 data/ 用户数据目录' },
];

function inspectPackage(packagePath) {
  if (!fs.existsSync(packagePath)) fail(`找不到安装包：${packagePath}`);
  if (!packagePath.toLowerCase().endsWith('.cblplugin')) fail('安装包扩展名必须是 .cblplugin');

  const names = listZip(packagePath).map((e) => e.name);

  if (!names.includes('plugin.json')) fail('包根目录没有 plugin.json');

  for (const rule of PACKAGE_FORBIDDEN) {
    const hit = names.find(rule.test);
    if (hit) fail(`${rule.why}：${hit}`);
  }

  const manifest = JSON.parse(readZipEntry(packagePath, 'plugin.json').toString('utf8'));
  if (!names.includes(manifest.entry)) fail(`包内没有入口程序集 ${manifest.entry}`);
  if (!ID_RE.test(manifest.id ?? '')) fail(`包内 plugin.json 的 id 不合法：${manifest.id}`);

  return { manifest, names };
}

function add(packagePath, flags) {
  const root = marketRoot(flags);
  const { manifest } = inspectPackage(packagePath);

  const id = flags.id ?? manifest.id;
  if (id !== manifest.id) fail(`--id「${id}」与包内 plugin.json 的 id「${manifest.id}」不一致`);
  if (!ID_RE.test(id)) fail(`id 不合法：${id}`);

  const version = String(manifest.version ?? flags.version ?? '1.0.0');
  if (flags.version && flags.version !== manifest.version) {
    fail(`--version「${flags.version}」与包内 plugin.json 的 version「${manifest.version}」不一致`);
  }

  const category = flags.category ?? 'other';
  if (!CATEGORIES.includes(category)) fail(`category 只能是：${CATEGORIES.join(' / ')}`);

  const trust = flags.trust ?? 'community';
  if (!TRUST.includes(trust)) fail(`trust 只能是：${TRUST.join(' / ')}`);

  const entryPath = path.join(root, 'entries', `${id}.json`);
  const previous = fs.existsSync(entryPath) ? readJson(entryPath) : null;

  const fileName = `${id}-${version}.cblplugin`;
  const targetDir = path.join(root, PKG_DIR, id);
  fs.mkdirSync(targetDir, { recursive: true });
  const targetPackage = path.join(targetDir, fileName);
  if (path.resolve(targetPackage) !== path.resolve(packagePath)) fs.copyFileSync(packagePath, targetPackage);

  const tags = flags.tags ? String(flags.tags).split(',').map((t) => t.trim()).filter(Boolean) : previous?.tags ?? [];
  const changelog = flags.changelog
    ? String(flags.changelog).split('|').map((t) => t.trim()).filter(Boolean)
    : previous?.changelog ?? [];

  const entry = {
    id,
    name: flags.name ?? previous?.name ?? manifest.name ?? id,
    summary: flags.summary ?? previous?.summary ?? '',
    ...(flags.description || previous?.description ? { description: flags.description ?? previous.description } : {}),
    author: flags.author ?? previous?.author ?? manifest.author ?? '',
    version,
    api_version: Number(flags['api-version'] ?? manifest.apiVersion ?? 1),
    ...(flags['min-app-version'] || previous?.min_app_version ? { min_app_version: flags['min-app-version'] ?? previous.min_app_version } : {}),
    ...(flags['target-sdk-version'] || previous?.target_sdk_version || manifest.target_sdk_version
      ? {
          target_sdk_version:
            flags['target-sdk-version'] ?? previous?.target_sdk_version ?? manifest.target_sdk_version,
        }
      : {}),
    category,
    ...(tags.length ? { tags } : {}),
    trust,
    ...(flags.license || previous?.license ? { license: flags.license ?? previous.license } : {}),
    ...(flags.homepage || previous?.homepage ? { homepage: flags.homepage ?? previous.homepage } : {}),
    ...(flags.repo || previous?.repo ? { repo: flags.repo ?? previous.repo } : {}),
    package: fileName,
    published_at: flags['published-at'] ?? today(),
    ...(changelog.length ? { changelog } : {}),
  };

  if (!entry.summary) fail('缺少 --summary（列表页那一句话，必填）');
  if (!entry.author) fail('缺少 --author（plugin.json 里也没有，补上）');

  writeJson(entryPath, entry);

  const size = fs.statSync(targetPackage).size;
  ok(`已上架 ${id} v${version}`);
  console.log(`   entry   ${path.relative(process.cwd(), entryPath)}`);
  console.log(`   package ${path.relative(process.cwd(), targetPackage)}  ${(size / 1024).toFixed(1)} KB`);
  console.log(`   sha256  ${sha256(targetPackage)}`);
  if (previous && previous.version !== version) {
    console.log(`   版本    ${previous.version} → ${version}`);
  }
}

// ————————————————————— build / verify：聚合 index.json —————————————————————

function collectIndex(root, { write }) {
  const entriesDir = path.join(root, 'entries');
  const files = fs.existsSync(entriesDir)
    ? fs.readdirSync(entriesDir).filter((f) => f.endsWith('.json')).sort()
    : [];

  if (!files.length) fail('entries/ 里一个插件都没有，先把包装进来（market.mjs add）');

  const problems = [];
  const plugins = [];
  const seen = new Set();

  for (const file of files) {
    const entry = readJson(path.join(entriesDir, file));
    const where = `entries/${file}`;

    if (!ID_RE.test(entry.id ?? '')) problems.push(`${where}：id 不合法`);
    if (seen.has(entry.id)) problems.push(`${where}：id「${entry.id}」与另一个 entry 重复`);
    seen.add(entry.id);

    for (const field of ['name', 'summary', 'author', 'version', 'category', 'trust', 'package']) {
      if (!entry[field]) problems.push(`${where}：缺少 ${field}`);
    }
    if (!CATEGORIES.includes(entry.category)) problems.push(`${where}：category「${entry.category}」不在枚举里`);
    if (!TRUST.includes(entry.trust)) problems.push(`${where}：trust「${entry.trust}」不在枚举里`);

    const packagePath = path.join(root, PKG_DIR, entry.id, entry.package ?? '');
    if (!entry.package || !fs.existsSync(packagePath)) {
      problems.push(`${where}：找不到安装包 ${PKG_DIR}/${entry.id}/${entry.package}`);
      continue;
    }

    const digest = sha256(packagePath);
    const size = fs.statSync(packagePath).size;

    // 包里的清单必须和 entry 对得上 —— 这是「索引挂 1.2、包里是 1.0」的唯一防线
    let manifest = null;
    try {
      manifest = JSON.parse(readZipEntry(packagePath, 'plugin.json').toString('utf8'));
    } catch {
      problems.push(`${where}：包内没有可读的 plugin.json`);
    }
    if (manifest) {
      if (manifest.id !== entry.id) problems.push(`${where}：包内 id「${manifest.id}」与 entry 不一致`);
      if (String(manifest.version) !== String(entry.version)) {
        problems.push(`${where}：包内 version「${manifest.version}」与 entry「${entry.version}」不一致`);
      }
    }

    const iconName = `icons/${entry.id}.png`;
    const hasIcon = fs.existsSync(path.join(root, iconName));

    plugins.push({
      id: entry.id,
      name: entry.name,
      summary: entry.summary,
      ...(entry.description ? { description: entry.description } : {}),
      author: entry.author,
      version: String(entry.version),
      api_version: Number(entry.api_version ?? 1),
      ...(entry.min_app_version ? { min_app_version: entry.min_app_version } : {}),
      ...(entry.target_sdk_version ? { target_sdk_version: entry.target_sdk_version } : {}),
      category: entry.category,
      ...(entry.tags?.length ? { tags: entry.tags } : {}),
      trust: entry.trust,
      ...(hasIcon ? { icon: iconName } : {}),
      path: `${PKG_DIR}/${entry.id}/${entry.package}`,
      sha256: digest,
      size,
      ...(entry.homepage ? { homepage: entry.homepage } : {}),
      ...(entry.repo ? { repo: entry.repo } : {}),
      ...(entry.license ? { license: entry.license } : {}),
      published_at: entry.published_at ?? today(),
      ...(entry.changelog?.length ? { changelog: entry.changelog } : {}),
      ...(entry.revoked ? { revoked: true } : {}),
    });
  }

  if (problems.length) {
    console.error('✖ 索引校验没通过：');
    for (const problem of problems) console.error('   · ' + problem);
    process.exit(1);
  }

  plugins.sort((a, b) => (a.published_at < b.published_at ? 1 : a.published_at > b.published_at ? -1 : a.name.localeCompare(b.name)));

  const index = { schema: 1, generated_at: isoLocal(), count: plugins.length, plugins };

  if (write) {
    writeJson(path.join(root, 'index.json'), index);
    ok(`已生成 index.json（${plugins.length} 个插件，${(fs.statSync(path.join(root, 'index.json')).size / 1024).toFixed(1)} KB）`);
  } else {
    ok(`校验通过：${plugins.length} 个插件，哈希与包内清单都对得上`);
  }
  return index;
}

// ————————————————————— list —————————————————————

function list(flags) {
  const root = marketRoot(flags);
  const indexPath = path.join(root, 'index.json');
  if (!fs.existsSync(indexPath)) fail('还没有 index.json，先跑 build');
  const index = readJson(indexPath);
  console.log(`index.json  schema=${index.schema}  count=${index.count}  generated_at=${index.generated_at}`);
  for (const p of index.plugins) {
    console.log(
      `  ${p.id.padEnd(26)} v${String(p.version).padEnd(8)} ${p.category.padEnd(10)} ` +
        `${(p.size / 1024).toFixed(1).padStart(7)} KB  ${p.name}`,
    );
  }
}

// ————————————————————— 入口 —————————————————————

const { positional, flags } = parseArgs(process.argv.slice(2));
const [command, ...rest] = positional;

switch (command) {
  case 'pack': {
    const sourceDir = rest[0];
    if (!sourceDir) fail('用法：market.mjs pack <插件输出目录> [-o 包路径]');
    const manifest = readJson(path.join(sourceDir, 'plugin.json'));
    const outFile =
      flags.o ??
      path.join(marketRoot(flags), PKG_DIR, manifest.id, `${manifest.id}-${manifest.version}.cblplugin`);
    pack(path.resolve(sourceDir), path.resolve(outFile));
    break;
  }
  case 'add': {
    const packagePath = rest[0];
    if (!packagePath) fail('用法：market.mjs add <包路径> --id <id> --summary "…"');
    add(path.resolve(packagePath), flags);
    break;
  }
  case 'build':
    collectIndex(marketRoot(flags), { write: true });
    break;
  case 'verify':
    collectIndex(marketRoot(flags), { write: false });
    break;
  case 'list':
    list(flags);
    break;
  default:
    console.log(
      [
        '插件市场工具链',
        '',
        '  node tools/market.mjs pack   <插件输出目录> [-o 包路径]',
        '  node tools/market.mjs add    <包路径> --id <id> --summary "…" [--tags a,b] [--changelog "x|y"]',
        '  node tools/market.mjs build                    生成 index.json',
        '  node tools/market.mjs verify                   只校验不写（CI 用）',
        '  node tools/market.mjs list                     查看当前市场',
        '',
        '通用：--root <市场根目录>（默认 ./market）',
      ].join('\n'),
    );
}
