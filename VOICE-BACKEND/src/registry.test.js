import { test } from 'node:test';
import assert from 'node:assert/strict';
import { SessionRegistry } from './registry.js';

test('upsert adds a session with lastSeen from the injected clock', () => {
  const registry = new SessionRegistry(() => 1000);
  registry.upsert('pilot1', { callsign: 'AVI123', lat: 1, lon: 2 });
  assert.equal(registry.get('pilot1').lastSeen, 1000);
  assert.equal(registry.get('pilot1').callsign, 'AVI123');
});

test('sweepStale removes sessions older than maxAgeMs and returns their ids', () => {
  let now = 0;
  const registry = new SessionRegistry(() => now);
  registry.upsert('stale-pilot', { callsign: 'AVI001' });
  now = 20000;
  registry.upsert('fresh-pilot', { callsign: 'AVI002' });

  const removed = registry.sweepStale(15000);

  assert.deepEqual(removed, ['stale-pilot']);
  assert.equal(registry.get('stale-pilot'), undefined);
  assert.equal(registry.get('fresh-pilot').callsign, 'AVI002');
});

test('list returns a plain array suitable for broadcasting to admin sockets', () => {
  const registry = new SessionRegistry();
  registry.upsert('pilot1', { callsign: 'AVI123', distanceNm: 42, state: 'clear' });
  assert.deepEqual(registry.list(), [
    { pilotId: 'pilot1', callsign: 'AVI123', distanceNm: 42, state: 'clear' },
  ]);
});
