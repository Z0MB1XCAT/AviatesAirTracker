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

function waitForClose(ws) {
  return new Promise((resolve) => ws.once('close', resolve));
}

// Attaches a 'message' listener synchronously (mirroring how the real admin
// client in AviatesAirWebsite assigns ws.onopen/onclose/onmessage back-to-back
// right after `new WebSocket(...)`), and queues parsed messages so that
// awaiting `next()` later still sees messages that arrived in the meantime.
// This avoids a test-only race where a message sent by the server right
// after the handshake completes could arrive before a listener attached
// only after `await waitForOpen(ws)` had a chance to resume.
function queueMessages(ws) {
  const queue = [];
  const waiters = [];
  ws.on('message', (data) => {
    const parsed = JSON.parse(data.toString());
    if (waiters.length > 0) {
      waiters.shift()(parsed);
    } else {
      queue.push(parsed);
    }
  });
  return {
    next() {
      if (queue.length > 0) return Promise.resolve(queue.shift());
      return new Promise((resolve) => waiters.push(resolve));
    },
  };
}

test('pilot connecting at EGNX receives a clear state, and the admin feed reflects it', async () => {
  const { httpServer, close } = createServer(testConfig());
  await new Promise((resolve) => httpServer.listen(0, resolve));
  const port = httpServer.address().port;

  const adminWs = new WebSocket(`ws://localhost:${port}/admin`);
  const adminMessages = queueMessages(adminWs);
  await waitForOpen(adminWs);
  await adminMessages.next(); // initial empty snapshot

  const pilotWs = new WebSocket(`ws://localhost:${port}/pilot`);
  const pilotMessages = queueMessages(pilotWs);
  await waitForOpen(pilotWs);

  const adminUpdatePromise = adminMessages.next();
  pilotWs.send(JSON.stringify({ pilotId: 'p1', callsign: 'AVI123', lat: 52.8311, lon: -1.3281, freq: '123.575' }));

  const pilotReply = await pilotMessages.next();
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

test('a non-object JSON pilot payload (null or a bare number) does not crash the server', async () => {
  const { httpServer, close } = createServer(testConfig());
  await new Promise((resolve) => httpServer.listen(0, resolve));
  const port = httpServer.address().port;

  const pilotWs = new WebSocket(`ws://localhost:${port}/pilot`);
  const pilotMessages = queueMessages(pilotWs);
  await waitForOpen(pilotWs);

  // Neither of these should throw inside the server's 'message' handler.
  pilotWs.send('null');
  pilotWs.send('42');

  // A subsequent well-formed message should still get a normal reply,
  // proving the server (and this connection) survived the bad payloads.
  pilotWs.send(JSON.stringify({ pilotId: 'p2', callsign: 'AVI456', lat: 52.8311, lon: -1.3281, freq: '123.575' }));

  const pilotReply = await pilotMessages.next();
  assert.equal(pilotReply.state, 'clear');
  assert.ok(pilotReply.distanceNm < 1);

  pilotWs.close();
  close();
});
