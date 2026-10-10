const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const api = async (url, opts = {}) => {
  if (opts.body && !(opts.body instanceof FormData)) { opts.headers = { 'Content-Type': 'application/json' }; opts.body = JSON.stringify(opts.body); }
  const r = await fetch('/api' + url, opts);
  if (!r.ok) throw new Error((await r.json().catch(() => ({}))).detail || r.statusText);
  return r.json();
};
const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const fmtSize = n => n > 1e9 ? (n / 1e9).toFixed(1) + ' GB' : n > 1e6 ? (n / 1e6).toFixed(1) + ' MB' : Math.max(1, Math.round(n / 1e3)) + ' KB';
const ago = t => { const d = (Date.now() / 1000 - t) / 86400;
  return d < 1 ? 'Today' : d < 2 ? 'Yesterday' : d < 14 ? Math.floor(d) + ' days ago' : d < 60 ? Math.floor(d / 7) + ' weeks ago' : d < 700 ? Math.floor(d / 30) + ' months ago' : Math.floor(d / 365) + ' years ago'; };
const store = { get: (k, d) => { try { return localStorage.getItem(k) ?? d; } catch { return d; } }, set: (k, v) => { try { localStorage.setItem(k, v); } catch { } } };

/* ================================================================ icons (Lucide) */
const IC = { library: 'layout-grid', inbox: 'inbox', pencil: 'pencil-line', plus: 'plus', x: 'x', search: 'search', sparkle: 'sparkles', image: 'image', video: 'clapperboard', music: 'audio-lines',
  folder: 'folder', folderplus: 'folder-plus', explorer: 'folder-open', copy: 'copy', open: 'external-link', play: 'play', pause: 'pause', send: 'arrow-up', panel: 'panel-left', sliders: 'sliders-horizontal',
  columns: 'columns-3', rows: 'rows-3', lock: 'lock', loader: 'loader-circle', eye: 'eye', move: 'folder-input', history: 'history', commit: 'git-commit-horizontal', left: 'chevron-left', right: 'chevron-right',
  check: 'check', palette: 'palette', pipette: 'pipette', tag: 'tag', type: 'type', vol: 'volume-2', vol1: 'volume-1', mute: 'volume-x', expand: 'maximize-2', scan: 'scan-search', wand: 'wand-sparkles',
  layers: 'layers', hash: 'hash', star: 'star', dots: 'ellipsis', link: 'link', shapes: 'shapes', images: 'images', sort: 'arrow-up-down', minus: 'minus',
  quote: 'quote', calendar: 'calendar', user: 'user', down: 'arrow-down', mood: 'moon-star', redo: 'rotate-cw' };
const ic = (n, extra = '', sw = 1.75) => `<svg class="ic" width="18" height="18" viewBox="0 0 24 24" fill="${n === 'play' || n === 'pause' ? 'currentColor' : 'none'}" stroke="currentColor" stroke-width="${sw}" stroke-linecap="round" stroke-linejoin="round" ${extra}>${LUCIDE[IC[n] || n] || ''}</svg>`;
const hydrate = (root = document) => root.querySelectorAll('i[data-ic]').forEach(i => { if (!i.firstChild) i.innerHTML = ic(i.dataset.ic); });
const KIND = { image: { icon: 'image', color: '#4a8be0', label: 'Images' }, video: { icon: 'video', color: '#8e5bd0', label: 'Videos' }, audio: { icon: 'music', color: '#e0629f', label: 'Audio' } };
const kicon = (k, size) => `<i data-ic="${KIND[k]?.icon || 'image'}" style="color:${KIND[k]?.color || '#888'};${size ? `width:${size}px;height:${size}px` : ''}"></i>`;
const HUES = ['#d9534f', '#e8803a', '#8e5bd0', '#d9a21f', '#4a8be0', '#46a876', '#e0629f'];
const glyph = id => {
  const c = HUES[id % HUES.length], k = id % 7;
  const g = [`<path d="M12 3.5c3.4 4.1 5.6 6.8 5.6 9.7a5.6 5.6 0 0 1-11.2 0c0-2.9 2.2-5.6 5.6-9.7z" fill="${c}"/>`, `<g stroke="${c}" stroke-width="2.4" stroke-linecap="round"><path d="M12 4v16M5 8l14 8M19 8L5 16"/></g>`,
    `<path d="M12 3.6l7 8.4-7 8.4L5 12z" fill="${c}"/>`, `<path d="M4 8a2 2 0 0 1 2-2h3.6l2 2.2H18a2 2 0 0 1 2 2V17a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2z" fill="${c}"/>`,
    `<path d="M6.5 4h9.5a2 2 0 0 1 2 2v12.5H8a2 2 0 0 1-1.5-3.3z" fill="${c}"/>`, `<path d="M8 4h8l4.5 5.2L12 20 3.5 9.2z" fill="${c}"/>`,
    `<g fill="${c}"><circle cx="9" cy="9" r="3.3"/><circle cx="15.5" cy="10" r="3.3"/><circle cx="11.5" cy="15.5" r="3.3"/></g>`][k];
  return `<svg class="glyph" viewBox="0 0 24 24">${g}</svg>`;
};

/* ================================================================ state */
/* user settings (Customize) — kept in this browser only */
const PDEF = { tile: 300, gap: 12, rad: 10, names: false, hover: true, accent: '#ff5a1f', layout: 'cols', theme: 'blush', tstyle: 'lift', head: 'serif', ideas: true };
const THEMES = [['blush', 'Blush', '#f5f6f9', 'linear-gradient(180deg,#f1e8ea,#e8e2ea)'], ['paper', 'Paper', '#fbfaf8', '#f3f1ec'], ['mist', 'Mist', '#f6f8fb', 'linear-gradient(180deg,#e8eef7,#e1e8f3)'],
  ['sage', 'Sage', '#f7f8f4', 'linear-gradient(180deg,#e8eee2,#dfe8da)'], ['sand', 'Sand', '#faf7f1', 'linear-gradient(180deg,#f3eadc,#ece0cc)'], ['lilac', 'Lilac', '#f8f7fc', 'linear-gradient(180deg,#eeeaf8,#e5dff3)'],
  ['peach', 'Peach', '#fdf8f5', 'linear-gradient(180deg,#fbe8dd,#f5dfe0)'], ['studio', 'Studio', '#ffffff', '#f5f5f6'],
  ['night', 'Night', '#16171d', '#111217'], ['ink', 'Ink', '#0b0b0d', '#000000']];
const P = (() => { try { return { ...PDEF, ...JSON.parse(store.get('prefs', '{}')) }; } catch { return { ...PDEF }; } })();
function applyPrefs() {
  const d = document.documentElement, r = d.style; r.setProperty('--gap', P.gap + 'px'); r.setProperty('--rad', P.rad + 'px'); r.setProperty('--accent', P.accent);
  if (P.theme === 'blush') d.removeAttribute('data-theme'); else d.dataset.theme = P.theme;
  const b = document.body.classList; b.toggle('names', P.names); b.toggle('head-sans', P.head === 'sans'); ['lift', 'soft', 'outline', 'flat'].forEach(t => b.toggle('t-' + t, P.tstyle === t));
  const th = THEMES.find(t => t[0] === P.theme); if (th) $('meta[name=theme-color]').content = th[2];
  store.set('prefs', JSON.stringify(P));
}
applyPrefs();
const S = { view: 'all', project: null, kind: 'all', root: null, color: null, near: null, source: null, pattern: null, sort: 'mtime', layout: P.layout,
  files: [], total: 0, selected: new Set(), searching: false, search: null, projects: [], roots: [], sources: [], colors: [], moods: [], stats: { kinds: {} }, known: [], ideas: [], ideaAt: 0, related: [],
  open: null, cur: null, ctx: null, zoom: 1, vol: +store.get('vol', 60), muted: store.get('muted', '0') === '1', animNext: true, srcAll: false, loading: false };
const EXAMPLES = ['a person standing at night', 'product on a green background', 'aerial view of a city', 'someone talking about pricing'];

const toast = (msg, icon = 'check', bad = false) => {
  const t = $('#toast'); t.innerHTML = `${ic(icon)}<span>${esc(msg)}</span>`; t.hidden = false; t.firstChild.style.color = bad ? '#ff8f8f' : '#7fe0a0';
  clearTimeout(toast.t); toast.t = setTimeout(() => t.hidden = true, 2600);
};
/* name the user's file manager and path style for this OS */
const OS = /Mac/.test(navigator.platform) ? 'mac' : /Win/.test(navigator.platform) ? 'win' : 'linux';
const FM = { win: 'File Explorer', mac: 'Finder', linux: 'folder' }[OS], FMS = { win: 'Explorer', mac: 'Finder', linux: 'folder' }[OS];
const EXPATH = { win: 'C:\\Users\\you\\Downloads', mac: '/Users/you/Downloads', linux: '/home/you/Downloads' }[OS];
const reveal = async id => { try { const r = await api(`/reveal/${id}`, { method: 'POST' }); toast(`Shown in ${r.where || FM}`, 'explorer'); } catch (e) { toast(e.message, 'x', true); } };
const copyText = async (text, msg = 'Path copied') => { try { await navigator.clipboard.writeText(text); toast(msg, 'copy'); } catch { toast('Could not copy', 'x', true); } };

/* ================================================================ history: one stack, every view */
const H = { stack: [], i: -1, busy: false };
const snapshot = () => ({ view: S.view, project: S.project, kind: S.kind, root: S.root, color: S.color, near: S.near, source: S.source, pattern: S.pattern, sort: S.sort,
  search: S.searching ? S.search : null, open: S.open, ctx: S.ctx, scroll: 0 });
function record(replace = false) {
  if (H.busy) return; const cur = H.stack[H.i];
  if (cur) cur.scroll = !$('#browse').hidden ? $('#scroller').scrollTop : $('#viewer').scrollTop;
  const s = snapshot();
  if (replace && cur) H.stack[H.i] = s; else { H.stack = H.stack.slice(0, H.i + 1); H.stack.push(s); H.i = H.stack.length - 1; }
  navState();
}
function navState() { $('#navBack').disabled = H.i <= 0; $('#navFwd').disabled = H.i >= H.stack.length - 1; }
async function applyState(s) {
  H.busy = true; stopAudio();
  Object.assign(S, { view: s.view, project: s.project, kind: s.kind, root: s.root, color: s.color, near: s.near, source: s.source, pattern: s.pattern, sort: s.sort, searching: !!s.search, search: s.search });
  S.selected.clear(); S.animNext = true; setNav();
  $('#q').value = '';
  try {
    if (s.open) { S.ctx = s.ctx; await openViewer(s.open, { rec: 'none', scroll: s.scroll }); }
    else {
      const home = grabStage(); if (home) S.animNext = false;
      viewerUI(false);
      if (s.search?.body) await runSearch(s.search.body, s.search.label, { rec: 'none', tags: s.search.tags }); else { S.searching = false; S.search = null; await loadFiles(true); }
      $('#scroller').scrollTop = s.scroll || 0;
      if (home) morphOut(home);
    }
  } finally { H.busy = false; navState(); }
}
async function histMove(d) { const n = H.i + d; if (n < 0 || n >= H.stack.length) return; const cur = H.stack[H.i]; if (cur) cur.scroll = !$('#browse').hidden ? $('#scroller').scrollTop : $('#viewer').scrollTop; H.i = n; await applyState(H.stack[n]); }
const goBack = () => { if (H.i > 0) histMove(-1); else if (S.open) { const home = grabStage(); viewerUI(false); setTitle(); updateBar(); if (home) morphOut(home); } };
$('#navBack').onclick = () => histMove(-1); $('#navFwd').onclick = () => histMove(1);
document.addEventListener('mouseup', e => { if (e.button === 3) { e.preventDefault(); histMove(-1); } if (e.button === 4) { e.preventDefault(); histMove(1); } });

/* ================================================================ adding folders */
function addBoxHTML() {
  const colr = { Downloads: '#e8803a', Pictures: '#4a8be0', Videos: '#8e5bd0', Documents: '#d9a21f', Desktop: '#46a876', Music: '#e0629f' };
  const icn = { Downloads: 'inbox', Pictures: 'image', Videos: 'video', Documents: 'folder', Desktop: 'panel', Music: 'music' };
  return `<div class="addbox"><h4><i data-ic="folderplus"></i>Which folder should I read?</h4>
    <div class="pathrow"><div class="inp"><i data-ic="folder"></i><input class="apath" placeholder="Paste a folder path, e.g. ${EXPATH}" spellcheck="false"></div>
      <button class="btn" data-browse><i data-ic="search"></i><span>Browse…</span></button><button class="btn dark" data-go>Add &amp; read</button></div>
    <div class="quick"><small>or pick one:</small>${S.known.map(k => `<button data-path="${esc(k.path)}"><i data-ic="${icn[k.label] || 'folder'}" style="color:${colr[k.label] || '#888'}"></i>${esc(k.label)}</button>`).join('')}</div></div>`;
}
function bindAddBox(root) {
  const inp = $('.apath', root);
  $('[data-go]', root).onclick = () => inp.value.trim() ? addRoot(inp.value.trim()) : inp.focus();
  inp.onkeydown = e => { if (e.key === 'Enter' && inp.value.trim()) addRoot(inp.value.trim()); };
  $('[data-browse]', root).onclick = async e => {
    const b = e.currentTarget, lab = $('span', b); b.disabled = true; lab.textContent = 'Waiting…';
    try { const nat = window.pywebview?.api?.pick_folder;   // desktop app: the window's own dialog
      const r = nat ? { path: await nat() } : await api('/pick_folder', { method: 'POST' }); if (r.path) { inp.value = r.path; addRoot(r.path); } } catch (err) { toast(err.message, 'x', true); }
    b.disabled = false; lab.textContent = 'Browse…';
  };
  $$('[data-path]', root).forEach(b => b.onclick = () => addRoot(b.dataset.path));
  hydrate(root); setTimeout(() => inp.focus(), 60);
}
function addFolderModal() {
  modal(`<div class="mh"><h3>Add a folder</h3><p>File Mind looks at every image and video inside it and listens to every audio file, then learns what each one is about. Zips, installers and other non-media files are ignored. Your files are only read, never changed.</p></div><div class="mb">${addBoxHTML()}</div>`);
  bindAddBox($('#modalBox'));
}
async function addRoot(path) {
  try { await api('/roots', { method: 'POST', body: { path: path.replace(/^"|"$/g, '') } }); } catch (err) { return toast(err.message, 'x', true); }
  closeModal(); await refreshAll(); poll();
}
['#addTop', '#addSide', '#addRow', '#addToolbar'].forEach(s => $(s).onclick = addFolderModal);

/* ================================================================ sidebar: folders, types, colors, sources, spaces */
async function loadRoots() {
  S.roots = await api('/roots');
  $('#roots').innerHTML = S.roots.map(r => {
    const nm = r.path.split(/[\\/]/).filter(Boolean).pop() || r.path;
    return `<div class="row ${S.root === r.id ? 'active' : ''}" data-root="${r.id}" title="${esc(r.path)}"><i data-ic="folder" style="color:#e8803a"></i><span>${esc(nm)}</span><em>${r.files.toLocaleString()}</em>
      <button class="mini" data-reindex="${r.id}" title="Look for new files"><i data-ic="loader"></i></button><button class="mini" data-forget="${r.id}" title="Forget this folder (never deletes files)"><i data-ic="x"></i></button></div>`;
  }).join('') || '<div class="hintrow">No folder yet.</div>';
  hydrate($('#roots'));
}
$('#roots').onclick = async e => {
  const b = e.target.closest('button');
  if (b) {
    if (b.dataset.reindex) { await api(`/roots/${b.dataset.reindex}/index`, { method: 'POST' }); poll(); }
    if (b.dataset.forget && confirm('Remove this folder from File Mind?\nYour files on disk are NOT touched.')) { await api(`/roots/${b.dataset.forget}`, { method: 'DELETE' }); if (S.root == b.dataset.forget) S.root = null; await refreshAll(); }
    return;
  }
  const r = e.target.closest('[data-root]'); if (r) go({ root: S.root === +r.dataset.root ? null : +r.dataset.root }, { keepFilters: true });
};
function drawTypes() {
  const k = S.stats.kinds || {};
  $('#types').innerHTML = Object.keys(KIND).filter(x => k[x]).map(x => `<button class="row ${S.kind === x && S.view === 'all' ? 'active' : ''}" data-kind="${x}">${kicon(x)}<span>${KIND[x].label}</span><em>${k[x].toLocaleString()}</em></button>`).join('') || '<div class="hintrow">Appears once files are read.</div>';
  hydrate($('#types'));
}
$('#types').onclick = e => { const b = e.target.closest('[data-kind]'); if (!b) return; const k = S.kind === b.dataset.kind ? 'all' : b.dataset.kind; go({ kind: k }, { keepFilters: true }); };
function drawColors() {
  const hasNear = S.near ? `<button class="sw on" style="background:${S.near}" title="Closest to ${S.near}" data-near="${S.near}"></button>` : '';
  $('#colors').innerHTML = S.colors.map(c => `<button class="sw ${S.color === c.key ? 'on' : ''} ${c.count ? '' : 'off'}" style="background:${c.swatch}" title="${c.label} — ${c.count.toLocaleString()} files where it covers a big part of the picture" data-color="${c.key}"></button>`).join('')
    + hasNear + `<label class="sw pick" title="Pick any color — find the closest matches"><i data-ic="pipette"></i><input type="color" id="colorPick" value="${S.near || '#ff5a1f'}" hidden></label>`;
  hydrate($('#colors')); $('#rainbow').classList.toggle('on', S.sort === 'color');
  $('#colorPick').onchange = e => go({ near: e.target.value }, { keepFilters: true });
  drawMoods();
}
/* moods are not pixel colours: they are searched by meaning, with the embedding model */
const MOOD_TINT = { neon: 'linear-gradient(135deg,#ff2bd6,#2b6bff)', pastel: 'linear-gradient(135deg,#ffd1dc,#c9e7ff)', moody: 'linear-gradient(135deg,#1c1f2b,#4a3b52)',
  golden: 'linear-gradient(135deg,#ffb347,#ff7a3d)', bw: 'linear-gradient(135deg,#111 50%,#eee 50%)', vivid: 'conic-gradient(#ff3b30,#ffcc00,#34c759,#007aff,#af52de,#ff3b30)',
  minimal: 'linear-gradient(135deg,#fff,#e9e7e2)', earthy: 'linear-gradient(135deg,#8a6a43,#a6a36b)', teal_orange: 'linear-gradient(135deg,#108a8a 50%,#ff8a3d 50%)', dreamy: 'linear-gradient(135deg,#cdb8ff,#ffe0f0)' };
function drawMoods() {
  const on = new Set(curTags().map(t => t.mood).filter(Boolean));
  $('#moods').innerHTML = S.moods.map(m => `<button class="${on.has(m.key) ? 'on' : ''}" data-mood="${m.key}" style="background:${MOOD_TINT[m.key] || '#ddd'}" title="${esc(m.label)} — a mood, found by meaning. Adds to your search."></button>`).join('');
}
$('#moods').onclick = e => { const b = e.target.closest('[data-mood]'); if (!b) return; const m = S.moods.find(x => x.key === b.dataset.mood);
  const tags = curTags(), i = tags.findIndex(t => t.mood === m.key);
  if (i >= 0) { const n = tags.slice(); n.splice(i, 1); searchTags(n); } else addTag({ q: m.query, label: m.label, mood: m.key }); };
$('#colors').onclick = e => { const b = e.target.closest('[data-color]'); if (b) go({ color: S.color === b.dataset.color ? null : b.dataset.color }, { keepFilters: true, keepKind: true }); };
$('#rainbow').onclick = () => { S.sort = S.sort === 'color' ? 'mtime' : 'color'; drawColors(); if (!S.searching) { S.animNext = true; loadFiles(true); } };
function drawSources() {
  const list = S.srcAll ? S.sources : S.sources.slice(0, 6);
  $('#sources').innerHTML = list.map(s => `<button class="row ${S.source === s.key ? 'active' : ''}" data-source="${s.key}"><span class="dot" style="background:${s.color}"></span><span>${esc(s.label)}</span><em>${s.count.toLocaleString()}</em></button>`).join('')
    + (S.sources.length > 6 ? `<button class="row" id="srcMore"><i data-ic="${S.srcAll ? 'left' : 'dots'}" style="color:var(--ink3)"></i><span style="color:var(--ink3)">${S.srcAll ? 'Show fewer' : `${S.sources.length - 6} more sources`}</span></button>` : '')
    || '<div class="hintrow">Detected from file names — Midjourney, ChatGPT, Higgsfield…</div>';
  hydrate($('#sources'));
}
$('#sources').onclick = e => { if (e.target.closest('#srcMore')) { S.srcAll = !S.srcAll; return drawSources(); } const b = e.target.closest('[data-source]'); if (b) go({ source: S.source === b.dataset.source ? null : b.dataset.source }, { keepFilters: true, keepKind: true }); };
let polling = false;
async function poll() {
  if (polling) return; polling = true; let last = 0;
  while (true) {
    const s = await api('/status'), b = $('#banner');
    const busy = s.running && ['loading', 'embedding'].includes(s.phase);      // a quick re-scan stays silent
    if (busy) {
      const pct = s.total ? Math.round(100 * s.done / s.total) : 0, loading = ['scanning', 'loading'].includes(s.phase);
      b.hidden = false;
      b.innerHTML = `<div class="spin"><i data-ic="loader"></i></div><div class="tx"><div><b>${loading ? esc(s.message) : 'Reading your files'}</b>${loading ? '' : ` — ${s.done.toLocaleString()} of ${s.total.toLocaleString()}`}${s.errors ? ` · ${s.errors} unreadable skipped` : ''}</div>
        <div class="track"><div id="barFill" style="width:${loading ? 5 : pct}%"></div></div><div class="sm">${s.current ? esc(s.current) : 'This happens on your PC. You can already search what has been read.'}</div></div><button id="stopBtn">Pause</button>`;
      hydrate(b); $('#stopBtn').onclick = () => api('/index/stop', { method: 'POST' });
    } else b.hidden = true;
    if (s.running && Date.now() - last > 6000) { last = Date.now(); loadRoots(); loadStats(); if (!S.searching && !S.open) loadFiles(true, true); }
    if (!s.running) break;
    await new Promise(r => setTimeout(r, 1000));
  }
  polling = false; $('#banner').hidden = true; await loadRoots(); await loadStats(); if (!S.searching && !S.open) await loadFiles(true, true);
}
async function loadStats() {
  const [s, src, col] = await Promise.all([api('/stats'), api('/sources'), api('/colors')]); S.stats = s; S.sources = src.sources; S.colors = col;
  if (!S.moods.length) S.moods = await api('/moods').catch(() => []);
  if (!S.ideas.length && s.total) loadIdeas();
  $('#cAll').textContent = s.total ? s.total.toLocaleString() : ''; $('#cUn').textContent = s.total ? s.unassigned.toLocaleString() : ''; $('#cRen').textContent = s.renames || '';
  $('#dot').hidden = !(s.renames || S.projects.some(p => p.count)); drawTypes(); drawColors(); drawSources();
}
async function loadProjects() {
  S.projects = await api('/projects');
  $('#projects').innerHTML = S.projects.map(p => `<button class="row proj ${S.view === 'project' && S.project === p.id ? 'active' : ''}" data-id="${p.id}">${glyph(p.id)}<span>${esc(p.name)}</span><em>${p.count}</em></button>`).join('')
    || '<div class="hintrow">Spaces are virtual folders. Press + to make one.</div>';
  $('#projSelect').innerHTML = S.projects.map(p => `<option value="${p.id}">${esc(p.name)}</option>`).join('');
  $('#cSp').textContent = S.projects.length || '';
}
$('#projects').onclick = e => { const b = e.target.closest('.proj'); if (b) go({ view: 'project', project: +b.dataset.id }); };
$('#projects').oncontextmenu = async e => {
  const b = e.target.closest('.proj'); if (!b) return; e.preventDefault();
  const p = S.projects.find(x => x.id == b.dataset.id);
  const name = prompt('Rename this space.\nLeave it empty to delete the space (your files stay untouched).', p.name); if (name === null) return;
  if (!name.trim()) { await api(`/projects/${p.id}`, { method: 'DELETE' }); if (S.project === p.id) { S.project = null; S.view = 'all'; } } else await api(`/projects/${p.id}`, { method: 'PATCH', body: { name } });
  await refreshAll();
};
$('#newProject').onclick = async () => { const name = prompt('Name your new space:'); if (!name) return; await api('/projects', { method: 'POST', body: { name } }); await loadProjects(); };
$$('.row[data-view]').forEach(b => b.onclick = () => go({ view: b.dataset.view }));
$$('.label[data-sec]').forEach(l => {
  const sec = l.dataset.sec, kids = () => { const out = []; let n = l.nextElementSibling; while (n && !n.classList.contains('label') && !n.classList.contains('grow')) { out.push(n); n = n.nextElementSibling; } return out; };
  const apply = c => { l.classList.toggle('col', c); kids().forEach(k => k.hidden = c); };
  if (store.get('sec_' + sec, '0') === '1') apply(true);
  l.onclick = e => { if (e.target.closest('button')) return; const c = !l.classList.contains('col'); store.set('sec_' + sec, c ? '1' : '0'); apply(c); };
});
$('#toggleSide').onclick = () => { document.body.classList.add('collapsed'); $('#showSide').hidden = false; };
$('#showSide').onclick = () => { document.body.classList.remove('collapsed'); $('#showSide').hidden = true; };

/* ---------------- navigation entry point */
function go(o = {}, opt = {}) {
  viewerUI(false);
  const keep = opt.keepFilters ? { color: S.color, near: S.near, source: S.source, pattern: S.pattern, view: S.view === 'project' ? 'all' : S.view } : {};
  const base = { view: 'all', project: null, kind: opt.keepKind ? S.kind : 'all', color: null, near: null, source: null, pattern: null, ...keep };
  if (o.color || o.near || o.source || o.pattern) { base.color = base.near = base.source = base.pattern = null; }       // one colour/source filter at a time
  const srch = opt.keepFilters && S.searching && S.search?.tags ? S.search : null;
  Object.assign(S, base, o, { searching: !!srch, search: srch });
  if (S.near) S.color = null; if (S.color) S.near = null;
  S.selected.clear(); S.animNext = true; setNav(); record();
  if (srch) runSearch(srch.body, srch.label, { rec: 'none', tags: srch.tags }); else loadFiles(true);
}
function setNav() {
  $$('.row[data-view]').forEach(b => b.classList.toggle('active', S.view === b.dataset.view && (S.view !== 'all' || (S.kind === 'all' && !S.color && !S.near && !S.source && !S.pattern && !S.searching))));
  $$('.proj').forEach(b => b.classList.toggle('active', S.view === 'project' && +b.dataset.id === S.project));
  $$('#types .row').forEach(b => b.classList.toggle('active', S.kind === b.dataset.kind));
  $$('#roots .row').forEach(b => b.classList.toggle('active', S.root === +b.dataset.root));
  $$('#sources .row[data-source]').forEach(b => b.classList.toggle('active', S.source === b.dataset.source));
  drawColors();
}
function filterLabel() {
  if (S.color) { const c = S.colors.find(x => x.key === S.color); return { text: c?.label || S.color, dot: c?.swatch, clear: 'color' }; }
  if (S.near) return { text: 'Closest to ' + S.near, dot: S.near, clear: 'near' };
  if (S.source) { const s = S.sources.find(x => x.key === S.source); return { text: s?.label || S.source, dot: s?.color, clear: 'source' }; }
  if (S.pattern) return { text: 'Name: ' + S.pattern, icon: 'type', clear: 'pattern' };
  return null;
}
function setTitle() {
  if (S.open) return;
  const pr = S.projects.find(p => p.id === S.project), fl = filterLabel(); let g = `<i data-ic="library" style="color:var(--ink2)"></i>`, t = 'Library';
  if (S.searching) { g = `<i data-ic="search" style="color:var(--ink2)"></i>`; t = S.search?.label || 'Search'; }
  else if (S.view === 'project') { g = pr ? glyph(pr.id) : ''; t = pr?.name || ''; }
  else if (S.view === 'unassigned') { g = `<i data-ic="inbox" style="color:var(--ink2)"></i>`; t = 'Unsorted'; }
  else if (S.view === 'renames') { g = `<i data-ic="pencil" style="color:var(--ink2)"></i>`; t = 'Pending renames'; }
  else if (S.view === 'spaces') { g = `<i data-ic="layers" style="color:var(--ink2)"></i>`; t = 'All spaces'; }
  else if (fl) { g = fl.dot ? `<span class="dotc" style="background:${fl.dot}"></span>` : `<i data-ic="${fl.icon}" style="color:var(--ink2)"></i>`; t = fl.text + (S.kind !== 'all' ? ' · ' + KIND[S.kind].label : ''); }
  else if (S.kind !== 'all') { g = kicon(S.kind); t = KIND[S.kind].label; }
  $('#title').className = 'title'; $('#title').innerHTML = `${g}<span>${esc(t)}</span><small>${(S.searching ? S.files.length : S.total).toLocaleString()}</small>`; hydrate($('#title'));
}

/* ================================================================ grid */
function filterParams(extra = {}) {
  const p = new URLSearchParams({ sort: S.sort, ...extra });
  if (S.root) p.set('root', S.root); if (S.kind !== 'all') p.set('kind', S.kind);
  if (S.view === 'project') p.set('project', S.project); if (S.view === 'unassigned') p.set('unassigned', 'true');
  if (S.color) p.set('color', S.color); if (S.near) p.set('near', S.near); if (S.source) p.set('source', S.source); if (S.pattern) p.set('pattern', S.pattern);
  return p;
}
async function loadFiles(reset = true, keepScroll = false) {
  if (S.searching || (!reset && S.loading)) return;
  if (S.view === 'spaces') { S.files = []; S.total = S.projects.length; $('#more').hidden = true; return render(); }
  S.loading = true;
  try {
    const r = await api('/files?' + filterParams({ limit: reset ? 180 : 120, offset: reset ? 0 : S.files.length })); const sc = $('#scroller').scrollTop;
    let files = r.files; if (S.view === 'renames') files = files.filter(f => f.proposed);
    S.files = reset ? files : S.files.concat(files); S.total = S.view === 'renames' ? files.length : r.total;
    $('#more').hidden = S.files.length >= r.total || S.view === 'renames';
    if (!reset) { S.animNext = false; appendGrid(files); } else { render(); if (keepScroll) $('#scroller').scrollTop = sc; }
  } finally { S.loading = false; }
}
// endless scroll: load the next page a screen before the end
new IntersectionObserver(es => { if (es[0].isIntersecting && !$('#more').hidden && !S.searching && !S.open) loadFiles(false); }, { root: $('#scroller'), rootMargin: '900px' }).observe($('#more .sentinel'));
const aspect = f => f.tw && f.th ? clamp(f.tw / f.th, .55, 2.4) : f.kind === 'audio' ? 1.3 : 1.35;
const wave = id => { let h = ''; for (let i = 0; i < 34; i++) h += `<i style="height:${14 + ((id * 41 + i * 59 + (i % 5) * 23) % 86)}%"></i>`; return h; };
function tileInner(f) {
  if (f.kind === 'audio') return `<div class="aud" data-aud="${f.id}"><div class="top">${kicon('audio')}<span>${esc(f.ext.slice(1))} · ${fmtSize(f.size)}</span><span class="dur"></span></div>
    <div class="prow"><button class="pbtn" data-play title="Play">${ic('play')}</button><div class="wv" title="Click to jump">${wave(f.id)}</div></div><div class="nm">${esc(f.name)}</div></div>`;
  return `<img class="im" loading="lazy" decoding="async" src="/api/thumb/${f.id}" alt="" onload="this.classList.add('in')" onerror="this.classList.add('in')">${f.kind === 'video' ? `<div class="play"><span>${ic('play')}</span></div>` : ''}<div class="cap">${esc(f.name)}</div>`;
}
function tile(f, w, h, i, animate) {
  const extra = (f.score !== undefined ? `<span class="score">${(f.score * 100).toFixed(0)}%</span>` : '') + (f.proposed ? `<div class="prop">→ ${esc(f.proposed)}</div>` : '');
  return `<div class="tile ${S.selected.has(f.id) ? 'sel' : ''} ${animate ? 'rise' : ''}" data-id="${f.id}" data-kind="${f.kind}" style="width:${w}px;height:${h}px;${f.kind !== 'audio' && f.dom ? `background:${f.dom};` : ''}${animate ? `--i:${Math.min(i, 40)}` : ''}">
    <div class="chk" data-chk>${ic('check', '', 2.8)}</div><div class="qa"><button data-qa="reveal" title="Show in ${FM}">${ic('explorer')}</button><button data-qa="similar" title="Find similar">${ic('sparkle')}</button></div>${extra}${tileInner(f)}</div>`;
}
/* masonry: equal-width columns, each tile keeps its own shape; new pages drop into the shortest column */
function masonry(W) { const gap = P.gap, cn = Math.max(2, Math.round((W + gap) / (P.tile + gap))), w = Math.floor((W - gap * (cn - 1)) / cn); return { gap, cn, w }; }
const tileH = (f, w) => Math.round(clamp(w / aspect(f), w * .42, w * 1.9));
function layoutHTML(files, W, animate, M) {
  const gap = P.gap; let n = 0;
  if ((M?.force || S.layout) === 'cols') {
    const m = masonry(W), cols = Array.from({ length: m.cn }, () => ({ h: 0, items: [] }));
    for (const f of files) { const h = tileH(f, m.w), c = cols.reduce((a, b) => b.h < a.h ? b : a); c.items.push(tile(f, m.w, h, n++, animate)); c.h += h + gap; }
    if (M) Object.assign(M, { w: m.w, heights: cols.map(c => c.h), n });
    return `<div class="cols">${cols.map(c => `<div class="col" style="width:${m.w}px">${c.items.join('')}</div>`).join('')}</div>`;
  }
  if (M) M.w = 0;
  const target = Math.round(P.tile * .78), rows = []; let cur = [], sum = 0;
  for (const f of files) { const a = aspect(f); cur.push({ f, a }); sum += a; if (sum * target + gap * (cur.length - 1) >= W) { rows.push({ items: cur, h: (W - gap * (cur.length - 1)) / sum }); cur = []; sum = 0; } }
  if (cur.length) rows.push({ items: cur, h: Math.min(target, W / sum) });
  return rows.map(r => `<div class="jrow">${r.items.map(({ f, a }) => tile(f, Math.floor(a * r.h), Math.floor(r.h), n++, animate)).join('')}</div>`).join('');
}
/* add tiles to an already-drawn masonry without moving what is on screen */
function appendTo(container, M, files) {
  const colEls = container.querySelectorAll(':scope > .cols > .col');
  if ((M.force || S.layout) !== 'cols' || !colEls.length || !M.w) return false;
  const parts = [...colEls].map(() => []);
  for (const f of files) { const h = tileH(f, M.w); let k = 0; M.heights.forEach((v, i) => { if (v < M.heights[k]) k = i; }); parts[k].push(tile(f, M.w, h, M.n++, true)); M.heights[k] += h + P.gap; }
  colEls.forEach((c, i) => parts[i].length && c.insertAdjacentHTML('beforeend', parts[i].join('')));
  hydrate(container); return true;
}
const GM = {};
function appendGrid(files) { if (!appendTo($('#grid'), GM, files)) render(); else { afterRenderAudio(); updateBar(); setTitle(); drawHero(); } }
function drawHero() {
  const hasLib = S.roots.length || S.files.length; $('.hero').style.display = hasLib && S.view !== 'spaces' ? '' : 'none';
  const fl = filterLabel(); let h = ''; drawBar();
  const u = S.searching && S.understood || {}, read = [u.kind && `only ${KIND[u.kind].label.toLowerCase()}`, u.source && `from ${u.source}`, u.without && `leaving out ${u.without.map(w => `“${w}”`).join(', ')}`].filter(Boolean);
  if (read.length) h += `<span class="und">${ic('sparkle')}Read as ${esc(read.join(' · '))}</span>`;
  if (S.searching && S.related.length && P.ideas) h += `<span class="rel">${ic('plus')}Narrow with</span>${S.related.map((x, i) => ideaChip(x, i, 'add')).join('')}`;
  if (!S.searching && P.ideas) h += ideasHTML();
  if ((fl || S.searching || S.kind !== 'all') && S.files.length) h += `<button class="chipb save" id="saveSpace">${ic('folderplus')}<span>Save these ${S.searching ? S.files.length : S.total} as a space</span></button>`;
  const d = new Date(), n = S.searching ? S.files.length : S.total;
  $('#overline').innerHTML = `${d.toLocaleDateString(undefined, { weekday: 'long', month: 'long', day: 'numeric' })}<span class="soft">· ${n.toLocaleString()} ${S.searching ? 'closest matches' : 'files'}</span>`;
  $('#try').innerHTML = h; hydrate($('#try'));
  $$('#try [data-ex]').forEach(b => b.onclick = () => searchTags([{ q: b.dataset.ex }]));
  $$('#try [data-add]').forEach(b => b.onclick = () => addTag({ q: b.dataset.add }));
  const mi = $('#moreIdeas'); if (mi) mi.onclick = () => { S.ideaAt += IDEAS_SHOWN; drawHero(); };
  const sv = $('#saveSpace'); if (sv) sv.onclick = saveAsSpace;
  $('#layoutBtn').innerHTML = `<i data-ic="${S.layout === 'rows' ? 'columns' : 'rows'}"></i>`; $('#layoutBtn').title = S.layout === 'rows' ? 'Masonry columns' : 'Justified rows'; hydrate($('#layoutBtn'));
}
/* ideas: searches this library can answer well, drawn from what was actually indexed */
const IDEAS_SHOWN = 7;
const thumbDot = t => t.kind === 'audio' ? `<i>${ic('music')}</i>` : `<img src="/api/thumb/${t.id}" alt="" loading="lazy">`;
const ideaChip = (x, i, how = 'ex') => `<button class="idea" data-${how}="${esc(x.q)}" style="--i:${i}" title="${how === 'add' ? 'Add to the search' : 'Search'} “${esc(x.q)}”">${x.top?.length ? `<span class="th">${x.top.slice(0, 2).map(thumbDot).join('')}</span>` : ''}<span>${esc(x.q)}</span></button>`;
function shuffleIdeas(list) {             // strongest ideas are likelier to come first, but every visit differs
  return list.map((x, i) => ({ x, k: Math.random() ** (1 + i * .08) })).sort((a, b) => b.k - a.k).map(o => o.x);
}
function ideasHTML() {
  const pool = S.ideas.length ? S.ideas : EXAMPLES.map(q => ({ q, top: [] }));
  const n = pool.length, at = S.ideaAt % n, page = [...pool, ...pool].slice(at, at + Math.min(IDEAS_SHOWN, n));
  return `<span>${ic('sparkle')}Ideas</span>${page.map((x, i) => ideaChip(x, i)).join('')}${n > IDEAS_SHOWN ? `<button class="idea more" id="moreIdeas" title="Show other ideas">${ic('redo')}<span>More</span></button>` : ''}`;
}
/* ---- the search bar holds tags: words, moods, colours, sources and types, all combined */
const curTags = () => S.searching && S.search?.tags ? S.search.tags : [];
function searchTags(tags) { if (!tags.length) return clearSearch(); runSearch({ qs: tags.map(t => t.q) }, '', { tags }); }
function addTag(t) { searchTags([...curTags().filter(x => x.q !== t.q), t]); }
function drawBar() {
  const fl = filterLabel(), x = `<button data-x title="Remove">${ic('x')}</button>`, out = [];
  if (fl) out.push(`<span class="qt" data-clear="${fl.clear}">${fl.dot ? `<i class="d" style="background:${fl.dot}"></i>` : ic(fl.icon)}${esc(fl.text)}${x}</span>`);
  if (S.kind !== 'all') out.push(`<span class="qt" data-clear="kind">${kicon(S.kind)}${KIND[S.kind].label}${x}</span>`);
  if (S.searching && !S.search?.tags) out.push(`<span class="qt" data-clear="search">${ic('sparkle')}${esc(S.search?.label || 'Search')}${x}</span>`);
  curTags().forEach((t, i) => out.push(`<span class="qt${t.voice ? ' vo' : ''}" data-tag="${i}" title="${t.voice ? 'Spoken search' : 'Click to edit'}">${t.mood ? `<i class="d" style="background:${MOOD_TINT[t.mood]}"></i>` : t.voice ? ic('mic') : ''}${esc(t.label || t.q)}${x}</span>`));
  $('#qtags').innerHTML = out.join(''); hydrate($('#qtags'));
  $('#searchForm').classList.toggle('tagged', out.length > 0);
  $('#q').placeholder = out.length ? 'Add more…' : "Search by what's in it…";
}
function dropTag(el, edit) {
  if (el.dataset.tag != null) { const tags = curTags().slice(), [t] = tags.splice(+el.dataset.tag, 1); if (edit && !t.mood && !t.voice) $('#q').value = t.q; searchTags(tags); $('#q').focus(); return; }
  const k = el.dataset.clear; if (k === 'search') return clearSearch();
  k === 'kind' ? go({ kind: 'all' }, { keepFilters: true }) : go({ [k]: null }, { keepFilters: true, keepKind: true });
}
$('#qtags').onclick = e => { const el = e.target.closest('.qt'); if (el) { e.preventDefault(); dropTag(el, !e.target.closest('[data-x]')); } };
$('#q').addEventListener('keydown', e => { if (e.key === 'Backspace' && !e.target.value) { const last = $$('#qtags .qt').pop(); if (last) { e.preventDefault(); dropTag(last, false); } } });

/* ---- All spaces: each space as a little fanned stack of what is inside */
function spacesHTML() {
  const card = p => { const c = p.cover || [];
    const fan = c.length ? c.map((t, i) => t.kind === 'audio' ? `<span class="k${i}">${ic('music')}</span>` : `<img class="k${i}" src="/api/thumb/${t.id}" alt="" loading="lazy">`).join('') : `<span class="none">Empty</span>`;
    return `<button class="space" data-sp="${p.id}"><div class="fan n${c.length}">${fan}</div><div class="spname"><span class="ring" style="--c:${HUES[p.id % HUES.length]}"></span>${esc(p.name)}<em>${p.count}</em></div></button>`; };
  return `<div class="spaces"><div class="sphead"><h1 class="d-head">All Spaces</h1><div class="acts"><button class="btn ghost sm" id="spAuto">${ic('sparkle')}<span>Auto-group</span></button><button class="btn dark sm" id="spNew"><span class="ring" style="--c:var(--accent)"></span><span>Create a new space</span></button></div></div>
    <div class="spgrid">${S.projects.map(card).join('')}<button class="space add" id="spNew2"><div class="fan"><span class="none">${ic('plus')}</span></div><div class="spname">New space</div></button></div></div>`;
}
function bindSpaces() {
  $$('#grid [data-sp]').forEach(b => b.onclick = () => go({ view: 'project', project: +b.dataset.sp }));
  ['#spNew', '#spNew2'].forEach(s => $(s).onclick = async () => { const name = prompt('Name your new space:'); if (!name) return; await api('/projects', { method: 'POST', body: { name } }); await loadProjects(); render(); });
  $('#spAuto').onclick = autoGroup;
}
async function loadIdeas() { try { const r = await api('/ideas?n=36'); if (r.ideas.length) { S.ideas = shuffleIdeas(r.ideas); S.ideaAt = 0; if (!S.searching) drawHero(); } } catch { } }
async function saveAsSpace() {
  const fl = filterLabel(); const name = prompt('Name this space:', S.searching ? (S.search?.label || 'Search').replace(/[“”]/g, '') : fl ? fl.text.replace('Name: ', '') : 'Collection'); if (!name) return;
  const ids = S.searching ? S.files.map(f => f.id) : (await api('/files?' + filterParams({ ids_only: 'true' }))).ids;
  const r = await api('/projects', { method: 'POST', body: { name, file_ids: ids } }); await refreshAll(); go({ view: 'project', project: r.id }); toast(`Created “${name}” with ${ids.length} files`);
}
function render() {
  if (S.view === 'spaces') { $('#grid').innerHTML = spacesHTML(); $('#empty').hidden = true; bindSpaces(); updateBar(); setTitle(); drawHero(); return; }
  const W = Math.floor($('#grid').clientWidth || ($('#scroller').clientWidth - 88));
  for (const k in GM) delete GM[k];
  $('#grid').innerHTML = layoutHTML(S.files, W, S.animNext, GM); S.animNext = false; stopHover();
  const none = !S.files.length; $('#empty').hidden = !none;
  if (none) {
    if (!S.roots.length) {
      $('#empty').className = '';
      $('#empty').innerHTML = `<div class="over">Your private oasis</div><h1>Everything you've made,<br>finally findable.</h1>
        <p class="lead">Point File Mind at a messy folder. It looks at every photo and video and listens to every sound — on this PC only — so you can search by what's in them and sort them into projects.</p>${addBoxHTML()}
        <div class="steps">
          <div class="step a"><div class="n"><i data-ic="eye"></i>Step 1</div><h3>It reads everything</h3><p>EmbeddingGemma 2 looks at pictures and video frames and listens to audio. Thousands of files take a while the first time; you can pause, and search already works on what's done.</p></div>
          <div class="step b"><div class="n"><i data-ic="search"></i>Step 2</div><h3>Search by meaning</h3><p>Describe what it looks like or sounds like — “red logo on black”, “man talking about pricing”. Or filter by color, or by the AI tool a file came from.</p></div>
          <div class="step c"><div class="n"><i data-ic="move"></i>Step 3</div><h3>Commit when happy</h3><p>Spaces and new names are only drafts. Nothing on disk changes until you review the plan and press commit — and every commit can be undone.</p></div></div>`;
      bindAddBox($('#empty'));
    } else {
      $('#empty').className = 'plain';
      $('#empty').innerHTML = S.searching ? `<h2>Nothing close to that.</h2><p>Try describing what it looks like, or what it's about.</p>`
        : S.view === 'renames' ? `<h2>No names drafted.</h2><p>Open any file and type a new name — it appears here until you commit.</p>`
        : S.view === 'project' ? `<h2>An empty space.</h2><p>Select files in the library and choose “Add to space”.</p>` : `<h2>Nothing here yet.</h2><p>Files appear as they're read, or nothing matches this filter.</p>`;
    }
  }
  document.body.classList.toggle('selecting', S.selected.size > 0); updateBar(); setTitle(); drawHero(); hydrate(); afterRenderAudio();
}
$('#scroller').addEventListener('scroll', () => $('#toolbar').classList.toggle('scrolled', $('#scroller').scrollTop > 6), { passive: true });
let rz; new ResizeObserver(() => { clearTimeout(rz); rz = setTimeout(() => { if (!S.open && S.files.length) { S.animNext = false; render(); } }, 150); }).observe($('#scroller'));

/* ---- hover preview for videos (muted, plays after a short dwell) */
const hv = { tile: null, timer: null, vid: null };
function stopHover() { clearTimeout(hv.timer); if (hv.vid) hv.vid.remove(); hv.tile = hv.vid = null; }
const hoverPreview = e => {
  const t = e.target.closest('.tile[data-kind="video"]'); if (t === hv.tile) return; stopHover(); if (!t || !P.hover) return; hv.tile = t;
  hv.timer = setTimeout(() => { const v = document.createElement('video'); v.className = 'hv'; v.muted = true; v.loop = true; v.playsInline = true; v.preload = 'auto'; v.src = '/api/file/' + t.dataset.id;
    t.insertBefore(v, t.querySelector('.chk')); v.play().then(() => v.classList.add('on')).catch(() => { }); hv.vid = v; }, 420);
};
['#grid', '#feed'].forEach(s => { $(s).addEventListener('mouseover', hoverPreview); $(s).addEventListener('mouseleave', stopHover); });

/* ---- selection + clicks */
let lastClicked = null;
function gridClick(e, getFiles, open) {
  const t = e.target.closest('.tile'); if (!t) return; const id = +t.dataset.id;
  const qa = e.target.closest('[data-qa]'); if (qa) { e.stopPropagation(); return qa.dataset.qa === 'reveal' ? reveal(id) : (go_similar(id)); }
  const aud = e.target.closest('.aud');
  if (aud && !e.ctrlKey && !e.metaKey && !e.shiftKey && !e.target.closest('[data-chk]')) {
    if (e.target.closest('[data-play]')) return playAudio(+aud.dataset.aud);
    const wv = e.target.closest('.wv'); if (wv) { const r = wv.getBoundingClientRect(); return playAudio(+aud.dataset.aud, clamp((e.clientX - r.left) / r.width, 0, 1)); }
  }
  if (e.target.closest('[data-chk]') || e.ctrlKey || e.metaKey || e.shiftKey || S.selected.size) {
    if (e.shiftKey && lastClicked) { const ids = getFiles().map(f => f.id), a = ids.indexOf(lastClicked), b = ids.indexOf(id); ids.slice(Math.min(a, b), Math.max(a, b) + 1).forEach(i => S.selected.add(i)); }
    else if (S.selected.has(id)) S.selected.delete(id); else S.selected.add(id);
    lastClicked = id; return refreshSel();
  }
  open(id);
}
$('#grid').onclick = e => gridClick(e, () => S.files, id => { S.ctx = null; openViewer(id); });
function refreshSel() { $$('.tile').forEach(t => t.classList.toggle('sel', S.selected.has(+t.dataset.id))); document.body.classList.toggle('selecting', S.selected.size > 0); updateBar(); }
const go_similar = id => { const f = S.files.find(x => x.id === id) || F.files.find(x => x.id === id) || S.cur; runSearch({ similar_to: id }, 'Similar to ' + (f?.name || 'file')); };

/* ---- right-click menu */
function showCtx(e, id) {
  e.preventDefault();
  const sp = S.projects.slice(0, 6);
  $('#ctx').innerHTML = `<button data-c="open">${ic('expand')}Open</button><button data-c="reveal">${ic('explorer')}Show in ${FM}</button><button data-c="copy">${ic('copy')}Copy path</button><button data-c="similar">${ic('sparkle')}Find similar</button><hr>
    ${sp.length ? `<div class="sub">Add to space</div>${sp.map(p => `<button data-c="add" data-p="${p.id}">${glyph(p.id)}${esc(p.name)}</button>`).join('')}` : ''}<button data-c="new">${ic('folderplus')}New space with this</button>`;
  const m = $('#ctx'); m.hidden = false; m.style.left = clamp(e.clientX, 8, innerWidth - 240) + 'px'; m.style.top = clamp(e.clientY, 8, innerHeight - m.offsetHeight - 8) + 'px';
  m.onclick = async ev => {
    const b = ev.target.closest('[data-c]'); if (!b) return; m.hidden = true; const c = b.dataset.c;
    if (c === 'open') { S.ctx = $('#viewer').hidden ? null : F.files.slice(); openViewer(id); } if (c === 'reveal') reveal(id); if (c === 'copy') { const i = await api(`/info/${id}`); copyText(i.path); } if (c === 'similar') go_similar(id);
    if (c === 'add') { await api(`/projects/${b.dataset.p}/add`, { method: 'POST', body: { file_ids: [id] } }); toast('Added to space'); await loadProjects(); await loadStats(); }
    if (c === 'new') { const n = prompt('Name your new space:'); if (n) { await api('/projects', { method: 'POST', body: { name: n, file_ids: [id] } }); await refreshAll(); toast('Space created'); } }
  }; hydrate(m);
}
['#grid', '#feed'].forEach(s => $(s).addEventListener('contextmenu', e => { const t = e.target.closest('.tile'); if (t) showCtx(e, +t.dataset.id); }));
document.addEventListener('click', e => { if (!e.target.closest('#ctx')) $('#ctx').hidden = true; if (!e.target.closest('#dock')) $('#sortMenu').hidden = true; });

/* ---- dock: sort + layout */
$('#sortBtn').onclick = e => { e.stopPropagation(); $('#sortMenu').hidden = !$('#sortMenu').hidden; $$('#sortMenu button').forEach(b => b.classList.toggle('on', b.dataset.sort === S.sort)); };
$('#sortMenu').onclick = e => { const s = e.target.dataset.sort; if (!s) return; S.sort = s; $('#sortMenu').hidden = true; drawColors(); if (!S.searching) { S.animNext = true; loadFiles(true); } };
$('#layoutBtn').onclick = () => { S.layout = P.layout = S.layout === 'rows' ? 'cols' : 'rows'; applyPrefs(); S.animNext = false; glide(render); };
const resize = d => { P.tile = clamp(P.tile + d, 160, 560); applyPrefs(); S.animNext = false; glide(render); if (S.open) relayoutFeed(); };
$('#sizeDown').onclick = () => resize(-50); $('#sizeUp').onclick = () => resize(50);

/* ================================================================ inline audio */
const AP = { el: new Audio(), id: null }; AP.el.preload = 'none';
const fmtDur = s => !s ? '' : (s >= 3600 ? Math.floor(s / 3600) + ':' + String(Math.floor(s % 3600 / 60)).padStart(2, '0') : Math.floor(s / 60)) + ':' + String(Math.floor(s % 60)).padStart(2, '0');
const audTile = id => document.querySelector(`.aud[data-aud="${id}"]`);
function setPlaying(id, on) { const t = audTile(id); if (!t) return; t.classList.toggle('playing', on); t.querySelector('.pbtn').innerHTML = ic(on ? 'pause' : 'play'); }
function paintProg() { const t = audTile(AP.id); if (!t) return; const bars = t.querySelectorAll('.wv i'), p = AP.el.duration ? AP.el.currentTime / AP.el.duration : 0; bars.forEach((b, i) => b.classList.toggle('on', i < p * bars.length)); }
function stopAudio() { if (AP.id == null) return; AP.el.pause(); const t = audTile(AP.id); if (t) { setPlaying(AP.id, false); t.querySelectorAll('.wv i.on').forEach(b => b.classList.remove('on')); } AP.id = null; }
function playAudio(id, frac) {
  AP.el.volume = S.vol / 100;
  if (AP.id === id) { if (frac != null && AP.el.duration) AP.el.currentTime = frac * AP.el.duration; if (AP.el.paused) { AP.el.play(); setPlaying(id, true); } else if (frac == null) { AP.el.pause(); setPlaying(id, false); } return; }
  stopAudio(); AP.id = id; AP.el.src = `/api/file/${id}`;
  if (frac != null) AP.el.onloadedmetadata = () => { AP.el.currentTime = frac * AP.el.duration; AP.el.onloadedmetadata = null; };
  AP.el.play().catch(() => { }); setPlaying(id, true);
}
AP.el.ontimeupdate = paintProg; AP.el.onended = stopAudio;
const waveCache = {}, waveQ = []; let waveBusy = 0;
const waveObs = new IntersectionObserver(es => es.forEach(e => { if (e.isIntersecting) { waveObs.unobserve(e.target); waveQ.push(e.target); pumpWave(); } }), { rootMargin: '300px' });
function pumpWave() { while (waveBusy < 3 && waveQ.length) { const el = waveQ.shift(); waveBusy++; loadWave(el).finally(() => { waveBusy--; pumpWave(); }); } }
async function loadWave(el) { const id = +el.dataset.aud; let w = waveCache[id]; if (!w) { try { w = waveCache[id] = await api('/wave/' + id); } catch { return; } } applyWave(el, w); }
function applyWave(el, w) { if (!el.isConnected) return; el.querySelector('.wv').innerHTML = w.peaks.map(p => `<i style="height:${Math.round(p * 100)}%"></i>`).join(''); el.querySelector('.dur').textContent = fmtDur(w.duration); if (AP.id === +el.dataset.aud) paintProg(); }
function afterRenderAudio() { $$('.aud').forEach(el => { const w = waveCache[+el.dataset.aud]; if (w) applyWave(el, w); else waveObs.observe(el); }); if (AP.id != null) { setPlaying(AP.id, !AP.el.paused); paintProg(); } }

/* ================================================================ search */
async function runSearch(body, label, opt = {}) {
  let tags = opt.tags || null;
  if (body.q && !body.qs) { tags = [{ q: body.q, label: opt.label || null, mood: opt.mood || null }]; body = { qs: [body.q] }; }
  if (tags) label = tags.map(t => t.label || `“${t.q}”`).join(' + ');
  S.searching = true; S.search = { body, label, tags }; viewerUI(false); S.animNext = true; S.selected.clear();
  if (opt.rec !== 'none') record();
  try {
    const mine = S.search;
    const r = await api('/search', { method: 'POST', body: { ...body, root: S.root, kinds: S.kind === 'all' ? null : [S.kind], color: S.color, source: S.source, project: S.view === 'project' ? S.project : null, limit: 200 } });
    if (S.search !== mine) return;
    S.files = r.files; S.related = []; S.understood = r.understood || {}; $('#more').hidden = true; $('#scroller').scrollTop = 0; render(); drawMoods();
    const have = new Set((tags || []).map(t => t.q.toLowerCase()));
    api('/ideas/related', { method: 'POST', body: { ids: S.files.slice(0, 30).map(f => f.id), q: tags?.at(-1)?.q || '', n: 6 + have.size } })
      .then(x => { if (S.search === mine) { S.related = x.ideas.filter(i => !have.has(i.q.toLowerCase())).slice(0, 6); drawHero(); } }).catch(() => { });
  } catch (err) { toast(err.message, 'x', true); S.searching = false; S.search = null; render(); }
}
function clearSearch() { S.searching = false; S.search = null; S.related = []; $('#q').value = ''; S.animNext = true; record(); loadFiles(true); drawMoods(); }
function submitSearch() { const q = $('#q').value.trim(); if (!q) return; $('#q').value = ''; addTag({ q }); }
$('#searchForm').onsubmit = e => { e.preventDefault(); submitSearch(); };
$('#focusSearch').onclick = () => { if (S.open) goBack(); $('#q').focus(); };
$('#imgq').onchange = async e => {
  const f = e.target.files[0]; if (!f) return; viewerUI(false);
  const fd = new FormData(); fd.append('file', f); S.searching = true; S.search = { body: null, label: 'Like ' + f.name, text: '' }; S.animNext = true;
  const r = await api(`/search/image?${S.root ? 'root=' + S.root : ''}`, { method: 'POST', body: fd }); S.files = r.files; $('#more').hidden = true; record(true); render(); e.target.value = '';
};

/* ================================================================ viewer: the file on top, an endless "more like this" below */
const F = { id: null, files: [], done: false, loading: false, M: {} };
function viewerUI(on) {
  $('#browse').hidden = on; $('#viewer').hidden = !on;
  if (!on) { if (S.open) stopMedia(); S.open = null; S.cur = null; F.id = null; $('#stage').innerHTML = ''; $('#feed').innerHTML = ''; updateBar(); }
}
function stopMedia() { const m = $('#stage video, #stage audio'); if (m) { m.pause(); m.removeAttribute('src'); m.load(); } }
function paintVol() {
  const eff = S.muted || S.vol === 0; $('#volBtn').innerHTML = ic(eff ? 'mute' : S.vol < 45 ? 'vol1' : 'vol'); $('#volRange').value = eff ? 0 : S.vol; $('#volVal').textContent = (eff ? 0 : S.vol) + '%';
}
function applyVol() { const m = $('#stage video, #stage audio'); if (m) { m.volume = S.vol / 100; m.muted = S.muted || S.vol === 0; } paintVol(); }
const saveVol = () => { store.set('vol', S.vol); store.set('muted', S.muted ? '1' : '0'); };
$('#volRange').oninput = e => { S.vol = +e.target.value; S.muted = S.vol === 0; saveVol(); applyVol(); };
$('#volBtn').onclick = () => { S.muted = !S.muted; if (!S.muted && S.vol === 0) S.vol = 60; saveVol(); applyVol(); };
const ctxIds = () => (S.ctx && S.ctx.length ? S.ctx : S.files).map(f => f.id);

function buildStage(id, kind, ar) {
  const st = $('#stage'); st.innerHTML = ''; st.dataset.id = id; let m;
  if (kind === 'image') { m = document.createElement('img'); m.alt = ''; m.decoding = 'async'; m.src = `/api/file/${id}`; st.appendChild(m); }
  else if (kind === 'video' || kind === 'audio') {
    m = document.createElement(kind); m.controls = true; m.loop = kind === 'video'; m.preload = 'auto'; m.src = `/api/file/${id}`; if (kind === 'video') m.poster = `/api/thumb/${id}`;
    m.volume = S.vol / 100; m.muted = S.muted || S.vol === 0;
    m.onvolumechange = () => { S.vol = Math.round(m.volume * 100); S.muted = m.muted; saveVol(); paintVol(); };
    st.appendChild(m);
    m.play().catch(() => { S.muted = true; applyVol(); m.play().catch(() => { }); toast('Browser blocked sound on autoplay — press the speaker to unmute', 'vol', true); });
  }
  if (m && ar && kind !== 'audio') m.style.cssText = `aspect-ratio:${ar};width:min(100%,calc((100vh - 230px) * ${ar.toFixed(4)}))`;   // final size known before the bytes arrive
  return m;
}
async function openViewer(id, opt = {}) {
  stopAudio(); stopHover();
  const cur = H.stack[H.i]; if (cur && !H.busy) cur.scroll =!$('#browse').hidden ? $('#scroller').scrollTop : $('#viewer').scrollTop;   // remember where we were
  const from = opt.rec == null ? grabTile(id) : null;     // a click: remember the tile so it can fly into place
  if (S.open) stopMedia();
  S.zoom = 1; viewerUI(true); S.open = id; S.selected.clear(); $('#floatbar').hidden = true;
  if (opt.rec === 'replace') record(true); else if (opt.rec == null || opt.rec === 'push') record();
  $('#viewer').scrollTo({ top: 0, behavior: opt.smooth && !from ? 'smooth' : 'auto' });
  if (from) morphIn(from, buildStage(id, from.kind, from.ar));
  const f = await api(`/info/${id}`); if (S.open !== id) return; S.cur = f;
  $('#title').className = 'title two'; $('#title').innerHTML = `<span>${esc(f.name)}</span><span class="sub">${esc(KIND[f.kind]?.label.replace(/s$/, '') || f.kind)} · ${fmtSize(f.size)}</span>`;
  if ($('#stage').dataset.id !== String(id) || !from) buildStage(id, f.kind, f.tw && f.th ? f.tw / f.th : 0);
  $('#zoom').hidden = f.kind !== 'image' && f.kind !== 'video'; $('#volbox').hidden = f.kind === 'image'; applyZoom(); paintVol();
  const ids = ctxIds(), i = ids.indexOf(id); $('#prevItem').hidden = i <= 0; $('#nextItem').hidden = i < 0 || i >= ids.length - 1;
  drawInspector(f); setNav();
  await startFeed(id);
  if (opt.scroll) {            // coming back through history: grow the feed until the old position exists
    for (let k = 0; k < 8 && $('#viewer').scrollHeight < opt.scroll + innerHeight && !F.done && S.open === id; k++) await feedMore();
    $('#viewer').scrollTop = opt.scroll;
  }
}
const step = d => { const ids = ctxIds(), i = ids.indexOf(S.open); if (i >= 0 && ids[i + d]) openViewer(ids[i + d], { rec: 'replace' }); };
$('#prevItem').onclick = () => step(-1); $('#nextItem').onclick = () => step(1);
function applyZoom() { $$('#stage img,#stage video').forEach(m => m.style.transform = `scale(${S.zoom})`); $('#zVal').textContent = Math.round(S.zoom * 100) + '%'; }
$('#zIn').onclick = () => { S.zoom = Math.min(4, S.zoom * 1.25); applyZoom(); }; $('#zOut').onclick = () => { S.zoom = Math.max(.25, S.zoom / 1.25); applyZoom(); };

/* ---- the endless feed of similar files */
async function startFeed(id) {
  Object.assign(F, { id, files: [], done: false, loading: false, M: { force: 'cols' } });
  $('#feed').innerHTML = ''; $('#feedmsg').textContent = '';
  $('#feedhead').innerHTML = `<div><h2 class="d-head">More like this</h2><div class="sub">Closest first, by what is in them. Keep scrolling; click any to follow it.</div></div>
    <div class="acts"><button class="btn ghost sm" id="fhGrid">${ic('images')}<span>Open as results</span></button><button class="btn dark sm" id="fhColl">${ic('folderplus')}<span>Make a collection</span></button></div>`;
  $('#fhGrid').onclick = () => go_similar(id); $('#fhColl').onclick = () => collectionModal(S.cur);
  await feedMore();
}
async function feedMore() {
  if (F.loading || F.done || !F.id) return; F.loading = true; const id = F.id, page = 60;
  try {
    const r = await api('/search', { method: 'POST', body: { similar_to: id, limit: page, offset: F.files.length } });
    if (F.id !== id) return;
    const fresh = r.files; if (fresh.length < page) F.done = true;
    const first = !F.files.length; F.files.push(...fresh);
    if (first) { F.M = { force: 'cols' }; $('#feed').innerHTML = layoutHTML(fresh, Math.floor($('#feed').clientWidth), true, F.M); hydrate($('#feed')); }
    else appendTo($('#feed'), F.M, fresh);
    afterRenderAudio();
    $('#feedmsg').textContent = F.done ? (F.files.length ? `That's everything — ${F.files.length.toLocaleString()} files, least alike last.` : 'Nothing similar yet.') : '';
  } catch (e) { $('#feedmsg').textContent = e.message; F.done = true; }
  finally { F.loading = false; }
}
function relayoutFeed() { if (!F.files.length) return; F.M = { force: 'cols' }; $('#feed').innerHTML = layoutHTML(F.files, Math.floor($('#feed').clientWidth), false, F.M); hydrate($('#feed')); afterRenderAudio(); }
new IntersectionObserver(es => { if (es[0].isIntersecting && S.open) feedMore(); }, { root: $('#viewer'), rootMargin: '1400px' }).observe($('#feedmore .sentinel'));
let rzf; new ResizeObserver(() => { clearTimeout(rzf); rzf = setTimeout(() => { if (S.open) relayoutFeed(); }, 150); }).observe($('#feed'));
$('#feed').onclick = e => gridClick(e, () => F.files, id => { S.ctx = F.files.slice(); openViewer(id, { smooth: true }); });

function drawInspector(f) {
  const id = f.id, box = $('#inspector'), others = S.projects.filter(p => !f.projects.some(x => x.id === p.id)), src = S.sources.find(s => s.key === f.src), m = f.meta || {};
  const label = k => S.colors.find(c => c.key === k)?.label || k;
  box.innerHTML = `
    <div class="ihead"><h1 class="nm d-head">${esc(f.name.replace(/\.[^.]+$/, ''))}</h1>
      <div class="meta">${kicon(f.kind, 14)}<span>${esc(KIND[f.kind]?.label.replace(/s$/, '') || f.kind)} · ${esc(f.ext.slice(1).toUpperCase())}</span><span>${fmtSize(f.size)}</span><span>${ago(f.mtime)}</span></div></div>
    <div class="is"><div class="quickbtns"><button class="btn dark" id="qbExplorer" title="Show in ${FM}  (E)">${ic('explorer')}<span>Show in ${FMS}</span></button>
      <button class="btn icon" id="qbOpen" title="Open in a new window">${ic('open')}</button><button class="btn icon" id="qbCopy" title="Copy the full path">${ic('copy')}</button><button class="btn icon" id="qbSim" title="All similar files as search results">${ic('images')}</button></div></div>
    ${f.tags?.length ? `<div class="is"><h5><i data-ic="tag"></i>Tags <span class="cnt">· what is in it</span></h5>
      <div class="tagline">${f.tags.map(t => `<button data-t="${esc(t)}" title="Search “${esc(t)}”  ·  Shift-click to add it to the current search">${esc(t)}</button>`).join('')}</div></div>` : ''}
    <div class="is"><dl class="kv">
      ${src ? `<dt>Source</dt><dd><button class="chip" id="srcChip" title="Everything from ${esc(src.label)}"><span class="dot" style="background:${src.color}"></span>${esc(src.label)}</button></dd>` : ''}
      ${m.author ? `<dt>By</dt><dd>${esc(m.author)}</dd>` : ''}${m.date ? `<dt>Made</dt><dd>${esc(m.date)}</dd>` : ''}
      ${m.prompt ? `<dt>Prompt</dt><dd class="pr"><span title="${esc(m.prompt)}">${esc(m.prompt)}</span><button class="mini" id="pCopy" title="Copy the prompt">${ic('copy')}</button></dd>` : ''}
      <dt>Folder</dt><dd class="path" title="${esc(f.folder)}">${esc(f.folder.length > 34 ? '…' + f.folder.slice(-32) : f.folder)}</dd></dl>${f.error ? `<div class="err">${esc(f.error)}</div>` : ''}</div>
    ${f.colors.length ? `<div class="is"><h5><i data-ic="palette"></i>Colors</h5>
      <div class="strip">${f.colors.map(c => `<button style="background:${c}" data-near="${c}" title="${c} — find files in this colour"></button>`).join('')}</div>
      ${(f.swatches || []).length ? `<div class="mostly">Mostly ${(f.swatches || []).map(w => `<button data-color="${w.key}" title="Files that are largely ${esc(label(w.key))}">${esc(label(w.key))} <b>${Math.round(w.share * 100)}%</b></button>`).join('<span>·</span>')}</div>` : ''}</div>` : ''}
    <div class="is"><h5><i data-ic="folder"></i>Space</h5><div class="chips">
      ${f.projects.map(p => `<span class="chip">${glyph(p.id)}${esc(p.name)}<button data-rm="${p.id}" title="Remove from this space">${ic('x')}</button></span>`).join('')}
      <span class="chip add"><i data-ic="plus"></i><select id="insAdd"><option value="">Add</option>${others.map(p => `<option value="${p.id}">${esc(p.name)}</option>`).join('')}<option value="__new">New space…</option></select></span></div></div>
    <div class="is"><h5><i data-ic="pencil"></i>New name <span class="cnt">· applied only when you commit</span></h5>
      <div class="pillin"><input id="insName" value="${esc(f.proposed || f.name.replace(/\.[^.]+$/, ''))}" spellcheck="false"><button id="insSave" title="Save">${ic('send')}</button></div>
      ${f.proposed ? `<div class="pending">Pending: <b>${esc(f.proposed)}</b> · <a id="insClr">remove</a></div>` : ''}</div>`;
  $('#qbExplorer').onclick = () => reveal(id); $('#qbOpen').onclick = () => window.open(`/api/file/${id}`, '_blank'); $('#qbCopy').onclick = () => copyText(f.path); $('#qbSim').onclick = () => go_similar(id);
  if (m.prompt) $('#pCopy').onclick = () => copyText(m.prompt, 'Prompt copied');
  $$('[data-t]', box).forEach(b => b.onclick = e => e.shiftKey && S.searching ? addTag({ q: b.dataset.t }) : searchTags([{ q: b.dataset.t }]));
  const sc = $('#srcChip'); if (sc) sc.onclick = () => go({ source: f.src });
  $$('[data-near]', box).forEach(d => d.onclick = () => go({ near: d.dataset.near }));
  $$('[data-color]', box).forEach(d => d.onclick = () => go({ color: d.dataset.color }));
  $$('[data-rm]', box).forEach(b => b.onclick = async () => { await api(`/projects/${b.dataset.rm}/remove`, { method: 'POST', body: { file_ids: [id] } }); await loadProjects(); await loadStats(); refreshInspector(id); });
  $('#insAdd').onchange = async e => {
    let pid = e.target.value; if (!pid) return;
    if (pid === '__new') { const n = prompt('Name your new space:'); if (!n) { e.target.value = ''; return; } pid = (await api('/projects', { method: 'POST', body: { name: n } })).id; }
    await api(`/projects/${pid}/add`, { method: 'POST', body: { file_ids: [id] } }); await loadProjects(); await loadStats(); toast('Added to space'); refreshInspector(id);
  };
  const save = async () => { const v = $('#insName').value.trim(); if (!v) return; await api('/names', { method: 'PUT', body: { file_ids: [id], new_name: v } }); await loadStats(); toast('Name drafted — applied on commit'); refreshInspector(id); };
  $('#insSave').onclick = save; $('#insName').onkeydown = e => { if (e.key === 'Enter') save(); };
  const clr = $('#insClr'); if (clr) clr.onclick = async () => { await api('/names/clear', { method: 'POST', body: { file_ids: [id] } }); await loadStats(); refreshInspector(id); };
  hydrate(box);
}
async function refreshInspector(id) { const f = await api(`/info/${id}`); if (S.open === id) { S.cur = f; drawInspector(f); } }

async function collectionModal(f, startThr) {
  const sims = (await api('/search', { method: 'POST', body: { similar_to: f.id, limit: 240 } })).files; if (!sims.length) return;
  const pct = sims.map(s => s.score * 100), hi = Math.ceil(pct[0]), lo = Math.floor(pct[pct.length - 1]), start = startThr ?? Math.round(pct[Math.min(23, sims.length - 1)]), stem = f.name.replace(/\.[^.]+$/, '');
  modal(`<div class="mh"><h3>Make a collection</h3><p>A new space made of this file and everything similar to it. Slide to decide how close a match has to be.</p></div>
    <div class="mb"><div class="line"><span class="k">Name</span><input id="cName" style="flex:1" value="${esc('Similar to ' + stem.slice(0, 40))}"></div>
    <div class="line"><span class="k">Similarity</span><span class="hint">looser</span><input id="cThr" type="range" min="${lo}" max="${hi}" step="1" value="${clamp(start, lo, hi)}" style="flex:1"><span class="hint">closer</span><b id="cPct" style="width:56px;text-align:right"></b></div>
    <div class="hint" id="cCount" style="margin-bottom:8px"></div><div class="bigstrip" id="cStrip"></div>
    <div class="line"><button class="btn dark" id="cMake">${ic('check')}<span>Create collection</span></button><button class="btn" id="cCancel">Cancel</button></div></div>`, { w: 460 });
  const pick = () => sims.filter(s => s.score * 100 >= +$('#cThr').value);
  const upd = () => { const p = pick(); $('#cPct').textContent = '≥ ' + $('#cThr').value + '%'; $('#cCount').textContent = `${p.length + 1} files — this one plus ${p.length} similar`;
    $('#cStrip').innerHTML = `<img src="/api/thumb/${f.id}" class="me">` + p.slice(0, 17).map(s => s.kind === 'audio' ? `<span>${kicon('audio', 20)}</span>` : `<img src="/api/thumb/${s.id}">`).join(''); hydrate($('#cStrip')); };
  $('#cThr').oninput = upd; upd(); $('#cCancel').onclick = closeModal;
  $('#cMake').onclick = async () => { const name = $('#cName').value.trim() || 'Collection'; const r = await api('/projects', { method: 'POST', body: { name, file_ids: [f.id, ...pick().map(s => s.id)] } }); closeModal(); await refreshAll(); go({ view: 'project', project: r.id }); toast(`Created “${name}”`); };
}

/* ---- Customize: an inline panel, every change shows live */
function settingsModal() {
  if ($('#modal').dataset.kind === 'tune' && !$('#modal').hidden) return closeModal();
  const ACC = ['#ff5a1f', '#e5484d', '#e0a21b', '#3c9d55', '#1b9f97', '#2f63d8', '#8f4fcf', '#cf3aae', '#16181d'];
  const seg = (id, opts) => `<div class="seg" id="${id}">${opts.map(([v, l, i]) => `<button data-v="${v}">${i ? ic(i) : ''}${l}</button>`).join('')}</div>`;
  const tog = (id, t, sub) => `<div class="trow"><span>${t}${sub ? `<small>${sub}</small>` : ''}</span><button class="toggle" id="${id}"></button></div>`;
  modal(`<div class="mh"><h3>Make it yours</h3><p>Changes show as you make them. Saved on this PC; your files are never touched.</p></div><div class="mb tune">
    <h6>Theme</h6><div class="themes" id="sTheme">${THEMES.map(([k, l, bg, side]) => `<button data-v="${k}"><span class="tt" style="background:${bg}"><i style="background:${side}"></i><b><span></span><span></span><span></span></b></span>${l}</button>`).join('')}</div>
    <h6>Accent</h6><div class="accents" id="sAcc">${ACC.map(c => `<button data-c="${c}" style="background:${c}" title="${c}"></button>`).join('')}<label title="Any color">${ic('pipette')}<input type="color" id="sAccAny" hidden></label></div>
    <h6>Headings</h6>${seg('sHead', [['serif', 'Serif'], ['sans', 'Sans']])}
    <h6>Layout</h6>${seg('sLay', [['cols', 'Masonry', 'columns'], ['rows', 'Rows', 'rows']])}
    <h6>Tile size <output id="oTile"></output></h6><input type="range" id="sTile" min="160" max="560" step="10">
    <h6>Spacing <output id="oGap"></output></h6><input type="range" id="sGap" min="0" max="32" step="1">
    <h6>Corners <output id="oRad"></output></h6><input type="range" id="sRad" min="0" max="28" step="1">
    <h6>Tiles</h6>${seg('sStyle', [['lift', 'Lift'], ['soft', 'Shadow'], ['outline', 'Outline'], ['flat', 'Flat']])}
    <h6>Show</h6>${tog('sNames', 'File names', 'under every tile, not just on hover')}${tog('sHover', 'Video preview on hover', 'plays muted after a moment')}${tog('sIdeas', 'Search ideas', 'suggestions picked from your library')}
    <h6>Default volume <output id="oVol"></output></h6><input type="range" id="sVol" min="0" max="100" step="5">
    <div class="line"><button class="btn ghost" id="sReset">${ic('redo')}<span>Reset everything</span></button></div></div>`, { kind: 'tune', w: 340 });
  const sync = () => {
    [['#sTheme', P.theme], ['#sHead', P.head], ['#sLay', P.layout], ['#sStyle', P.tstyle]].forEach(([g, v]) => $$(g + ' [data-v]').forEach(b => b.classList.toggle('on', b.dataset.v === v)));
    $('#sTile').value = P.tile; $('#oTile').textContent = P.tile + 'px'; $('#sGap').value = P.gap; $('#oGap').textContent = P.gap + 'px'; $('#sRad').value = P.rad; $('#oRad').textContent = P.rad + 'px';
    $$('#sAcc button').forEach(b => b.classList.toggle('on', b.dataset.c === P.accent)); $('#sAccAny').value = P.accent;
    $('#sNames').classList.toggle('on', P.names); $('#sHover').classList.toggle('on', P.hover); $('#sIdeas').classList.toggle('on', P.ideas);
    $('#sVol').value = S.vol; $('#oVol').textContent = S.vol + '%';
  };
  const commit = (relay = true) => { applyPrefs(); S.layout = P.layout; sync(); if (relay) { S.animNext = false; if (S.open) relayoutFeed(); else glide(render); } };
  const pick = (id, key, relay) => $(id).onclick = e => { const v = e.target.closest('[data-v]')?.dataset.v; if (v) { P[key] = v; commit(relay); } };
  pick('#sTheme', 'theme', false); pick('#sHead', 'head', false); pick('#sLay', 'layout', true); pick('#sStyle', 'tstyle', false);
  $('#sTile').oninput = e => { P.tile = +e.target.value; commit(); }; $('#sGap').oninput = e => { P.gap = +e.target.value; commit(); }; $('#sRad').oninput = e => { P.rad = +e.target.value; commit(false); };
  $('#sAcc').onclick = e => { const c = e.target.dataset.c; if (c) { P.accent = c; commit(false); } }; $('#sAccAny').oninput = e => { P.accent = e.target.value; commit(false); };
  $('#sNames').onclick = () => { P.names = !P.names; commit(false); }; $('#sHover').onclick = () => { P.hover = !P.hover; commit(false); };
  $('#sIdeas').onclick = () => { P.ideas = !P.ideas; commit(false); drawHero(); };
  $('#sVol').oninput = e => { S.vol = +e.target.value; S.muted = S.vol === 0; saveVol(); applyVol(); sync(); };
  $('#sReset').onclick = () => { Object.assign(P, PDEF); commit(); drawHero(); };
  sync();
}
$('#settingsBtn').onclick = settingsModal;

/* ================================================================ keyboard */
document.addEventListener('keydown', e => {
  const typing = ['INPUT', 'SELECT', 'TEXTAREA'].includes(document.activeElement.tagName);
  if (!typing && (e.key === '/' || (e.ctrlKey && e.key.toLowerCase() === 'k'))) { e.preventDefault(); if (S.open) goBack(); $('#q').focus(); }
  if (e.altKey && e.key === 'ArrowLeft') { e.preventDefault(); histMove(-1); } if (e.altKey && e.key === 'ArrowRight') { e.preventDefault(); histMove(1); }
  if (!typing && e.key === 'Backspace') { e.preventDefault(); histMove(-1); }
  if (!typing && S.open && e.key === 'ArrowLeft' && !e.altKey) step(-1); if (!typing && S.open && e.key === 'ArrowRight' && !e.altKey) step(1);
  if (!typing && S.open && e.key.toLowerCase() === 'e') reveal(S.open);
  if (!typing && S.open && e.key.toLowerCase() === 'm') { S.muted = !S.muted; saveVol(); applyVol(); }
  if (!typing && S.open && e.key === ' ' && $('#stage video, #stage audio')) { e.preventDefault(); const m = $('#stage video, #stage audio'); m.paused ? m.play() : m.pause(); }
  if (e.key === 'Escape') { if (!$('#modal').hidden) closeModal(); else if (!$('#ctx').hidden) $('#ctx').hidden = true; else if (S.open) goBack(); else if (S.selected.size) { S.selected.clear(); refreshSel(); } else if (typing) document.activeElement.blur(); }
});

/* ================================================================ selection bar */
function updateBar() { const n = S.selected.size; $('#floatbar').hidden = n === 0; $('#dock').hidden = n > 0 || !S.files.length; $('#selCount').textContent = `${n} selected`; $('#removeFromProj').hidden = S.view !== 'project'; }
$('#selNone').onclick = () => { S.selected.clear(); refreshSel(); };
$('#selAll').onclick = () => { (S.open ? F.files : S.files).forEach(f => S.selected.add(f.id)); refreshSel(); };
$('#addToProj').onclick = async () => { const pid = $('#projSelect').value; if (!pid) return toast('Create a space first — press + next to Spaces', 'x', true); await api(`/projects/${pid}/add`, { method: 'POST', body: { file_ids: [...S.selected] } }); toast(`Added ${S.selected.size} files to the space`); S.selected.clear(); await refreshAll(); };
$('#newFromSel').onclick = async () => { const name = prompt('Name your new space:'); if (!name) return; await api('/projects', { method: 'POST', body: { name, file_ids: [...S.selected] } }); S.selected.clear(); await refreshAll(); toast('Space created'); };
$('#removeFromProj').onclick = async () => { await api(`/projects/${S.project}/remove`, { method: 'POST', body: { file_ids: [...S.selected] } }); S.selected.clear(); await refreshAll(); };
$('#renameSel').onclick = () => {
  const ids = [...S.selected], proj = S.projects.find(p => p.id === S.project)?.name || '';
  modal(`<div class="mh"><h3>Rename ${ids.length} files</h3><p>This only drafts new names. Nothing on disk changes until you commit.</p></div>
    <div class="mb"><p class="hint">Tokens: <code>{name}</code> original · <code>{project}</code> · <code>{n}</code> or <code>{n:3}</code> counter · <code>{date}</code> modified date. Extensions are kept.</p>
    <div class="line"><input id="pat" style="flex:1" value="${esc(proj ? '{project}_{n:3}' : '{date}_{name}')}"></div>
    <div class="line"><span class="k">Project label</span><input id="plab" value="${esc(proj)}"><span class="hint">Start at</span><input id="pst" type="number" value="1" style="width:72px"></div>
    <div id="pv" class="plan"></div><div class="line"><button class="btn dark" id="ok">Save names</button><button class="btn" id="clr">Clear names</button><button class="btn" id="cancel">Cancel</button></div></div>`);
  const prev = () => { const sel = (S.open ? F.files : S.files).filter(f => S.selected.has(f.id)).slice(0, 6); let n = +$('#pst').value;
    $('#pv').textContent = sel.map(f => { const stem = f.name.replace(/\.[^.]*$/, ''); const out = $('#pat').value.replace('{name}', stem).replace('{project}', $('#plab').value).replace('{date}', new Date(f.mtime * 1000).toISOString().slice(0, 10)).replace(/\{n(?::(\d+))?\}/, (_, w) => String(n).padStart(+w || 1, '0')); n++; return `${f.name}\n   → ${out}${f.ext}`; }).join('\n'); };
  ['pat', 'plab', 'pst'].forEach(i => $('#' + i).oninput = prev); prev();
  $('#ok').onclick = async () => { await api('/names/pattern', { method: 'POST', body: { file_ids: ids, pattern: $('#pat').value, project: $('#plab').value, start: +$('#pst').value } }); closeModal(); S.selected.clear(); await loadStats(); S.animNext = false; loadFiles(true, true); toast('Names drafted'); };
  $('#clr').onclick = async () => { await api('/names/clear', { method: 'POST', body: { file_ids: ids } }); closeModal(); await loadStats(); loadFiles(true, true); }; $('#cancel').onclick = closeModal;
};

/* ================================================================ group by content / by file name */
function reviewGroups(title, intro, groups, nameOf, extraBtn) {
  modal(`<div class="mh"><h3>${title}</h3><p>${intro}</p></div><div class="mb">${groups.map((g, i) => `<div class="cl" data-i="${i}"><div class="line" style="margin:0"><input type="checkbox" checked class="use"><input class="nm" value="${esc(nameOf(g))}"><span class="hint">${g.count} files</span>${extraBtn ? `<button class="btn sm" data-view="${i}">View</button>` : ''}</div>
    <div class="strip">${g.sample.map(f => f.kind === 'audio' ? `<span title="${esc(f.name)}">${kicon('audio', 20)}</span>` : `<img src="/api/thumb/${f.id}" title="${esc(f.name)}">`).join('')}</div></div>`).join('')}
    <div class="line"><button class="btn dark" id="accept">${ic('check')}Create selected spaces</button><button class="btn" id="cancel">Cancel</button></div></div>`, { w: 480 });
  hydrate($('#modalBox')); $('#cancel').onclick = closeModal;
  $$('[data-view]', $('#modalBox')).forEach(b => b.onclick = () => { const g = groups[+b.dataset.view]; closeModal(); go({ pattern: g.pattern }); });
  $('#accept').onclick = async () => { let n = 0; for (const el of $$('.cl')) { if (!el.querySelector('.use').checked) continue; const g = groups[+el.dataset.i];
    await api('/projects', { method: 'POST', body: { name: el.querySelector('.nm').value || nameOf(g), file_ids: g.file_ids } }); n++; } closeModal(); await refreshAll(); toast(`Created ${n} spaces`); };
}
function autoGroup() {
  modal(`<div class="mh"><h3>Find the patterns</h3><p>Groups files that look or sound alike, across images, video and audio. Rename them, untick the ones you don't want. Nothing touches your disk.</p></div>
    <div class="mb"><div class="line"><span class="hint">Tighter</span><input id="loose" type="range" min="0.1" max="0.9" step="0.05" value="0.5" style="flex:1"><span class="hint">Broader</span></div><div class="line"><button class="btn dark" id="go">${ic('sparkle')}Find groups</button></div>
    <label class="hint"><input type="checkbox" id="unas" checked> Only files not already in a space</label><div id="clres"></div></div>`, { w: 480 });
  $('#go').onclick = async () => {
    $('#clres').innerHTML = '<p class="hint">Reading the shape of your files…</p>';
    const r = await api('/cluster', { method: 'POST', body: { root: S.root, looseness: +$('#loose').value, unassigned: $('#unas').checked } });
    if (!r.clusters.length) return $('#clres').innerHTML = '<p class="hint">No groups found — slide towards Broader, or read more files first.</p>';
    reviewGroups('Find the patterns', `${r.clusters.length} groups · ${r.leftover} files left ungrouped. Rename, untick, then create.`, r.clusters, g => g.name, false);
  };
}
async function nameGroups() {
  modal(`<div class="mh"><h3>Group by file name</h3><p>Looking for names that share a shape — like <code>ty (12)</code>, <code>tea chai india…</code> or <code>cinematic film still…</code>. Files from known tools (Midjourney, ChatGPT…) are already under Sources.</p></div><div class="mb"><p class="hint">Reading names…</p></div>`);
  const r = await api('/name_groups');
  if (!r.groups.length) return $('#modalBox .mb').innerHTML = '<p class="hint">No shared name patterns found.</p>';
  const nice = p => p.replace(/\{id\}/g, 'id').replace(/#/g, '').replace(/\s+/g, ' ').trim().replace(/^./, c => c.toUpperCase()) || 'Untitled pattern';
  reviewGroups('Group by file name', `${r.groups.length} name patterns found among files not claimed by a known tool. “View” shows the group in the library.`, r.groups, g => nice(g.pattern) + ' (names)', true);
}
$('#autoGroup').onclick = autoGroup; $('#autoTop').onclick = autoGroup; $('#nameGroups').onclick = nameGroups;

/* ================================================================ commit + history */
$('#commitBtn').onclick = () => {
  const defRoot = S.roots.find(r => r.id === S.root)?.path || S.roots[0]?.path || '';
  modal(`<div class="mh"><h3>Make it real</h3><p>The only step that changes files on your PC. You'll see every move first, and you can undo it afterwards.</p></div>
    <div class="mb"><div class="line"><span class="k">Destination</span><input id="dest" style="flex:1" value="${esc(defRoot ? defRoot + '\\Spaces' : '')}"></div>
    <label><input type="checkbox" id="doMove" checked> Move each space's files into <code>destination\\SpaceName\\</code></label><br><label><input type="checkbox" id="doRen" checked> Apply drafted new names</label>
    <div class="line"><span class="k">Mode</span><select id="mode"><option value="move">Move (undo available)</option><option value="copy">Copy (originals stay put)</option></select></div>
    <div class="line"><button id="prev" class="btn dark">${ic('eye')}Preview plan</button></div><div id="planOut"></div></div>`, { w: 520 });
  $('#prev').onclick = async () => {
    const body = { dest_root: $('#dest').value.trim(), move_into_projects: $('#doMove').checked, apply_renames: $('#doRen').checked };
    const p = await api('/commit/plan', { method: 'POST', body });
    if (!p.ops.length) return $('#planOut').innerHTML = '<p class="hint">Nothing to do yet — put files in spaces or draft new names first.</p>';
    $('#planOut').innerHTML = `<p><b>${p.counts.move}</b> moves · <b>${p.counts.rename}</b> renames in place</p>${p.warnings.map(w => `<div class="warn">${esc(w)}</div>`).join('')}
      <div class="plan">${p.ops.slice(0, 400).map(o => `[${o.kind}] ${o.old}\n      → ${o.new}`).map(esc).join('\n')}${p.ops.length > 400 ? `\n… and ${p.ops.length - 400} more` : ''}</div>
      <div class="line"><button class="btn orange" id="run">Commit ${p.ops.length} changes</button></div>`;
    $('#run').onclick = async () => {
      if (!confirm(`Apply ${p.ops.length} changes to your real files?\nYou can undo from History.`)) return;
      const r = await api('/commit/run', { method: 'POST', body: { ...body, mode: $('#mode').value } });
      $('#planOut').innerHTML = `<p class="good">Done — ${r.done} applied${r.failed ? `, <span class="warn">${r.failed} failed</span>` : ''}. Saved as #${r.commit_id}.</p>` + (r.errors.length ? `<div class="plan">${r.errors.map(esc).join('\n')}</div>` : ''); refreshAll();
    };
  }; hydrate($('#modalBox'));
};
$('#historyBtn').onclick = async () => {
  const cs = await api('/commits');
  modal(`<div class="mh"><h3>History</h3><p>Every real change is journaled. Undo puts files back exactly where they were.</p></div><div class="mb">` +
    (cs.map(c => `<div class="line"><span style="flex:1">#${c.id} · ${new Date(c.ts * 1000).toLocaleString()} · ${esc(c.summary)}</span>${c.undone ? '<span class="hint">undone</span>' : `<button class="btn" data-undo="${c.id}">Undo</button>`}</div>`).join('') || '<p class="hint">No commits yet.</p>') + `<div class="line"><button class="btn" id="cancel">Close</button></div></div>`);
  $('#cancel').onclick = closeModal;
  $('#modalBox').onclick = async e => { const id = e.target.dataset.undo; if (!id) return; const r = await api(`/commits/${id}/undo`, { method: 'POST' }); toast(r.error || `Restored ${r.restored} files`); closeModal(); refreshAll(); };
};

/* ================================================================ modal + boot */
/* panels open beside the library (pushing it aside) instead of covering the window */
function modal(html, opt = {}) {
  const m = $('#modal'); m.dataset.kind = opt.kind || ''; m.style.setProperty('--pw', (opt.w || 440) + 'px');
  $('#modalBox').innerHTML = html; $('#modalBox').onclick = null; m.hidden = false; m.scrollTop = 0; hydrate(m);
}
function closeModal() { $('#modal').hidden = true; $('#modal').dataset.kind = ''; }
$('#pclose').onclick = closeModal;
async function refreshAll() { await loadRoots(); await loadProjects(); await loadStats(); await loadFiles(true, true); setNav(); }
hydrate();
(async () => { await firstRun(); S.known = await api('/known_folders').catch(() => []); await refreshAll(); record(); poll(); })();

/* ---- first run: the AI model is fetched once, then File Mind never needs the internet again */
async function firstRun() {
  let m = await api('/model').catch(() => ({ ready: true })); if (m.ready) return;
  const gb = n => (n / 1e9).toFixed(2) + ' GB';
  const el = document.body.appendChild(Object.assign(document.createElement('div'), { id: 'setup' }));
  el.innerHTML = `<div class="card"><span class="mark"></span><h1 class="d-head">Getting File Mind ready</h1>
    <p>File Mind uses Google's EmbeddingGemma model to understand your photos, videos and sounds.
    It downloads once — about 1.5 GB — and from then on everything runs on this computer, offline. Your files are never uploaded.</p>
    <div class="bar"><i></i></div><div class="surow"><span id="suLeft">Starting…</span><span id="suRight"></span></div>
    <button class="btn dark" id="suRetry" hidden>${ic('rotate-cw')}<span>Try again</span></button></div>`;
  let last = null;
  const start = () => { $('#suRetry').hidden = true; api('/model/download', { method: 'POST' }).catch(() => { }); };
  $('#suRetry').onclick = start; start();
  while (true) {
    await new Promise(r => setTimeout(r, 700));
    m = await api('/model').catch(() => m);
    if (m.ready) break;
    const pct = m.total ? m.done / m.total : 0, now = performance.now();
    el.querySelector('.bar i').style.width = (pct * 100).toFixed(1) + '%';
    if (m.error) { $('#suLeft').textContent = 'The download stopped. Check your internet connection and try again — it continues where it left off.'; $('#suRight').textContent = ''; $('#suRetry').hidden = false; continue; }
    if (m.total) {
      const speed = last ? (m.done - last.done) / ((now - last.t) / 1000) : 0; last = { done: m.done, t: now };
      $('#suLeft').textContent = `${gb(m.done)} of ${gb(m.total)}`;
      $('#suRight').textContent = speed > 1e5 ? `${(speed / 1e6).toFixed(1)} MB/s · about ${Math.max(1, Math.round((m.total - m.done) / speed / 60))} min left` : '';
    }
  }
  el.querySelector('.bar i').style.width = '100%'; $('#suLeft').textContent = 'Ready'; $('#suRight').textContent = '';
  await new Promise(r => setTimeout(r, 500)); el.classList.add('out'); setTimeout(() => el.remove(), 400);
}

/* ================================================================ motion
   Opening a file: the tile you clicked flies into the stage while everything around it
   is pushed outward and fades, as if the camera dives in. Going back reverses it — the
   file lands in its old slot and the grid gathers around it. Changing tile size or layout
   glides every visible tile to its new place. Only transforms and opacity move. */
const reduced = matchMedia('(prefers-reduced-motion: reduce)'), calm = { get matches() { return reduced.matches || document.hidden; } };
const DIVE = 'cubic-bezier(.32,.72,0,1)', OUT = 'cubic-bezier(.2,.8,.2,1)';
const onScreen = r => r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth && r.width > 0;
const settle = (a, ms, fn) => { let done = false; const f = () => { if (!done) { done = true; fn(); } }; a.onfinish = f; setTimeout(f, ms + 250); };
const fx = () => $('#fx') || document.body.appendChild(Object.assign(document.createElement('div'), { id: 'fx' }));
function ghost(src, r, rad) {
  const g = document.createElement('div'); g.className = 'ghost';
  g.style.cssText = `left:${r.left}px;top:${r.top}px;width:${r.width}px;height:${r.height}px;border-radius:${rad}px`;
  if (src) g.innerHTML = `<img src="${src}" alt="">`; return g;
}
function grabTile(id) {
  if (calm.matches) return null;
  const box = $('#browse').hidden ? '#feed' : '#grid', t = $(`${box} .tile[data-id="${id}"]`), im = t?.querySelector('img.im');
  if (!t || !im?.naturalWidth || t.dataset.kind === 'audio') return null;
  const r = t.getBoundingClientRect(); if (!onScreen(r)) return null;
  const others = [];
  for (const o of $$(`${box} .tile`)) {             // everything else that is visible gets scattered
    if (o === t || others.length > 90) continue;
    const q = o.getBoundingClientRect(); if (!onScreen(q)) continue;
    const oi = o.querySelector('img.im'); others.push({ r: q, src: oi?.naturalWidth ? oi.src : '', bg: getComputedStyle(o).backgroundColor });
  }
  const c = (box === '#grid' ? $('#scroller') : $('#viewer')).getBoundingClientRect();
  return { id, r, src: im.src, kind: t.dataset.kind, ar: r.width / r.height, others, clip: `inset(${c.top}px ${innerWidth - c.right}px ${innerHeight - c.bottom}px ${c.left}px)` };
}
function morphIn(from, media) {
  const layer = fx(), rad = parseFloat(getComputedStyle(document.body).getPropertyValue('--rad')) || 10;
  const cx = from.r.left + from.r.width / 2, cy = from.r.top + from.r.height / 2;
  const scat = layer.appendChild(Object.assign(document.createElement('div'), { className: 'scat' }));   // kept inside the grid area
  scat.style.clipPath = from.clip; setTimeout(() => scat.remove(), 900);
  for (const o of from.others) {
    const g = ghost(o.src, o.r, rad); g.style.background = o.bg; scat.appendChild(g);
    const dx = o.r.left + o.r.width / 2 - cx, dy = o.r.top + o.r.height / 2 - cy, d = Math.hypot(dx, dy);
    g.animate([{ transform: 'none', opacity: 1 }, { transform: `translate(${dx * .55}px,${dy * .55}px) scale(${1.25 + d / 4000})`, opacity: 0 }],
      { duration: 460, easing: OUT, delay: Math.min(d / 30, 60), fill: 'forwards' }); settle(g.getAnimations()[0], 520, () => g.remove());
  }
  if (!media) return;
  const to = media.getBoundingClientRect(); if (!to.width) return;
  const g = ghost(from.src, from.r, rad); g.classList.add('flyer'); layer.appendChild(g);
  media.classList.add('arriving');
  const a = g.animate([
    { left: from.r.left + 'px', top: from.r.top + 'px', width: from.r.width + 'px', height: from.r.height + 'px', borderRadius: rad + 'px' },
    { left: to.left + 'px', top: to.top + 'px', width: to.width + 'px', height: to.height + 'px', borderRadius: '6px' }], { duration: 560, easing: DIVE, fill: 'forwards' });
  const ready = media.tagName === 'IMG' ? (media.complete ? Promise.resolve() : media.decode().catch(() => { })) : Promise.resolve();
  const wait = ms => new Promise(r => setTimeout(r, ms));
  Promise.all([Promise.race([a.finished, wait(800)]), Promise.race([ready, wait(1500)])]).then(() => {
    media.classList.remove('arriving');
    settle(g.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 160, fill: 'forwards' }), 160, () => g.remove());
  });
}
function grabStage() {
  if (calm.matches || !S.open) return null;
  const m = $('#stage img, #stage video'); if (!m) return null;
  const r = m.getBoundingClientRect(); if (!onScreen(r)) return null;
  return { id: S.open, r, src: `/api/thumb/${S.open}` };
}
function morphOut(home) {
  const t = $(`#grid .tile[data-id="${home.id}"]`);
  if (!t) return gather(null);
  let r = t.getBoundingClientRect();
  if (!onScreen(r) || r.top < 60 || r.bottom > innerHeight - 40) { t.scrollIntoView({ block: 'center' }); r = t.getBoundingClientRect(); }
  const rad = parseFloat(getComputedStyle(t).borderRadius) || 10, g = ghost(home.src, home.r, 6); g.classList.add('flyer');
  fx().appendChild(g); t.style.visibility = 'hidden';
  g.animate([
    { left: home.r.left + 'px', top: home.r.top + 'px', width: home.r.width + 'px', height: home.r.height + 'px', borderRadius: '6px' },
    { left: r.left + 'px', top: r.top + 'px', width: r.width + 'px', height: r.height + 'px', borderRadius: rad + 'px' }], { duration: 520, easing: DIVE, fill: 'forwards' });
  settle(g.getAnimations()[0], 520, () => { t.style.visibility = ''; g.remove(); });
  gather(t, r);
}
function gather(center, cr) {
  const cx = cr ? cr.left + cr.width / 2 : innerWidth / 2, cy = cr ? cr.top + cr.height / 2 : innerHeight / 2;
  let n = 0;
  for (const o of $$('#grid .tile')) {
    if (o === center) continue;
    const q = o.getBoundingClientRect(); if (!onScreen(q)) continue; if (++n > 120) break;
    const dx = q.left + q.width / 2 - cx, dy = q.top + q.height / 2 - cy, d = Math.hypot(dx, dy);
    o.animate([{ transform: `translate(${dx * .4}px,${dy * .4}px) scale(1.18)`, opacity: 0 }, { transform: 'none', opacity: 1 }],
      { duration: 520, easing: DIVE, delay: 40 + Math.min(d / 20, 90), fill: 'backwards' });
  }
}
/* FLIP: remember where every visible tile was, redraw, then slide each one from old to new */
function glide(redraw) {
  if (calm.matches) return redraw();
  const before = new Map();
  for (const o of $$('#grid .tile')) { const q = o.getBoundingClientRect(); if (onScreen(q)) before.set(o.dataset.id, q); if (before.size > 150) break; }
  redraw();
  if (!before.size) return;
  for (const o of $$('#grid .tile')) {
    const a = before.get(o.dataset.id); if (!a) continue;
    const b = o.getBoundingClientRect(); if (!b.width) continue;
    const dx = a.left - b.left, dy = a.top - b.top, sx = a.width / b.width, sy = a.height / b.height;
    if (Math.abs(dx) < 1 && Math.abs(dy) < 1 && Math.abs(sx - 1) < .01 && Math.abs(sy - 1) < .01) continue;
    o.querySelector('img.im')?.classList.add('in');
    o.animate([{ transformOrigin: '0 0', transform: `translate(${dx}px,${dy}px) scale(${sx},${sy})` }, { transformOrigin: '0 0', transform: 'none' }], { duration: 420, easing: DIVE });
  }
}

/* ================================================================ voice search
   The recording itself goes to EmbeddingGemma, which understands speech directly and puts it
   in the same space as typed words — no transcription, nothing leaves this PC. */
const V = { rec: null, chunks: [], stream: null, timer: null, ctx: null, raf: 0 };
$('#micBtn').innerHTML = ic('mic');
async function startVoice() {
  try { V.stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } }); }
  catch { return toast('Microphone is blocked — allow it in the address bar to search by voice', 'mic', true); }
  V.chunks = []; V.rec = new MediaRecorder(V.stream); V.rec.ondataavailable = e => V.chunks.push(e.data); V.rec.onstop = sendVoice; V.rec.start();
  const b = $('#micBtn'); b.classList.add('on'); b.title = 'Listening… click to search';
  $('#q').placeholder = 'Listening… say what you are looking for';
  V.ctx = new AudioContext(); const an = V.ctx.createAnalyser(); an.fftSize = 512; V.ctx.createMediaStreamSource(V.stream).connect(an);
  const buf = new Uint8Array(an.fftSize); let quiet = 0, heard = false, last = performance.now();
  const tick = now => {                      // the ring follows your voice; a pause after speaking stops it
    an.getByteTimeDomainData(buf); let peak = 0; for (const v of buf) peak = Math.max(peak, Math.abs(v - 128));
    const lvl = Math.min(1, peak / 60); b.style.setProperty('--lvl', lvl.toFixed(2));
    if (lvl > .18) { heard = true; quiet = 0; } else if (heard) quiet += now - last; last = now;
    if (heard && quiet > 1300) return stopVoice();
    V.raf = requestAnimationFrame(tick);
  };
  V.raf = requestAnimationFrame(tick); V.timer = setTimeout(stopVoice, 15000);
}
function stopVoice() {
  cancelAnimationFrame(V.raf); clearTimeout(V.timer);
  if (V.rec?.state === 'recording') V.rec.stop();
  V.stream?.getTracks().forEach(t => t.stop()); V.ctx?.close().catch(() => { }); V.ctx = null;
  const b = $('#micBtn'); b.classList.remove('on'); b.classList.add('busy'); b.style.removeProperty('--lvl'); b.title = 'Search by voice';
}
async function sendVoice() {
  const fd = new FormData(); fd.append('file', new Blob(V.chunks, { type: V.rec.mimeType || 'audio/webm' }), 'voice.webm'); V.rec = null;
  try {
    const r = await api('/voice', { method: 'POST', body: fd });
    addTag({ q: 'voice:' + r.id, label: r.hint.slice(0, 2).join(' · '), voice: true });
  } catch (e) { toast(e.message, 'mic', true); }
  finally { $('#micBtn').classList.remove('busy'); drawBar(); }
}
$('#micBtn').onclick = () => V.rec ? stopVoice() : startVoice();
