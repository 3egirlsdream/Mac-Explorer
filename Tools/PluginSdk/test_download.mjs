import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadLatestSdk, selectSdkRelease } from '../../docs/developers/sdk-download.mjs';
function release(version, extra = {}) {
  const tag = `sdk-v${version}`, name = `MacExplorer-PluginSDK-${version}.zip`;
  return { tag_name: tag, draft: false, prerelease: false, assets: [{ name, size: 100, state: 'uploaded', browser_download_url: `https://github.com/3egirlsdream/Mac-Explorer/releases/download/${tag}/${name}` }], ...extra };
}
test('select SDK semver, ignoring host, drafts and previews', () => {
  assert.equal(selectSdkRelease([{ tag_name: 'v99.0.0' }, release('1.2.0'), release('1.10.0'), release('2.0.0', {draft:true}), release('3.0.0', {prerelease:true})]).version, '1.10.0');
});
test('page past host-only releases', async () => {
  let calls = 0;
  const result = await loadLatestSdk(async () => ({ok:true, json:async () => ++calls === 1 ? Array(100).fill({tag_name:'v1.0.44'}) : [release('1.0.0')]}));
  assert.equal(calls, 2); assert.equal(result.version, '1.0.0');
});
test('missing newest ZIP must not download an older SDK', () => {
  assert.throws(() => selectSdkRelease([release('1.0.0'), release('1.1.0', {assets:[]})]));
});
test('network errors and no SDK are explicit', async () => {
  await assert.rejects(loadLatestSdk(async () => ({ok:false})));
  await assert.rejects(loadLatestSdk(async () => { throw new TypeError('Failed to fetch'); }), /暂时无法获取/);
  assert.throws(() => selectSdkRelease([]));
});
test('reject external asset URL', () => {
  const item = release('1.0.0'); item.assets[0].browser_download_url = 'https://example.com/untrusted.zip';
  assert.throws(() => selectSdkRelease([item]));
});
