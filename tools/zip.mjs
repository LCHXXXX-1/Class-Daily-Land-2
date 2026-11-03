// ============================================================================
//  zip.mjs — 极简 zip 读写（无第三方依赖）
//  ---------------------------------------------------------------------------
//  为什么自己写：项目约定 tools/ 下的脚本零依赖（只靠 Node 内置模块）。
//  Node 有 zlib，但没有 zip 容器；容器格式本身只是三段结构：
//      本地文件头 + 数据 …（重复）… 中央目录 … 目录结束记录
//
//  只实现我们真正需要的部分：
//    · 写：deflate 压缩、UTF-8 文件名、固定时间戳（产物可复现）
//    · 读：只列条目名与大小；要内容时按需解压（用于「包内 plugin.json 校验」）
// ============================================================================

import fs from 'node:fs';
import zlib from 'node:zlib';

// 固定时间戳：同一个插件目录打两次包，字节完全一致。
// DOS 时间格式：date = (year-1980)<<9 | month<<5 | day，time = hour<<11 | min<<5 | sec/2
const DOS_DATE = ((2026 - 1980) << 9) | (1 << 5) | 1;
const DOS_TIME = 0;

const CRC_TABLE = (() => {
  const table = new Int32Array(256);
  for (let i = 0; i < 256; i++) {
    let c = i;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[i] = c;
  }
  return table;
})();

function crc32(buf) {
  let c = -1;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ -1) >>> 0;
}

/**
 * 写一个 zip 文件。
 * @param {string} outFile 目标路径
 * @param {{name: string, data: Buffer}[]} entries 名字用 / 分隔，不要以 / 开头
 */
export function writeZip(outFile, entries) {
  const locals = [];
  const centrals = [];
  let offset = 0;

  for (const entry of entries) {
    const name = Buffer.from(entry.name.replace(/\\/g, '/'), 'utf8');
    const raw = entry.data;
    const packed = zlib.deflateRawSync(raw, { level: 9 });
    // 压不小就用存储（小文件常见），避免「压缩后更大」
    const stored = packed.length >= raw.length;
    const body = stored ? raw : packed;
    const crc = crc32(raw);

    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);            // version needed
    local.writeUInt16LE(0x0800, 6);        // flag: 文件名 UTF-8
    local.writeUInt16LE(stored ? 0 : 8, 8); // method
    local.writeUInt16LE(DOS_TIME, 10);
    local.writeUInt16LE(DOS_DATE, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(body.length, 18);
    local.writeUInt32LE(raw.length, 22);
    local.writeUInt16LE(name.length, 26);
    local.writeUInt16LE(0, 28);            // extra
    locals.push(local, name, body);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);          // version made by
    central.writeUInt16LE(20, 6);          // version needed
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt16LE(stored ? 0 : 8, 10);
    central.writeUInt16LE(DOS_TIME, 12);
    central.writeUInt16LE(DOS_DATE, 14);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(body.length, 20);
    central.writeUInt32LE(raw.length, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt16LE(0, 30);          // extra
    central.writeUInt16LE(0, 32);          // comment
    central.writeUInt16LE(0, 34);          // disk
    central.writeUInt16LE(0, 36);          // internal attrs
    central.writeUInt32LE(0, 38);          // external attrs
    central.writeUInt32LE(offset, 42);
    centrals.push(central, name);

    offset += 30 + name.length + body.length;
  }

  const centralBuf = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(0, 4);
  end.writeUInt16LE(0, 6);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(centralBuf.length, 12);
  end.writeUInt32LE(offset, 16);
  end.writeUInt16LE(0, 20);

  fs.writeFileSync(outFile, Buffer.concat([...locals, centralBuf, end]));
}

/**
 * 读 zip 的中央目录。
 * @returns {{name: string, size: number, compressedSize: number, method: number, offset: number}[]}
 */
export function listZip(file) {
  const buf = fs.readFileSync(file);

  // 从尾部找 EOCD（可能带注释，最多回退 64KB）
  let eocd = -1;
  for (let i = buf.length - 22; i >= Math.max(0, buf.length - 65558); i--) {
    if (buf.readUInt32LE(i) === 0x06054b50) { eocd = i; break; }
  }
  if (eocd < 0) throw new Error('不是有效的 zip：找不到目录结束记录');

  const total = buf.readUInt16LE(eocd + 10);
  let ptr = buf.readUInt32LE(eocd + 16);
  const result = [];

  for (let i = 0; i < total; i++) {
    if (buf.readUInt32LE(ptr) !== 0x02014b50) throw new Error('zip 中央目录损坏');
    const method = buf.readUInt16LE(ptr + 10);
    const compressedSize = buf.readUInt32LE(ptr + 20);
    const size = buf.readUInt32LE(ptr + 24);
    const nameLen = buf.readUInt16LE(ptr + 28);
    const extraLen = buf.readUInt16LE(ptr + 30);
    const commentLen = buf.readUInt16LE(ptr + 32);
    const localOffset = buf.readUInt32LE(ptr + 42);
    const name = buf.toString('utf8', ptr + 46, ptr + 46 + nameLen);
    result.push({ name, size, compressedSize, method, offset: localOffset });
    ptr += 46 + nameLen + extraLen + commentLen;
  }
  return result;
}

/** 按条目名解压出内容（只支持 store 与 deflate，足够我们自己打的包）。 */
export function readZipEntry(file, name) {
  const buf = fs.readFileSync(file);
  const entry = listZip(file).find((e) => e.name === name);
  if (!entry) return null;

  const ptr = entry.offset;
  const nameLen = buf.readUInt16LE(ptr + 26);
  const extraLen = buf.readUInt16LE(ptr + 28);
  const start = ptr + 30 + nameLen + extraLen;
  const body = buf.subarray(start, start + entry.compressedSize);

  if (entry.method === 0) return Buffer.from(body);
  if (entry.method === 8) return zlib.inflateRawSync(body);
  throw new Error(`不支持的压缩方式 ${entry.method}`);
}
