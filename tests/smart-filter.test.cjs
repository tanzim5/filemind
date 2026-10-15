const test = require('node:test');
const assert = require('node:assert/strict');
const { parseSmartFilter } = require('../app/static/smart-filter.js');

test('resolves resolution, type, and remaining semantic subject', () => {
  assert.deepEqual(parseSmartFilter('show me 4K videos of motorcycles'), {
    filters: { min_pixels: 3840 * 2160 }, kind: 'video', query: 'motorcycles', understood: ['4K or higher', 'Videos']
  });
});

test('combines shape and duration without losing file type', () => {
  const result = parseSmartFilter('vertical clips under 30 seconds');
  assert.equal(result.kind, 'video');
  assert.equal(result.filters.aspect, 'portrait');
  assert.equal(result.filters.max_duration, 30);
  assert.equal(result.query, '');
});

test('converts sizes into bytes', () => {
  const result = parseSmartFilter('images larger than 20 MB');
  assert.equal(result.kind, 'image');
  assert.equal(result.filters.min_size, 20_000_000);
});
