const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const source = fs.readFileSync('app/static/drag.js', 'utf8');
async function setup(native = true) {
  const handlers = {}, calls = [], toast = [], classes = { toggle() {}, add() {}, remove() {} };
  class Element {
    constructor(matches = {}, tagName = 'DIV') { this.matches = matches; this.tagName = tagName; }
    closest(s) { return this.matches[s] || null; }
    getBoundingClientRect() { return { bottom: 500 }; }
  }
  const on = (n, f) => (handlers[n] ||= []).push(f);
  const button = { addEventListener: on };
  let finish;
  const c = vm.createContext({ Element, Math, location: { origin: 'http://localhost' },
    document: { addEventListener: on, documentElement: { classList: classes }, body: { classList: classes } },
    window: { addEventListener: on, pywebview: { api: {
      drag_capabilities: async () => ({ native_files: native }),
      drag_files: ids => { calls.push(Array.from(ids)); return new Promise(r => finish = r); }
    } } },
    S: { selected: new Set(), files: [{ id: 7, name: 'original.png', kind: 'image', ext: '.png' }], cur: { id: 9, name: 'movie.mp4', kind: 'video', ext: '.mp4' } }, F: { files: [] },
    $: s => s === '#stage' ? { dataset: { id: '9' } } : button,
    stopHover() {}, toast: m => toast.push(m),
  });
  vm.runInContext(source, c); await new Promise(setImmediate);
  const event = (target, extra = {}) => ({ target, button: 0, pointerType: 'mouse', buttons: 1,
    clientX: 10, clientY: 10, prevented: false, stopped: false,
    preventDefault() { this.prevented = true; }, stopImmediatePropagation() { this.stopped = true; }, ...extra });
  const fire = (name, e) => { for (const f of handlers[name] || []) { f(e); if (e.stopped) break; } return e; };
  const tile = new Element(); tile.dataset = { id: '7' }; tile.matches['.tile[data-id]'] = tile;
  return { c, calls, Element, event, fire, tile, finish: () => finish?.() };
}
test('click or small movement does not start a file drag', async () => {
  const t = await setup(); t.fire('pointerdown', t.event(t.tile));
  t.fire('pointermove', t.event(t.tile, { clientX: 13 })); t.fire('pointerup', t.event(t.tile));
  assert.equal(t.calls.length, 0);
});
test('drags selected originals once and suppresses the trailing open click', async () => {
  const t = await setup(); t.c.S.selected = new Set([7,8]);
  t.fire('pointerdown', t.event(t.tile)); t.fire('pointermove', t.event(t.tile, { clientX: 20 }));
  t.fire('pointermove', t.event(t.tile, { clientX: 30 })); assert.deepEqual(t.calls, [[7,8]]);
  t.finish(); await new Promise(setImmediate);
  assert.ok(t.fire('click', t.event(t.tile)).stopped);
});
test('viewer handle exports current file without the grid selection', async () => {
  const t = await setup(); t.c.S.selected = new Set([7,8]);
  const h = new t.Element({ '#dragFile': {} });
  t.fire('pointerdown', t.event(h)); t.fire('pointermove', t.event(h, { clientX: 20 }));
  assert.deepEqual(t.calls, [[9]]); t.finish();
});
test('video controls remain interactive and the video picture is draggable', async () => {
  const t = await setup(); const video = new t.Element({}, 'VIDEO');
  video.matches['#stage img, #stage video'] = video;
  t.fire('pointerdown', t.event(video, { clientY: 480 }));
  t.fire('pointermove', t.event(video, { clientX: 30, clientY: 480 })); assert.equal(t.calls.length, 0);
  t.fire('pointerdown', t.event(video, { clientY: 150 }));
  t.fire('pointermove', t.event(video, { clientX: 30, clientY: 150 })); assert.deepEqual(t.calls, [[9]]); t.finish();
});
test('native video drag cancels Chromium media-frame dragging', async () => {
  const t = await setup(), video = new t.Element({}, 'VIDEO');
  video.matches['#stage img, #stage video'] = video;
  const drag = t.fire('dragstart', t.event(video, { clientY: 150 }));
  assert.equal(drag.prevented, true);
  assert.equal(t.calls.length, 0);
});
test('released button cancels a pending gesture', async () => {
  const t = await setup(); t.fire('pointerdown', t.event(t.tile));
  t.fire('pointermove', t.event(t.tile, { buttons: 0, clientX: 30 })); assert.equal(t.calls.length, 0);
});
test('browser fallback exports original endpoint rather than thumbnail', async () => {
  const t = await setup(false), payload = {};
  const dataTransfer = { clearData: () => { for (const key of Object.keys(payload)) delete payload[key]; }, setData: (k,v) => payload[k] = v };
  t.fire('dragstart', t.event(t.tile, { dataTransfer }));
  assert.equal(payload.DownloadURL, 'image/png:original.png:http://localhost/api/file/7');
  assert.equal(dataTransfer.effectAllowed, 'copy'); assert.equal(t.calls.length, 0);
});
test('browser video fallback carries an MP4 URL instead of a still image', async () => {
  const t = await setup(false), payload = {};
  const h = new t.Element({ '#dragFile': {} });
  t.fire('dragstart', t.event(h, { dataTransfer: { clearData() {}, setData: (k,v) => payload[k] = v } }));
  assert.equal(payload.DownloadURL, 'video/mp4:movie.mp4:http://localhost/api/file/9');
});
