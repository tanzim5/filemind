/* Desktop: real shell file drops. Browser: download drag where supported. */
let nativeFileDrag = false, dragGesture = null, dragBusy = false, suppressDragClick = false, browserDragNotice = false;
async function connectFileDrag() {
  try { nativeFileDrag = !!(await window.pywebview?.api.drag_capabilities())?.native_files; }
  catch { nativeFileDrag = false; }
  document.documentElement.classList.toggle('native-file-drag', nativeFileDrag);
  const handle = $('#dragFile');
  if (handle.querySelector) handle.querySelector('span').textContent = nativeFileDrag ? 'Drag file' : 'Drag download';
  handle.title = nativeFileDrag ? 'Drag the original file into another app' : 'Open the desktop app to drag an original into Adobe apps';
}
window.addEventListener('pywebviewready', connectFileDrag);
connectFileDrag();

function dragSource(target, clientY) {
  if (!(target instanceof Element)) return null;
  const handle = target.closest('#dragFile');
  if (handle && S.cur) return { id: S.cur.id, tile: false };
  if (target.closest('button,input,select,a,[data-chk],.wv,audio')) return null;
  const tile = target.closest('.tile[data-id]');
  if (tile) return { id: +tile.dataset.id, tile: true };
  // Leave the video's bottom control strip alone; its picture and the handle can be dragged.
  const media = target.closest('#stage img, #stage video, #stage .audio-player, #stage');
  if (media) {
    if (media.tagName === 'VIDEO' && (clientY == null || clientY >= media.getBoundingClientRect().bottom - 64)) return null;
    if (target.closest('.audio-player button,.audio-player [data-audio-scrub]')) return null;
    return { id: +$('#stage').dataset.id, tile: false };
  }
  return null;
}
document.addEventListener('pointerdown', e => {
  suppressDragClick = false;
  if (!nativeFileDrag || dragBusy || e.button !== 0 || e.pointerType === 'touch') return;
  const source = dragSource(e.target, e.clientY);
  if (source) { e.preventDefault(); dragGesture = { ...source, x: e.clientX, y: e.clientY }; }
}, true);
document.addEventListener('pointermove', e => {
  if (!dragGesture || dragBusy) return;
  if (!(e.buttons & 1)) { dragGesture = null; return; }
  if (Math.hypot(e.clientX - dragGesture.x, e.clientY - dragGesture.y) < 6) return;
  const source = dragGesture; dragGesture = null;
  const ids = source.tile && S.selected.has(source.id) ? [...S.selected] : [source.id];
  e.preventDefault(); stopHover();
  dragBusy = true; suppressDragClick = true;
  document.body.classList.add('dragging-file');
  window.pywebview.api.drag_files(ids).catch(err => toast(err.message || 'Could not drag this file', 'x', true))
    .finally(() => { dragBusy = false; dragGesture = null; document.body.classList.remove('dragging-file'); });
}, { passive: false });
for (const name of ['pointerup', 'pointercancel', 'blur']) window.addEventListener(name, () => { dragGesture = null; });
document.addEventListener('click', e => {
  if (suppressDragClick) { e.preventDefault(); e.stopImmediatePropagation(); suppressDragClick = false; }
}, true);
document.addEventListener('dragstart', e => {
  const source = dragSource(e.target, e.clientY);
  if (!source) return;
  if (nativeFileDrag) { e.preventDefault(); return; }
  if (!browserDragNotice) {
    browserDragNotice = true;
    toast('For Photoshop or Media Encoder, open File Mind in its desktop window. Browser drags can only download files.', 'move');
  }
  const f = S.files.find(f => f.id === source.id) || F.files.find(f => f.id === source.id) || (S.cur?.id === source.id ? S.cur : null);
  if (!f || !e.dataTransfer) { e.preventDefault(); return; }
  // Remove Chromium's default image/video-frame payload. Browser DownloadURL is
  // a fallback; the Windows desktop bridge above supplies the actual shell file.
  e.dataTransfer.clearData();
  const ext = f.ext?.replace(/^\./, '').toLowerCase();
  const mime = ({ mp4: 'video/mp4', mov: 'video/quicktime', webm: 'video/webm', mkv: 'video/x-matroska',
    mp3: 'audio/mpeg', wav: 'audio/wav', m4a: 'audio/mp4', flac: 'audio/flac', ogg: 'audio/ogg',
    png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', webp: 'image/webp', gif: 'image/gif',
    tif: 'image/tiff', tiff: 'image/tiff', svg: 'image/svg+xml' })[ext] || 'application/octet-stream';
  e.dataTransfer.effectAllowed = 'copy';
  e.dataTransfer.setData('DownloadURL', `${mime}:${f.name}:${location.origin}/api/file/${source.id}`);
  e.dataTransfer.setData('text/uri-list', `${location.origin}/api/file/${source.id}`);
}, true);
$('#dragFile').addEventListener('click', () => toast(nativeFileDrag
  ? 'Hold and drag this button into another app'
  : 'Open the File Mind desktop window to drag originals into Adobe apps', 'move'));
