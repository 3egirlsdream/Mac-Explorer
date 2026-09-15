import { API_BASE } from './config.js';
import { readManifest } from './package.js';
const $ = id => document.getElementById(id);
let mode = 'login', session = null, selected = null, busy = false, countdown = 0;
try { session = JSON.parse(sessionStorage.getItem('plugin-publisher') || 'null'); } catch { sessionStorage.removeItem('plugin-publisher'); }
if (session && Date.parse(session.expiresAt) <= Date.now()) { session = null; sessionStorage.removeItem('plugin-publisher'); }
function message(id, text, error = false) { $(id).textContent = text; $(id).classList.toggle('error', error); }
async function api(action, body) {
  const response = await fetch(new URL(action, new URL(API_BASE, location.origin)), { method: body === undefined ? 'GET' : 'POST', headers: { 'Content-Type': 'application/json', ...(session ? { 'X-Plugin-Session': session.token } : {}) }, ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
  const data = await response.json();
  if (!response.ok || data.success === false) throw new Error(typeof data.message === 'string' ? data.message : '请求失败，请稍后重试。');
  return data;
}
function saveSession(value) { session = value; if (value) sessionStorage.setItem('plugin-publisher', JSON.stringify(value)); else sessionStorage.removeItem('plugin-publisher'); renderAccount(); }
function setMode(value) {
  mode = value;
  $('account-label').textContent = value === 'login' ? '用户名或邮箱' : '邮箱';
  $('email').type = value === 'login' ? 'text' : 'email';
  $('password').minLength = value === 'login' ? 1 : 6;
  for (const button of document.querySelectorAll('[data-mode]')) button.setAttribute('aria-selected', button.dataset.mode === value);
  $('name-field').hidden = value !== 'register'; $('code-field').hidden = value === 'login'; $('confirm-field').hidden = value === 'login';
  $('submit-account').textContent = value === 'register' ? '注册并登录' : value === 'recover' ? '重置密码' : '登录';
  $('recover').textContent = value === 'recover' ? '返回登录' : '忘记密码？';
  $('password').autocomplete = value === 'login' ? 'current-password' : 'new-password'; message('account-message', '');
}
function renderAccount() {
  $('signed-out').hidden = !!session; $('signed-in').hidden = !session;
  $('identity').textContent = session?.displayName || '';
  $('verified-label').textContent = session?.verified ? '已验证' : '发布前请验证邮箱';
  $('verify-panel').hidden = !session || session.verified;
  $('publish').disabled = busy || !selected || !session?.verified;
}
for (const button of document.querySelectorAll('[data-mode]')) button.onclick = () => setMode(button.dataset.mode);
$('recover').onclick = () => setMode(mode === 'recover' ? 'login' : 'recover');
$('account-form').onsubmit = async event => {
  event.preventDefault(); if (busy) return;
  const email = $('email').value.trim(), password = $('password').value, submittedMode = mode;
  if (mode !== 'login' && password !== $('confirm-password').value) return message('account-message', '两次输入的密码不一致。', true);
  if (mode !== 'login' && !$('code').value.trim()) return message('account-message', '请输入验证码。', true);
  busy = true; $('submit-account').disabled = true; renderAccount();
  try {
    const data = await api(submittedMode === 'register' ? 'Register' : submittedMode === 'recover' ? 'Recover' : 'Login', { ...(submittedMode === 'login' ? { username: email } : { email }), password, displayName: $('display-name').value, code: $('code').value.trim() });
    if (submittedMode === 'recover') { setMode('login'); message('account-message', '密码已重置，请重新登录。'); }
    else { message('account-message',''); saveSession({ ...data, email: email.includes('@') ? email : '' }); $('verification-email').value = session.email; $('password').value = ''; $('confirm-password').value = ''; await loadMine(); }
  } catch (error) { message('account-message', error.message, true); }
  finally { busy = false; $('submit-account').disabled = false; renderAccount(); }
};
async function sendCode() {
  if (countdown) return;
  const email = session ? $('verification-email').value.trim() : $('email').value.trim();
  $('send-code').disabled = $('verify-send').disabled = true;
  try {
    await api('SendCode', { email }); message('account-message', '验证码已发送，请检查邮箱。');
    countdown = 60; const timer = setInterval(() => {
      countdown--; for (const id of ['send-code','verify-send']) { $(id).textContent = countdown ? `${countdown} 秒后重发` : '发送验证码'; $(id).disabled = countdown > 0; }
      if (!countdown) clearInterval(timer);
    }, 1000);
  } catch (error) { message('account-message', error.message, true); $('send-code').disabled = $('verify-send').disabled = false; }
}
$('send-code').onclick = $('verify-send').onclick = sendCode;
$('verify-email').onclick = async () => { try { await api('VerifyEmail', { email: $('verification-email').value.trim(), code: $('verify-code').value.trim() }); saveSession({ ...session, verified: true }); message('account-message', '邮箱已验证。'); } catch (error) { message('account-message', error.message, true); } };
$('logout').onclick = async () => { try { await api('Logout', {}); } finally { saveSession(null); $('my-plugins').textContent = '登录后查看'; } };
async function selectPackage(file) {
  if (busy) return;
  const selection = ++selectionId;
  $('file-drop').classList.remove('has-file');
  $('file-title').textContent = file?.name || '拖入插件包';
  $('file-hint').textContent = file ? `${(file.size / 1024 / 1024).toFixed(1)} MB` : '.mexplug · 最大 512 MB';
  $('file-picker').textContent = file ? '重新选择' : '选择文件';
  selected = null; $('preview').hidden = true; renderAccount(); message('upload-message', '正在读取插件清单…');
  if (!file) { message('upload-message', ''); return; }
  try {
    const manifest = await readManifest(file); if (selection !== selectionId) return;
    $('file-drop').classList.add('has-file');
    selected = { file, manifest }; $('preview').hidden = false; $('plugin-name').textContent = manifest.name; $('plugin-description').textContent = manifest.description || ''; $('plugin-meta').replaceChildren();
    const fields = [['插件 ID',manifest.id],['版本',manifest.version],['平台',`${manifest.platform || 'osx'} / ${manifest.architecture || 'arm64'}`],['使用方式',manifest.paid ? `付费${manifest.trialDays ? ` · 试用 ${manifest.trialDays} 天` : ''}` : '免费'],['功能',manifest.commands.map(c => c.title).join('、')]];
    for (const [label,value] of fields) { const dt = document.createElement('dt'), dd = document.createElement('dd'); dt.textContent = label; dd.textContent = value; $('plugin-meta').append(dt,dd); }
    message('upload-message','');
  } catch (error) { if (selection !== selectionId) return; message('upload-message', error.message, true); }
  renderAccount();
}
let selectionId = 0;
$('package').onchange = () => selectPackage($('package').files[0]);
$('file-drop').ondragover = event => { event.preventDefault(); if (!busy) $('file-drop').classList.add('dragging'); };
$('file-drop').ondragleave = event => { if (!$('file-drop').contains(event.relatedTarget)) $('file-drop').classList.remove('dragging'); };
$('file-drop').ondrop = event => {
  event.preventDefault(); $('file-drop').classList.remove('dragging');
  if (busy) return;
  if (event.dataTransfer.files.length !== 1) return message('upload-message', '请选择一个插件包。', true);
  $('package').value = ''; selectPackage(event.dataTransfer.files[0]);
};
function upload(ticket,file) {
  return new Promise((resolve,reject) => {
    const xhr = new XMLHttpRequest(); xhr.open('POST',ticket.uploadUrl); xhr.timeout = 10 * 60 * 1000;
    xhr.upload.onprogress = event => { if (event.lengthComputable) { $('upload-progress').value = event.loaded * 100 / event.total; message('upload-message', `正在上传 ${Math.round(event.loaded * 100 / event.total)}%`); } };
    xhr.onload = () => xhr.status >= 200 && xhr.status < 300 ? resolve() : reject(new Error('上传失败，请重新选择发布。'));
    xhr.onerror = xhr.ontimeout = () => reject(new Error('上传中断，请重试。'));
    const data = new FormData(); data.append('token',ticket.token); data.append('key',ticket.key); data.append('file',file); xhr.send(data);
  });
}
$('publish').onclick = async () => {
  if (!selected || busy || !session?.verified) return;
  const { file, manifest } = selected; busy = true; renderAccount(); $('package').disabled = true; $('file-drop').classList.add('is-busy'); $('upload-progress').hidden = false;
  try {
    const ticket = await api('UploadToken', { pluginId: manifest.id, version: manifest.version, size: file.size });
    await upload(ticket,file); message('upload-message','上传完成，正在校验并上架…');
    await api('Complete', { uploadId: ticket.uploadId }); message('upload-message', `${manifest.name} ${manifest.version} 已上架。`); await loadMine();
  } catch (error) { message('upload-message',error.message,true); }
  finally { busy = false; $('package').disabled = false; $('file-drop').classList.remove('is-busy'); $('upload-progress').hidden = true; renderAccount(); }
};
async function loadMine() {
  if (!session) return;
  try {
    const items = await api('Mine'); $('my-plugins').replaceChildren();
    if (!items.length) $('my-plugins').textContent = '暂无插件';
    for (const item of items) {
      const row = document.createElement('div'); row.className = 'plugin-row'; const info = document.createElement('div'), title = document.createElement('strong'), meta = document.createElement('p');
      title.textContent = item.name; meta.textContent = `${item.id} · ${item.blocked ? '已停用' : item.listed ? '已上架' : '未上架'}`; info.append(title,meta); row.append(info);
      if (item.listed && !item.blocked) { const button = document.createElement('button'); button.textContent = '下架'; button.onclick = async () => { if (!confirm(`下架 ${item.name}？已安装的用户仍可使用。`)) return; button.disabled = true; try { await api('Unlist',{ pluginId:item.id }); await loadMine(); } catch (error) { message('upload-message',error.message,true); button.disabled = false; } }; row.append(button); }
      $('my-plugins').append(row);
    }
  } catch (error) { message('account-message',error.message,true); }
}
$('refresh-mine').onclick = loadMine;
$('verification-email').value = session?.email || '';
renderAccount(); loadMine();
