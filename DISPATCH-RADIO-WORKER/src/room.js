import { DurableObject } from 'cloudflare:workers';
import { haversineNm, classify } from './distance.js';
import { SessionRegistry } from './registry.js';

const DISPATCH_LAT = 52.8311;
const DISPATCH_LON = -1.3281;
const CLEAR_RANGE_NM = 200;
const DEGRADED_RANGE_NM = 400;
const STALE_SESSION_MS = 15000;
const SWEEP_INTERVAL_MS = 5000;

export class DispatchRadioRoom extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.ctx = ctx;

    // In-memory state does not survive hibernation. This registry is a
    // rebuildable cache, not a source of truth — rebuildRegistry() below
    // reconstructs it from live hibernatable sockets' attachments on every
    // wake (cold start or resume-from-hibernation both re-run this constructor).
    this.registry = new SessionRegistry();
    this.rebuildRegistry();

    this.ctx.blockConcurrencyWhile(async () => {
      if (this.ctx.getWebSockets('pilot').length > 0) {
        const alarm = await this.ctx.storage.getAlarm();
        if (alarm === null) {
          await this.ctx.storage.setAlarm(Date.now() + SWEEP_INTERVAL_MS);
        }
      }
    });
  }

  rebuildRegistry() {
    for (const ws of this.ctx.getWebSockets('pilot')) {
      const data = ws.deserializeAttachment();
      if (data?.pilotId) {
        this.registry.upsert(data.pilotId, data);
      }
    }
  }

  async fetch(request) {
    const url = new URL(request.url);
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);

    if (url.pathname === '/voice') {
      // The entry Worker already validated the admin key (if present) before
      // forwarding here — see worker.js. A pilot voice link carries pilotId
      // instead, used only to look up that pilot's current registry state
      // when deciding relay eligibility (see relayVoiceFrame below). Uses
      // distinct 'voice-pilot'/'voice-admin' tags, not 'pilot'/'admin' —
      // those tags are reserved for the text-protocol sockets, and
      // rebuildRegistry()/webSocketClose() assume every 'pilot'-tagged
      // socket carries a full registry-record attachment. Tagging a voice
      // socket 'pilot' too would corrupt the registry on rebuild.
      const isDispatcherVoice = url.searchParams.has('key');
      const pilotId = url.searchParams.get('pilotId');
      this.ctx.acceptWebSocket(server, [isDispatcherVoice ? 'voice-admin' : 'voice-pilot']);
      if (!isDispatcherVoice && pilotId) {
        server.serializeAttachment({ pilotId });
      }
      return new Response(null, { status: 101, webSocket: client });
    }

    const isAdmin = url.pathname === '/admin';
    this.ctx.acceptWebSocket(server, [isAdmin ? 'admin' : 'pilot']);

    if (isAdmin) {
      server.send(JSON.stringify({ pilots: this.registry.list() }));
    }

    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws, message) {
    const tags = this.ctx.getTags(ws);

    if (tags.includes('voice-pilot') || tags.includes('voice-admin')) {
      this.relayVoiceFrame(ws, message);
      return;
    }

    if (tags.includes('admin')) return;

    let msg;
    try {
      msg = JSON.parse(typeof message === 'string' ? message : new TextDecoder().decode(message));
    } catch {
      return;
    }
    if (!msg || typeof msg !== 'object' || !msg.pilotId) return;

    const distanceNm = haversineNm(DISPATCH_LAT, DISPATCH_LON, msg.lat, msg.lon);
    const state = classify(distanceNm, CLEAR_RANGE_NM, DEGRADED_RANGE_NM);

    const record = {
      pilotId: msg.pilotId,
      callsign: msg.callsign,
      lat: msg.lat,
      lon: msg.lon,
      freq: msg.freq,
      distanceNm,
      state,
      lastSeen: Date.now(),
    };

    // Persist to the socket itself so a future hibernation/wake cycle can
    // rebuild this pilot's entry (see rebuildRegistry above).
    ws.serializeAttachment(record);
    this.registry.upsert(msg.pilotId, record);

    ws.send(JSON.stringify({ state, distanceNm }));
    this.broadcastToAdmins();
    await this.ensureAlarmScheduled();
  }

  async ensureAlarmScheduled() {
    const existing = await this.ctx.storage.getAlarm();
    if (existing === null) {
      await this.ctx.storage.setAlarm(Date.now() + SWEEP_INTERVAL_MS);
    }
  }

  async webSocketClose(ws) {
    // Only the text-protocol 'pilot' socket owns registry membership. A
    // pilot's separate voice socket closing (e.g. between transmissions,
    // or a brief voice-channel reconnect) must not evict them from the
    // registry while their text socket is still open — otherwise they'd
    // wrongly vanish from the admin's pilot list mid-flight.
    if (!this.ctx.getTags(ws).includes('pilot')) return;

    const data = ws.deserializeAttachment();
    if (data?.pilotId) {
      this.registry.remove(data.pilotId);
      this.broadcastToAdmins();
    }
  }

  async webSocketError(ws) {
    await this.webSocketClose(ws);
  }

  async alarm() {
    this.rebuildRegistry();

    const now = Date.now();
    let removedAny = false;
    for (const ws of this.ctx.getWebSockets('pilot')) {
      const data = ws.deserializeAttachment();
      if (data?.lastSeen && now - data.lastSeen > STALE_SESSION_MS) {
        this.registry.remove(data.pilotId);
        ws.close(1000, 'stale session');
        removedAny = true;
      }
    }
    if (removedAny) this.broadcastToAdmins();

    // Only keep sweeping while pilots are connected. An idle room (nobody
    // tuned in) should be allowed to hibernate rather than being kept awake
    // by our own heartbeat — a new alarm gets armed the next time a pilot's
    // first message arrives (see ensureAlarmScheduled in webSocketMessage).
    const stillHasPilots = this.ctx.getWebSockets('pilot').length > 0;
    if (stillHasPilots) {
      await this.ctx.storage.setAlarm(Date.now() + SWEEP_INTERVAL_MS);
    }
  }

  broadcastToAdmins() {
    const payload = JSON.stringify({ pilots: this.registry.list() });
    for (const ws of this.ctx.getWebSockets('admin')) {
      ws.send(payload);
    }
  }

  // Dumb relay — never inspects audio payload content, only decides who
  // receives it. Binary frames are raw audio chunks (with a sender-id
  // envelope the client encodes/decodes); text frames are voice_start/
  // voice_end speaking-indicator markers. Both use the same eligibility
  // rule: every admin voice socket, plus every pilot voice socket whose
  // current registry state isn't acars_only, excluding the sender.
  relayVoiceFrame(senderWs, message) {
    const isBinary = message instanceof ArrayBuffer;
    const isMarker = !isBinary && typeof message === 'string';
    if (!isBinary && !isMarker) return;

    for (const ws of this.ctx.getWebSockets('voice-admin')) {
      if (ws !== senderWs) ws.send(message);
    }

    for (const ws of this.ctx.getWebSockets('voice-pilot')) {
      if (ws === senderWs) continue;
      const data = ws.deserializeAttachment();
      const entry = data?.pilotId ? this.registry.get(data.pilotId) : undefined;
      if (entry && entry.state !== 'acars_only') {
        ws.send(message);
      }
    }
  }
}
