import { describe, it, expect } from 'vitest';
import { haversineNm, classify } from '../src/distance.js';

describe('haversineNm', () => {
  it('returns 0 for identical points', () => {
    expect(haversineNm(52.8311, -1.3281, 52.8311, -1.3281)).toBe(0);
  });

  it('treats one degree of latitude as ~60nm (definition of a nautical mile)', () => {
    const d = haversineNm(0, 0, 1, 0);
    expect(Math.abs(d - 60)).toBeLessThan(0.5);
  });

  it('treats one degree of longitude at the equator as ~60nm', () => {
    const d = haversineNm(0, 0, 0, 1);
    expect(Math.abs(d - 60)).toBeLessThan(0.5);
  });
});

describe('classify', () => {
  it('buckets distance into clear/degraded/acars_only at the configured thresholds', () => {
    expect(classify(50, 200, 400)).toBe('clear');
    expect(classify(199.9, 200, 400)).toBe('clear');
    expect(classify(200, 200, 400)).toBe('degraded');
    expect(classify(399.9, 200, 400)).toBe('degraded');
    expect(classify(400, 200, 400)).toBe('acars_only');
    expect(classify(1000, 200, 400)).toBe('acars_only');
  });
});
