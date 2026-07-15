// ============================================================
// AVIATES VOICE — dispatch radio push-to-talk audio
//
// Owns its own WebSocket to the /voice route on the same
// Cloudflare Worker used for the text dispatch protocol. Chunks
// are captured via MediaRecorder (audio/webm;codecs=opus) and
// prefixed with a 1-byte-length sender-id envelope so a listener
// receiving overlapping transmissions can demultiplex them into
// separate playback buffers. See
// docs/superpowers/specs/2026-07-14-dispatch-radio-phase2-voice-design.md.
//
// Each transmission is buffered in full (not streamed via
// MediaSource) and played back as a single decoded clip once the
// sender's voice_end marker arrives — simpler and more robust
// than segment-by-segment streaming for short PTT bursts, at the
// cost of a small added latency most listeners won't notice.
// ============================================================

(function () {
  const CLEAR_NM = 200;
  const DEGRADED_NM = 400;
  const NOISE_GAIN_CEILING = 0.35;
  const SESSION_TIMEOUT_MS = 2000;
  const RECONNECT_DELAY_MS = 5000;

  let ws = null;
  let myPilotId = null;
  let myBackendUrl = null;
  let shouldStayConnected = false;
  let reconnectTimer = null;
  let currentDistanceNm = 0;
  let audioCtx = null;
  let mediaStream = null;
  let mediaRecorder = null;

  /** senderId -> { chunks: ArrayBuffer[], timeoutHandle: number|null } */
  const activeSessions = new Map();

  function ensureAudioContext() {
    if (!audioCtx) audioCtx = new AudioContext();
    return audioCtx;
  }

  function encodeVoiceFrame(senderId, chunkArrayBuffer) {
    const idBytes = new TextEncoder().encode(senderId);
    const out = new Uint8Array(1 + idBytes.length + chunkArrayBuffer.byteLength);
    out[0] = idBytes.length;
    out.set(idBytes, 1);
    out.set(new Uint8Array(chunkArrayBuffer), 1 + idBytes.length);
    return out.buffer;
  }

  function decodeVoiceFrame(arrayBuffer) {
    const bytes = new Uint8Array(arrayBuffer);
    const idLen = bytes[0];
    const senderId = new TextDecoder().decode(bytes.subarray(1, 1 + idLen));
    const chunk = bytes.subarray(1 + idLen).slice().buffer;
    return { senderId, chunk };
  }

  function computeDegradationFactor(distanceNm) {
    if (distanceNm <= CLEAR_NM) return 0;
    if (distanceNm >= DEGRADED_NM) return 1;
    return (distanceNm - CLEAR_NM) / (DEGRADED_NM - CLEAR_NM);
  }

  function getStaticNoiseBuffer(ctx) {
    if (ctx._aviatesNoiseBuffer) return ctx._aviatesNoiseBuffer;
    const length = ctx.sampleRate * 2;
    const buffer = ctx.createBuffer(1, length, ctx.sampleRate);
    const data = buffer.getChannelData(0);
    let last = 0;
    for (let i = 0; i < length; i++) {
      const white = Math.random() * 2 - 1;
      last = (last + 0.15 * white) / 1.15;
      data[i] = last * 3.5;
    }
    ctx._aviatesNoiseBuffer = buffer;
    return buffer;
  }

  function scheduleDropouts(ctx, gainNode, degradationFactor, durationSec) {
    if (degradationFactor <= 0) return;
    const avgIntervalSec = 20 - (17 * degradationFactor);
    let t = 0;
    while (t < durationSec) {
      t += avgIntervalSec * (0.5 + Math.random());
      if (t >= durationSec) break;
      const dipStart = ctx.currentTime + t;
      gainNode.gain.setValueAtTime(1, dipStart);
      gainNode.gain.linearRampToValueAtTime(0.15, dipStart + 0.05);
      gainNode.gain.linearRampToValueAtTime(1, dipStart + 0.15);
    }
  }

  function playDegradedClip(ctx, audioBuffer, degradationFactor) {
    const source = ctx.createBufferSource();
    source.buffer = audioBuffer;

    const bandpass = ctx.createBiquadFilter();
    bandpass.type = 'bandpass';
    bandpass.frequency.value = 1650;
    bandpass.Q.value = 0.7;

    const voiceGain = ctx.createGain();
    voiceGain.gain.value = 1.0;

    const merger = ctx.createGain();

    source.connect(bandpass);
    bandpass.connect(voiceGain);
    voiceGain.connect(merger);

    if (degradationFactor > 0) {
      const noiseGain = ctx.createGain();
      noiseGain.gain.value = degradationFactor * NOISE_GAIN_CEILING;
      const noiseSource = ctx.createBufferSource();
      noiseSource.buffer = getStaticNoiseBuffer(ctx);
      noiseSource.loop = true;
      noiseSource.connect(noiseGain);
      noiseGain.connect(merger);
      noiseSource.start();
      noiseSource.stop(ctx.currentTime + audioBuffer.duration);
    }

    merger.connect(ctx.destination);
    scheduleDropouts(ctx, merger, degradationFactor, audioBuffer.duration);

    source.start();
    source.stop(ctx.currentTime + audioBuffer.duration);
  }

  async function playSession(senderId) {
    const session = activeSessions.get(senderId);
    if (!session || session.chunks.length === 0) return;
    activeSessions.delete(senderId);

    try {
      const blob = new Blob(session.chunks, { type: 'audio/webm;codecs=opus' });
      const arrayBuffer = await blob.arrayBuffer();
      const ctx = ensureAudioContext();
      const audioBuffer = await ctx.decodeAudioData(arrayBuffer);
      playDegradedClip(ctx, audioBuffer, computeDegradationFactor(currentDistanceNm));
    } catch (err) {
      console.error('[aviatesVoice] Failed to decode/play clip from', senderId, err);
    }
  }

  function getOrCreateSession(senderId) {
    let session = activeSessions.get(senderId);
    if (!session) {
      session = { chunks: [], timeoutHandle: null };
      activeSessions.set(senderId, session);
    }
    if (session.timeoutHandle) clearTimeout(session.timeoutHandle);
    session.timeoutHandle = setTimeout(() => playSession(senderId), SESSION_TIMEOUT_MS);
    return session;
  }

  function handleIncomingBinary(arrayBuffer) {
    const { senderId, chunk } = decodeVoiceFrame(arrayBuffer);
    const session = getOrCreateSession(senderId);
    session.chunks.push(chunk);
  }

  function handleIncomingMarker(json) {
    let msg;
    try {
      msg = JSON.parse(json);
    } catch {
      return;
    }
    if (msg.type === 'voice_end' && msg.from) {
      const session = activeSessions.get(msg.from);
      if (session) {
        if (session.timeoutHandle) clearTimeout(session.timeoutHandle);
        playSession(msg.from);
      }
    }
    // voice_start needs no action here — getOrCreateSession() lazily starts
    // a fresh buffer for that sender on the first chunk that follows.
  }

  // Mirrors DispatchRadioService's own 5s reconnect loop for the /pilot
  // text socket — a dropped /voice socket must recover on its own while
  // still tuned in, not wait for the next full tune/untune cycle.
  function openSocket() {
    if (!shouldStayConnected) return;
    ws = new WebSocket(`${myBackendUrl}/voice?pilotId=${encodeURIComponent(myPilotId)}`);
    ws.binaryType = 'arraybuffer';
    ws.onmessage = (event) => {
      if (event.data instanceof ArrayBuffer) handleIncomingBinary(event.data);
      else handleIncomingMarker(event.data);
    };
    ws.onclose = () => {
      ws = null;
      if (shouldStayConnected) {
        reconnectTimer = setTimeout(openSocket, RECONNECT_DELAY_MS);
      }
    };
    ws.onerror = () => { /* onclose always follows onerror for a WebSocket */ };
  }

  window.aviatesVoice = {
    connect(pilotId, backendUrl) {
      if (shouldStayConnected) return;
      myPilotId = pilotId;
      myBackendUrl = backendUrl;
      shouldStayConnected = true;
      openSocket();
    },

    disconnect() {
      shouldStayConnected = false;
      if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
      if (mediaRecorder) this.stopTransmit();
      if (ws) { ws.close(); ws = null; }
      activeSessions.forEach((s) => { if (s.timeoutHandle) clearTimeout(s.timeoutHandle); });
      activeSessions.clear();
    },

    updateDistance(distanceNm) {
      currentDistanceNm = distanceNm;
    },

    async startTransmit() {
      if (!ws || ws.readyState !== WebSocket.OPEN || mediaRecorder) return;
      try {
        mediaStream = await navigator.mediaDevices.getUserMedia({ audio: true });
      } catch (err) {
        console.error('[aviatesVoice] Microphone access denied', err);
        return;
      }
      mediaRecorder = new MediaRecorder(mediaStream, { mimeType: 'audio/webm;codecs=opus' });
      mediaRecorder.ondataavailable = async (event) => {
        if (event.data.size === 0 || !ws || ws.readyState !== WebSocket.OPEN) return;
        const arrayBuffer = await event.data.arrayBuffer();
        ws.send(encodeVoiceFrame(myPilotId, arrayBuffer));
      };
      ws.send(JSON.stringify({ type: 'voice_start', from: myPilotId }));
      mediaRecorder.start(100);
    },

    stopTransmit() {
      if (mediaRecorder) {
        mediaRecorder.stop();
        mediaRecorder = null;
      }
      if (mediaStream) {
        mediaStream.getTracks().forEach((t) => t.stop());
        mediaStream = null;
      }
      if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: 'voice_end', from: myPilotId }));
      }
    },
  };
})();
