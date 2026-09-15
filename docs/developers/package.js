// Read only plugin.json using the ZIP central directory, without extracting executables.
export async function readManifest(file) {
  if (file.size > 512 * 1024 * 1024 || file.size < 22) throw new Error('插件包大小无效。');
  const tailStart = Math.max(0, file.size - 65557);
  const tail = new DataView(await file.slice(tailStart).arrayBuffer());
  let end = tail.byteLength - 22;
  for (; end >= 0; end--) if (tail.getUint32(end, true) === 0x06054b50 && end + 22 + tail.getUint16(end + 20, true) === tail.byteLength) break;
  if (end < 0) throw new Error('不是有效的 ZIP 插件包。');
  const length = tail.getUint32(end + 12, true), offset = tail.getUint32(end + 16, true);
  if (length > 16 * 1024 * 1024 || offset + length > file.size) throw new Error('插件目录过大或无效。');
  const bytes = new Uint8Array(await file.slice(offset, offset + length).arrayBuffer());
  const view = new DataView(bytes.buffer); let manifest = null;
  for (let at = 0; at < bytes.length;) {
    if (at + 46 > bytes.length || view.getUint32(at, true) !== 0x02014b50) throw new Error('插件目录损坏。');
    const nameLength = view.getUint16(at + 28, true), extraLength = view.getUint16(at + 30, true), commentLength = view.getUint16(at + 32, true);
    const name = new TextDecoder().decode(bytes.slice(at + 46, at + 46 + nameLength));
    if (name === 'plugin.json') {
      if (manifest) throw new Error('插件清单重复。');
      manifest = { method: view.getUint16(at + 10, true), size: view.getUint32(at + 20, true), unpacked: view.getUint32(at + 24, true), local: view.getUint32(at + 42, true) };
    }
    at += 46 + nameLength + extraLength + commentLength;
  }
  if (!manifest || manifest.unpacked > 1024 * 1024 || manifest.size > 1024 * 1024) throw new Error('缺少清单或清单过大。');
  const local = new DataView(await file.slice(manifest.local, manifest.local + 30).arrayBuffer());
  if (local.byteLength !== 30 || local.getUint32(0, true) !== 0x04034b50) throw new Error('清单数据损坏。');
  const start = manifest.local + 30 + local.getUint16(26, true) + local.getUint16(28, true);
  let stream = file.slice(start, start + manifest.size).stream();
  if (manifest.method === 8) stream = stream.pipeThrough(new DecompressionStream('deflate-raw'));
  else if (manifest.method !== 0) throw new Error('不支持此 ZIP 压缩方式。');
  const reader = stream.getReader(), chunks = []; let total = 0;
  try {
    while (true) { const { value, done } = await reader.read(); if (done) break; total += value.length; if (total > 1024 * 1024) throw new Error('清单过大。'); chunks.push(value); }
  } finally { await reader.cancel(); }
  const all = new Uint8Array(total); let cursor = 0; for (const chunk of chunks) { all.set(chunk, cursor); cursor += chunk.length; }
  const result = JSON.parse(new TextDecoder().decode(all));
  if (!/^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$/.test(result.id) || !/^\d+\.\d+(\.\d+){0,2}$/.test(result.version) || !result.name || !Array.isArray(result.commands)) throw new Error('清单格式不正确。');
  return result;
}
