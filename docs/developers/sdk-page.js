import { loadLatestSdk } from './sdk-download.mjs';
const status = document.getElementById('sdk-status');
const retry = document.getElementById('retry');
const download = document.getElementById('sdk-download');
async function start() {
  retry.disabled = true; download.hidden = true; status.textContent = '正在获取最新 SDK…';
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 15000);
  try {
    const sdk = await loadLatestSdk(fetch, controller.signal);
    status.textContent = `SDK ${sdk.version}`;
    download.href = sdk.url; download.hidden = false;
    location.assign(sdk.url);
  } catch (error) { status.textContent = error.name === 'AbortError' ? '连接超时，请重试。' : error.message; }
  finally { clearTimeout(timeout); retry.disabled = false; }
}
retry.onclick = start;
start();
