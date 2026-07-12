import { test } from 'node:test';
import assert from 'node:assert/strict';
import WebSocket from 'ws';
import { createServer } from './server.js';

function testConfig(overrides = {}) {
  return {
    dispatchLat: 52.8311,
    dispatchLon: -1.3281,
    clearRangeNm: 200,
    degradedRangeNm: 400,
    staleSessionMs: 15000,
    sweepIntervalMs: 100000, // long enough to not interfere with these tests
    adminKey: '',
    ...overrides,
  };
}

function waitForOpen(ws) {
  return new Promise((resolve) => ws.once('open', resolve));
}

function waitForMessage(ws) {
  return new Promise((resolve) => ws.once('message', (data) => resolve(JSON.parse(data.toString()))));
}

function waitForClose(ws) {
  return new Promise((resolve) => ws.once('close', resolve));
}

test('pilot connecting at EGNX receives a clear state, and the admin feed reflects it', async () => {
  const { httpServer, close } = createServer(testConfig());
  await new Promise((resolve) => httpServer.listen(0, resolve));
  const port = httpServer.address().port;

  const adminWs = new WebSocket(`ws://localhost:${port}/admin`);
  await waitForOpen(adminWs);
  await waitForMessage(adminWs); // initial empty snapshot

  const pilotWs = new WebSocket(`ws://localhost:${port}/pilot`);
  await waitForOpen(pilotWs);

  const adminUpdatePromise = waitForMessage(adminWs);
  pilotWs.send(JSON.stringify({ pilotId: 'p1', callsign: 'AVI123', lat: 52.8311, lon: -1.3281, freq: '123.575' }));

  const pilotReply = await waitForMessage(pilotWs);
  assert.equal(pilotReply.state, 'clear');
  assert.ok(pilotReply.distanceNm < 1);

  const adminUpdate = await adminUpdatePromise;
  assert.equal(adminUpdate.pilots.length, 1);
  assert.equal(adminUpdate.pilots[0].callsign, 'AVI123');
  assert.equal(adminUpdate.pilots[0].state, 'clear');

  pilotWs.close();
  adminWs.close();
  close();
});

test('admin connections without the correct key are rejected when adminKey is configured', async () => {
  const { httpServer, close } = createServer(testConfig({ adminKey: 'secret' }));
  await new Promise((resolve) => httpServer.listen(0, resolve));
  const port = httpServer.address().port;

  const badAdminWs = new WebSocket(`ws://localhost:${port}/admin?key=wrong`);
  await waitForClose(badAdminWs);

  close();
});
