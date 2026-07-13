import { test } from 'node:test';
import assert from 'node:assert/strict';
import { haversineNm, classify } from './distance.js';

test('haversineNm returns 0 for identical points', () => {
  assert.equal(haversineNm(52.8311, -1.3281, 52.8311, -1.3281), 0);
});

test('haversineNm: one degree of latitude is ~60nm (definition of a nautical mile)', () => {
  const d = haversineNm(0, 0, 1, 0);
  assert.ok(Math.abs(d - 60) < 0.5, `expected ~60nm, got ${d}`);
});

test('haversineNm: one degree of longitude at the equator is ~60nm', () => {
  const d = haversineNm(0, 0, 0, 1);
  assert.ok(Math.abs(d - 60) < 0.5, `expected ~60nm, got ${d}`);
});

test('classify buckets distance into clear/degraded/acars_only at the configured thresholds', () => {
  assert.equal(classify(50, 200, 400), 'clear');
  assert.equal(classify(199.9, 200, 400), 'clear');
  assert.equal(classify(200, 200, 400), 'degraded');
  assert.equal(classify(399.9, 200, 400), 'degraded');
  assert.equal(classify(400, 200, 400), 'acars_only');
  assert.equal(classify(1000, 200, 400), 'acars_only');
});
