import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import vm from 'node:vm';

// Execute the production controller with a minimal DOM and controlled dependencies.
// This verifies state/async behavior, not browser rendering or the live account API.
const source = (await readFile(new URL('../../docs/developers/developer.js', import.meta.url), 'utf8'))
  .replace(/^import .*;\r?\n/gm, '');
const account = (token = 'owner-a') => ({ token, displayName: token, expiresAt: '2099-01-01T00:00:00Z' });
const manifest = { id: 'test.plugin', version: '1.0.0', name: 'Plugin', commands: [{ id: 'run', title: 'Run' }] };
const file = { name: 'test.mexplug', size: 100 };
const response = data => ({ ok: true, json: async () => data });
const settle = () => new Promise(resolve => setImmediate(resolve));
function deferred() {
  let resolve, reject;
  const promise = new Promise((a, b) => { resolve = a; reject = b; });
  return { promise, resolve, reject };
}
class Element {
  children = [];
  value = '';
  hidden = false;
  disabled = false;
  dataset = {};
  classes = new Set();
  classList = {
    add: name => this.classes.add(name),
    remove: name => this.classes.delete(name),
    toggle: (name, enabled) => enabled ? this.classes.add(name) : this.classes.delete(name)
  };
  set textContent(value) { this.text = String(value); this.children = []; }
  get textContent() { return (this.text || '') + this.children.map(child => child.textContent).join(''); }
  replaceChildren(...children) { this.text = ''; this.children = children; }
  append(...children) { this.children.push(...children); }
  setAttribute() {}
  contains(child) { return this.children.includes(child); }
}
function environment({ stored = null, storageDenied = false, fetch: fetcher, readManifest = async () => manifest } = {}) {
  const elements = new Map(), calls = [];
  const el = id => {
    if (!elements.has(id)) elements.set(id, new Element());
    return elements.get(id);
  };
  const storage = {
    getItem() { if (storageDenied) throw new Error('Storage denied'); return stored; },
    setItem(_, value) { if (storageDenied) throw new Error('Storage denied'); stored = value; },
    removeItem() { if (storageDenied) throw new Error('Storage denied'); stored = null; }
  };
  const context = vm.createContext({
    API_BASE: 'https://example.test/api/', URL, location: { origin: 'https://example.test' },
    document: { getElementById: el, querySelectorAll: () => [], createElement: () => new Element() },
    sessionStorage: storage, readManifest, confirm: () => true,
    fetch: async (url, options) => {
      calls.push({ action: url.pathname.split('/').at(-1), options });
      return fetcher ? await fetcher(calls.at(-1)) : response([]);
    }
  });
  vm.runInContext(source, context, { filename: 'developer.js' });
  return { context, el, calls };
}

test('blocked session storage does not prevent loading or signing in', async () => {
  const env = environment({ storageDenied: true, fetch: async ({ action }) => response(action === 'Login' ? account() : []) });
  env.el('email').value = 'developer';
  env.el('password').value = 'password';
  await env.el('account-form').onsubmit({ preventDefault() {} });
  assert.equal(env.el('signed-in').hidden, false);
  assert.equal(env.el('identity').textContent, 'owner-a');
  assert.equal(env.el('account-message').textContent, '');
});

for (const stored of ['{invalid', JSON.stringify({ token: 'x' }), JSON.stringify({ token: 'x', expiresAt: 'invalid' }),
  JSON.stringify({ expiresAt: '2099-01-01' }), JSON.stringify({ token: 'x', expiresAt: '2000-01-01' })]) {
  test(`ignore invalid or expired stored session: ${stored}`, () => {
    const env = environment({ stored });
    assert.equal(env.el('signed-in').hidden, true);
    assert.equal(env.calls.length, 0);
  });
}

test('a Mine response arriving after logout cannot repopulate the account list', async () => {
  const mine = deferred();
  const env = environment({ stored: JSON.stringify(account()), fetch: ({ action }) => action === 'Mine' ? mine.promise : response({}) });
  await env.el('logout').onclick();
  mine.resolve(response([{ id: 'private', name: 'Previous account plugin' }]));
  await settle();
  assert.equal(env.el('my-plugins').textContent, '登录后查看');
  assert.equal(env.el('my-plugins').children.length, 0);
  assert.equal(env.calls.find(call => call.action === 'Logout').options.headers['X-Plugin-Session'], 'owner-a');
});

test('an old account response cannot overwrite the newly signed-in account', async () => {
  const old = deferred();
  const env = environment({ stored: JSON.stringify(account()), fetch: ({ options }) =>
    options.headers['X-Plugin-Session'] === 'owner-a' ? old.promise : response([{ id: 'b', name: 'Owner B' }]) });
  env.context.saveSession(account('owner-b'));
  await env.context.loadMine();
  old.resolve(response([{ id: 'a', name: 'Owner A' }]));
  await settle();
  assert.match(env.el('my-plugins').textContent, /Owner B/);
  assert.doesNotMatch(env.el('my-plugins').textContent, /Owner A/);
});

test('the latest refresh wins when responses arrive in reverse order', async () => {
  const old = deferred();
  let count = 0;
  const env = environment({ stored: JSON.stringify(account()), fetch: () => ++count === 1 ? old.promise : response([{ id: 'new', name: 'Latest' }]) });
  await env.context.loadMine();
  old.resolve(response([{ id: 'old', name: 'Stale' }]));
  await settle();
  assert.match(env.el('my-plugins').textContent, /Latest/);
  assert.doesNotMatch(env.el('my-plugins').textContent, /Stale/);
});

test('a stale refresh error does not replace successful current account feedback', async () => {
  const old = deferred();
  let count = 0;
  const env = environment({ stored: JSON.stringify(account()), fetch: () => ++count === 1 ? old.promise : response([]) });
  await env.context.loadMine();
  old.reject(new Error('stale request failure'));
  await settle();
  assert.equal(env.el('account-message').textContent, '');
});

test('a preview rendering failure never leaves the publish button armed', async () => {
  const env = environment({ stored: JSON.stringify(account()), readManifest: async () => ({ ...manifest, commands: [null] }) });
  await env.context.selectPackage(file);
  assert.equal(env.el('publish').disabled, true);
  assert.equal(env.el('preview').hidden, true);
  assert.equal(env.el('file-drop').classes.has('has-file'), false);
});

test('logout is blocked during publication, preserving the owner of the upload ticket', async () => {
  const ticket = deferred();
  const env = environment({ stored: JSON.stringify(account()), fetch: ({ action }) => action === 'UploadToken' ? ticket.promise : response([]) });
  await env.context.selectPackage(file);
  const publication = env.el('publish').onclick();
  assert.equal(env.el('logout').disabled, true);
  await env.el('logout').onclick();
  assert.equal(env.el('signed-in').hidden, false);
  assert.equal(env.calls.some(call => call.action === 'Logout'), false);
  ticket.reject(new Error('test upload failed'));
  await publication;
  assert.equal(env.el('logout').disabled, false);
});

test('failed server logout still ends the local session without an unhandled rejection', async () => {
  const env = environment({ stored: JSON.stringify(account()), fetch: ({ action }) => {
    if (action === 'Logout') throw new Error('network unavailable');
    return response([]);
  } });
  await env.el('logout').onclick();
  assert.equal(env.el('signed-in').hidden, true);
  assert.match(env.el('account-message').textContent, /本页面已退出/);
});

test('an earlier package selection cannot replace a newer preview', async () => {
  const old = deferred();
  let count = 0;
  const env = environment({ stored: JSON.stringify(account()), readManifest: () => ++count === 1 ? old.promise : Promise.resolve({ ...manifest, name: 'New selection' }) });
  const first = env.context.selectPackage(file);
  await env.context.selectPackage({ ...file, name: 'new.mexplug' });
  old.resolve({ ...manifest, name: 'Old selection' });
  await first;
  assert.equal(env.el('plugin-name').textContent, 'New selection');
  assert.equal(env.el('publish').disabled, false);
});
