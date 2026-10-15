/* Turn common media requests into exact, local metadata filters. Remaining words stay a semantic search. */
function parseSmartFilter(input) {
  let text = String(input || '').trim().toLowerCase();
  const filters = {}, understood = [];
  const take = (pattern, apply) => { const match = text.match(pattern); if (match) { apply(match); text = text.replace(match[0], ' '); } };
  take(/\b(?:4k|uhd)\b/i, () => { filters.min_pixels = 3840 * 2160; understood.push('4K or higher'); });
  take(/\b(?:1080p|full\s*hd)\b/i, () => { filters.min_pixels = 1920 * 1080; understood.push('Full HD or higher'); });
  take(/\b(?:60\s*fps|60\s*frames?\s*per\s*second)\b/i, () => { filters.min_fps = 60; understood.push('60 fps or higher'); });
  take(/\b(?:30\s*fps|30\s*frames?\s*per\s*second)\b/i, () => { filters.min_fps = 30; understood.push('30 fps or higher'); });
  take(/\b(?:vertical|portrait)\b/i, () => { filters.aspect = 'portrait'; understood.push('Portrait'); });
  take(/\b(?:horizontal|landscape)\b/i, () => { filters.aspect = 'landscape'; understood.push('Landscape'); });
  take(/\b(?:square)\b/i, () => { filters.aspect = 'square'; understood.push('Square'); });
  take(/\b(?:wide|widescreen)\b/i, () => { filters.aspect = 'wide'; understood.push('Wide'); });
  take(/\b(?:under|shorter than|less than)\s*(\d+(?:\.\d+)?)\s*(seconds?|secs?|minutes?|mins?|hours?|hrs?)\b/i, m => { filters.max_duration = +m[1] * (/^h/i.test(m[2]) ? 3600 : /^m/i.test(m[2]) ? 60 : 1); understood.push('Under ' + m[1] + ' ' + m[2]); });
  take(/\b(?:over|longer than|more than)\s*(\d+(?:\.\d+)?)\s*(seconds?|secs?|minutes?|mins?|hours?|hrs?)\b/i, m => { filters.min_duration = +m[1] * (/^h/i.test(m[2]) ? 3600 : /^m/i.test(m[2]) ? 60 : 1); understood.push('Over ' + m[1] + ' ' + m[2]); });
  take(/\b(?:larger than|over|bigger than)\s*(\d+(?:\.\d+)?)\s*(mb|gb)\b/i, m => { filters.min_size = Math.round(+m[1] * (/^g/i.test(m[2]) ? 1e9 : 1e6)); understood.push('Over ' + m[1] + ' ' + m[2].toUpperCase()); });
  take(/\b(?:smaller than|under|less than)\s*(\d+(?:\.\d+)?)\s*(mb|gb)\b/i, m => { filters.max_size = Math.round(+m[1] * (/^g/i.test(m[2]) ? 1e9 : 1e6)); understood.push('Under ' + m[1] + ' ' + m[2].toUpperCase()); });
  const kindMatch = text.match(/\b(videos?|clips?|footage|images?|photos?|pictures?|audio|songs?|sounds?|tracks?)\b/i);
  const kind = kindMatch ? (/video|clip|footage/.test(kindMatch[0]) ? 'video' : /image|photo|picture/.test(kindMatch[0]) ? 'image' : 'audio') : null;
  if (kindMatch) { text = text.replace(kindMatch[0], ' '); understood.push(kind === 'video' ? 'Videos' : kind === 'image' ? 'Images' : 'Audio'); }
  text = text.replace(/\b(?:show me|find me|find|show|only|files?|of|with|that|are|the|my|all)\b/g, ' ').replace(/\s+/g, ' ').trim();
  return { filters, kind, query: text, understood };
}
if (typeof module !== 'undefined') module.exports = { parseSmartFilter };
