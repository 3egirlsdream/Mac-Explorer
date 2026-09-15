export const sdkReleasesUrl = 'https://github.com/3egirlsdream/Mac-Explorer/releases?q=sdk-v';
const repository = '3egirlsdream/Mac-Explorer';
const pattern = /^sdk-v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
export function selectSdkRelease(releases) {
  const candidates = releases.filter(r => !r.draft && !r.prerelease && pattern.test(r.tag_name || ''));
  candidates.sort((a, b) => {
    const left = a.tag_name.slice(5).split('.').map(BigInt), right = b.tag_name.slice(5).split('.').map(BigInt);
    for (let i = 0; i < 3; i++) if (left[i] !== right[i]) return left[i] > right[i] ? -1 : 1;
    return 0;
  });
  const release = candidates[0];
  if (!release) throw new Error('SDK 尚未发布。');
  const version = release.tag_name.slice(5), name = `MacExplorer-PluginSDK-${version}.zip`;
  const asset = release.assets?.find(a => a.name === name && a.state === 'uploaded' && a.size > 0);
  const expected = `https://github.com/${repository}/releases/download/${release.tag_name}/${name}`;
  if (!asset || asset.browser_download_url !== expected) throw new Error('最新 SDK 下载包暂不可用。');
  return { version, url: expected };
}
export async function loadLatestSdk(fetcher = fetch, signal) {
  const releases = [];
  for (let page = 1; ; page++) {
    let response;
    try {
      response = await fetcher(`https://api.github.com/repos/${repository}/releases?per_page=100&page=${page}`, {
        headers: { Accept: 'application/vnd.github+json' }, signal
      });
    } catch (error) {
      if (error.name === 'AbortError') throw error;
      throw new Error('暂时无法获取 SDK，请稍后重试。');
    }
    if (!response.ok) throw new Error('暂时无法获取 SDK，请稍后重试。');
    let items;
    try { items = await response.json(); } catch { throw new Error('SDK 版本信息无效。'); }
    if (!Array.isArray(items)) throw new Error('SDK 版本信息无效。');
    releases.push(...items);
    if (items.length < 100) return selectSdkRelease(releases);
  }
}
