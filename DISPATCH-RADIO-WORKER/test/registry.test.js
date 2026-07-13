import { describe, it, expect } from 'vitest';
import { SessionRegistry } from '../src/registry.js';

describe('SessionRegistry', () => {
  it('upsert adds a session with lastSeen from the injected clock', () => {
    const registry = new SessionRegistry(() => 1000);
    registry.upsert('pilot1', { callsign: 'AVI123', lat: 1, lon: 2 });
    expect(registry.get('pilot1').lastSeen).toBe(1000);
    expect(registry.get('pilot1').callsign).toBe('AVI123');
  });

  it('sweepStale removes sessions older than maxAgeMs and returns their ids', () => {
    let now = 0;
    const registry = new SessionRegistry(() => now);
    registry.upsert('stale-pilot', { callsign: 'AVI001' });
    now = 20000;
    registry.upsert('fresh-pilot', { callsign: 'AVI002' });

    const removed = registry.sweepStale(15000);

    expect(removed).toEqual(['stale-pilot']);
    expect(registry.get('stale-pilot')).toBeUndefined();
    expect(registry.get('fresh-pilot').callsign).toBe('AVI002');
  });

  it('list returns a plain array suitable for broadcasting to admin sockets', () => {
    const registry = new SessionRegistry();
    registry.upsert('pilot1', { callsign: 'AVI123', distanceNm: 42, state: 'clear' });
    expect(registry.list()).toEqual([
      { pilotId: 'pilot1', callsign: 'AVI123', distanceNm: 42, state: 'clear' },
    ]);
  });
});
