export class SessionRegistry {
  constructor(now = () => Date.now()) {
    this._now = now;
    this._sessions = new Map(); // pilotId -> { callsign, lat, lon, freq, distanceNm, state, lastSeen }
  }

  upsert(pilotId, data) {
    const existing = this._sessions.get(pilotId) || {};
    this._sessions.set(pilotId, { ...existing, ...data, lastSeen: this._now() });
  }

  remove(pilotId) {
    this._sessions.delete(pilotId);
  }

  get(pilotId) {
    return this._sessions.get(pilotId);
  }

  sweepStale(maxAgeMs) {
    const cutoff = this._now() - maxAgeMs;
    const removed = [];
    for (const [pilotId, session] of this._sessions) {
      if (session.lastSeen < cutoff) {
        this._sessions.delete(pilotId);
        removed.push(pilotId);
      }
    }
    return removed;
  }

  list() {
    return Array.from(this._sessions.entries()).map(([pilotId, s]) => ({
      pilotId,
      callsign: s.callsign,
      distanceNm: s.distanceNm,
      state: s.state,
    }));
  }
}
