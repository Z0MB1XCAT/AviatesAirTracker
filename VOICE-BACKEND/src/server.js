import { createServer as createHttpServer } from 'node:http';
import { WebSocketServer } from 'ws';
import { haversineNm, classify } from './distance.js';
import { SessionRegistry } from './registry.js';

export function createServer(config) {
  const httpServer = createHttpServer((req, res) => {
    if (req.url === '/health') {
      res.writeHead(200, { 'Content-Type': 'text/plain' });
      res.end('ok');
      return;
    }
    res.writeHead(404);
    res.end();
  });

  const registry = new SessionRegistry();
  const adminSockets = new Set();

  const pilotWss = new WebSocketServer({ noServer: true });
  const adminWss = new WebSocketServer({ noServer: true });

  httpServer.on('upgrade', (req, socket, head) => {
    const url = new URL(req.url, 'http://localhost');
    if (url.pathname === '/pilot') {
      pilotWss.handleUpgrade(req, socket, head, (ws) => pilotWss.emit('connection', ws));
    } else if (url.pathname === '/admin') {
      const keyOk = !config.adminKey || url.searchParams.get('key') === config.adminKey;
      // Note: rejecting a bad key by destroying the raw socket (or by
      // responding with an HTTP error status) before the WebSocket handshake
      // completes makes the `ws` client library emit an 'error' event on the
      // client-side WebSocket instance (this is unconditional in `ws` - see
      // `abortHandshake`/`emitErrorAndClose` in ws/lib/websocket.js). An
      // 'error' event with no listener attached is an uncaught exception in
      // Node, and a caller that only cares about being disconnected (as a
      // rejected admin client typically does - it just listens for 'close')
      // would crash. So instead we let the WebSocket handshake complete
      // normally and then close the connection immediately server-side;
      // that produces a clean 'close' on the client with no 'error'.
      adminWss.handleUpgrade(req, socket, head, (ws) => {
        if (!keyOk) {
          ws.close(1008, 'unauthorized');
          return;
        }
        adminWss.emit('connection', ws);
      });
    } else {
      socket.destroy();
    }
  });

  function broadcastToAdmins() {
    const payload = JSON.stringify({ pilots: registry.list() });
    for (const ws of adminSockets) {
      if (ws.readyState === ws.OPEN) ws.send(payload);
    }
  }

  pilotWss.on('connection', (ws) => {
    let pilotId = null;

    ws.on('message', (raw) => {
      let msg;
      try {
        msg = JSON.parse(raw.toString());
      } catch {
        return;
      }
      if (!msg.pilotId) return;

      pilotId = msg.pilotId;

      const distanceNm = haversineNm(config.dispatchLat, config.dispatchLon, msg.lat, msg.lon);
      const state = classify(distanceNm, config.clearRangeNm, config.degradedRangeNm);

      registry.upsert(pilotId, {
        callsign: msg.callsign,
        lat: msg.lat,
        lon: msg.lon,
        freq: msg.freq,
        distanceNm,
        state,
      });

      ws.send(JSON.stringify({ state, distanceNm }));
      broadcastToAdmins();
    });

    ws.on('close', () => {
      if (pilotId) {
        registry.remove(pilotId);
        broadcastToAdmins();
      }
    });
  });

  adminWss.on('connection', (ws) => {
    adminSockets.add(ws);
    // Defer the initial snapshot slightly. If sent synchronously here, it
    // can be flushed to the socket right behind the HTTP 101 handshake
    // response (written moments earlier by ws's handleUpgrade), and on a
    // fast loopback connection the two writes are read by the client as a
    // single chunk: the client's 'open' event and the 'message' event for
    // this snapshot then both fire synchronously in the same read, before
    // any code awaiting 'open' resumes and has a chance to attach its
    // 'message' listener. That listener attaches a tick too late and misses
    // the snapshot, which then hangs forever waiting for a message that
    // already came and went. A same-iteration setImmediate isn't enough
    // separation to avoid this (it can still fire before the client's poll
    // phase has processed the handshake at all); a short setTimeout gives
    // the client a real chance to finish handling 'open' first.
    setTimeout(() => {
      if (ws.readyState === ws.OPEN) {
        ws.send(JSON.stringify({ pilots: registry.list() }));
      }
    }, 10);
    ws.on('close', () => adminSockets.delete(ws));
  });

  const sweepInterval = setInterval(() => {
    const removed = registry.sweepStale(config.staleSessionMs);
    if (removed.length > 0) broadcastToAdmins();
  }, config.sweepIntervalMs);

  function close() {
    clearInterval(sweepInterval);
    httpServer.close();
  }

  return { httpServer, close };
}
