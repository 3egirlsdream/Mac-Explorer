import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import { deflateRawSync } from 'node:zlib';

// Load the exact browser module without relying on a repository-wide package.json type.
const code = await readFile(new URL('../../docs/developers/package.js', import.meta.url), 'utf8');
const { readManifest } = await import('data:text/javascript;base64,' + Buffer.from(code).toString('base64'));
const valid = { id: 'test.plugin', version: '1.0.0', name: '测试插件', commands: [{ id: 'run', title: '运行' }] };

function crc32(bytes) {
  let crc = 0xffffffff;
  for (const value of bytes) {
    crc ^= value;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function zip(value = valid, deflate = false) {
  const name = Buffer.from('plugin.json');
  const plain = Buffer.from(JSON.stringify(value));
  const payload = deflate ? deflateRawSync(plain) : plain;
  const local = Buffer.alloc(30);
  local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4);
  local.writeUInt16LE(deflate ? 8 : 0, 8); local.writeUInt32LE(crc32(plain), 14);
  local.writeUInt32LE(payload.length, 18); local.writeUInt32LE(plain.length, 22); local.writeUInt16LE(name.length, 26);
  const central = Buffer.alloc(46);
  central.writeUInt32LE(0x02014b50, 0); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6);
  central.writeUInt16LE(deflate ? 8 : 0, 10); central.writeUInt32LE(crc32(plain), 16);
  central.writeUInt32LE(payload.length, 20); central.writeUInt32LE(plain.length, 24); central.writeUInt16LE(name.length, 28);
  const offset = local.length + name.length + payload.length;
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(1, 8); end.writeUInt16LE(1, 10);
  end.writeUInt32LE(central.length + name.length, 12); end.writeUInt32LE(offset, 16);
  return { bytes: Buffer.concat([local, name, payload, central, name, end]), offset, length: plain.length };
}
const read = bytes => readManifest(new Blob([bytes]));

for (const compressed of [false, true]) test(`valid ${compressed ? 'deflated' : 'stored'} ZIP`, async () => {
  assert.deepEqual(await read(zip(valid, compressed).bytes), valid);
});

test('reject truncated central directory record', async () => {
  const { bytes, offset } = zip(); bytes.writeUInt16LE(100, offset + 30);
  await assert.rejects(read(bytes), /目录损坏/);
});
test('reject mismatched uncompressed length', async () => {
  const { bytes, offset, length } = zip(); bytes.writeUInt32LE(length + 1, offset + 24);
  await assert.rejects(read(bytes), /解压长度/);
});
test('reject mismatched local entry name', async () => {
  const { bytes } = zip(); bytes[30] = 'x'.charCodeAt(0);
  await assert.rejects(read(bytes), /名称不一致/);
});
test('reject encrypted manifest instead of treating it as plain JSON', async () => {
  const { bytes, offset } = zip(); bytes.writeUInt16LE(1, offset + 8);
  await assert.rejects(read(bytes), /加密/);
});
test('reject declared entry count mismatch', async () => {
  const { bytes } = zip(); bytes.writeUInt16LE(2, bytes.length - 14); bytes.writeUInt16LE(2, bytes.length - 12);
  await assert.rejects(read(bytes), /条目数/);
});
test('reject content fields that only pass through JS coercion', async () => {
  await assert.rejects(read(zip({ ...valid, id: ['test.plugin'], name: [] }).bytes), /清单格式/);
});
test('reject null manifest with a useful message', async () => {
  await assert.rejects(read(zip(null).bytes), /清单格式/);
});
test('reject compressed bytes that overlap the central directory', async () => {
  const { bytes, offset, length } = zip(); bytes.writeUInt32LE(length + 1, offset + 20);
  await assert.rejects(read(bytes), /越界/);
});

for (const [name, commands] of [
  ['empty commands', []],
  ['null command', [null]],
  ['missing title', [{ id: 'run' }]],
  ['non-string title', [{ id: 'run', title: [] }]],
  ['blank title', [{ id: 'run', title: '   ' }]],
  ['invalid id', [{ id: '../run', title: 'Run' }]],
  ['duplicate id', [{ id: 'run', title: 'One' }, { id: 'run', title: 'Two' }]],
  ['too many commands', Array.from({ length: 101 }, (_, id) => ({ id: `run${id}`, title: 'Run' }))]
]) test(`reject ${name} before preview enables publication`, async () => {
  await assert.rejects(read(zip({ ...valid, commands }).bytes), /插件命令/);
});
