const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const source = fs.readFileSync('app/static/app.js', 'utf8');
const fn = source.slice(source.indexOf('async function openViewer('), source.indexOf('const step ='));
const deferred = () => { let resolve, reject; const promise = new Promise((a,b) => { resolve=a; reject=b; }); return { promise, resolve, reject }; };
function setup() {
  const stage = { dataset: { id: '1' }, media: { id: 1 }, replaceChildren(m) { this.media = m; } };
  const els = { '#stage': stage, '#browse': { hidden: true } };
  const pending = new Map(), messages = [];
  const noop = () => {};
  const c = vm.createContext({
    viewerRequest: 0, S: { open: 1, selected: new Set() }, H: { stack: [], i: -1 },
    $: s => els[s] ||= { scrollTo: noop },
    stopAudio: noop, stopHover: noop, stopMedia: noop, record: noop,
    viewerUI: noop, applyZoom: noop, paintVol: noop, drawInspector: noop, setNav: noop,
    ctxIds: () => [1,2,3], esc: x => x, fmtSize: () => '', KIND: { image: { label: 'Images' } },
    api: async url => ({ id: +url.split('/').pop(), kind: 'image', name: 'test.png', size: 4 }),
    prepareStage: id => { const d = deferred(); pending.set(id, d); return d.promise; },
    startFeed: async () => {}, toast: x => messages.push(x),
  });
  vm.runInContext(fn, c);
  return { c, stage, pending, messages };
}
test('keeps old frame until new image is decoded', async () => {
  const { c, stage, pending } = setup();
  const result = c.openViewer(2); await new Promise(setImmediate);
  assert.equal(stage.media.id, 1);
  pending.get(2).resolve({ id: 2 }); await result;
  assert.equal(stage.media.id, 2); assert.equal(c.S.open, 2);
});
test('late image cannot replace a newer preview', async () => {
  const { c, stage, pending } = setup();
  const a = c.openViewer(2); await new Promise(setImmediate);
  const b = c.openViewer(3); await new Promise(setImmediate);
  pending.get(3).resolve({ id: 3 }); await b;
  let discarded = false;
  pending.get(2).resolve({ id: 2, removeAttribute() { discarded = true; } }); await a;
  assert.equal(stage.media.id, 3); assert.equal(c.S.open, 3); assert.ok(discarded);
});
test('decode failure keeps existing preview and reports the error', async () => {
  const { c, stage, pending, messages } = setup();
  const a = c.openViewer(2); await new Promise(setImmediate);
  pending.get(2).reject(new Error('missing image')); await a;
  assert.equal(stage.media.id, 1); assert.equal(c.S.open, 1); assert.match(messages[0], /missing image/);
});
test('leaving the viewer cancels an in-flight preview', async () => {
  const { c, stage, pending } = setup();
  const a = c.openViewer(2); await new Promise(setImmediate); c.viewerRequest++;
  pending.get(2).resolve({ id: 2, removeAttribute() {} }); await a;
  assert.equal(stage.media.id, 1);
});
test('Back keeps the viewer visible until its gallery is ready', async () => {
  const history = source.slice(source.indexOf('async function applyState('), source.indexOf('async function histMove('));
  const ready = deferred(), browse = { hidden: true }, viewer = { hidden: false }, scroller = { scrollTop: 0 };
  const els = { '#browse': browse, '#viewer': viewer, '#scroller': scroller, '#q': { value: '' } };
  const c = vm.createContext({
    H: { busy: false }, S: { selected: new Set(), animNext: true }, $: s => els[s],
    stopAudio() {}, setNav() {}, drawFilterStatus() {}, navState() {}, setTitle() {}, updateBar() {},
    viewerUI(on) { browse.hidden = on; viewer.hidden = !on; },
    loadFiles: () => ready.promise, runSearch() {}, openViewer() {},
  });
  vm.runInContext(history, c);
  const back = c.applyState({ view: 'all', kind: 'all', filters: {}, sort: 'mtime', scroll: 120 });
  await new Promise(setImmediate);
  assert.equal(viewer.hidden, false);
  assert.equal(browse.hidden, true);
  ready.resolve(); await back;
  assert.equal(viewer.hidden, true);
  assert.equal(browse.hidden, false);
  assert.equal(scroller.scrollTop, 120);
});
