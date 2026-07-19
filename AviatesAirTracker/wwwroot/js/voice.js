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
  let starting = false;
  let pendingStop = false;
  let selectedMicId = '';
  let selectedSpeakerId = '';

  /** senderId -> { chunks: ArrayBuffer[], timeoutHandle: number|null } */
  const activeSessions = new Map();

  // AudioContext created outside a direct user gesture (e.g. reactively,
  // when the first incoming voice frame arrives) can start — or drift back
  // into — a 'suspended' state under browser/WebView2 autoplay policy.
  // source.start() on a suspended context produces no audible output and
  // no error, so this is easy to mistake for a relay/permission bug rather
  // than what it actually is. Always attempt resume() before playback.
  async function ensureAudioContext() {
    if (!audioCtx) audioCtx = new AudioContext();
    if (audioCtx.state === 'suspended') {
      try {
        await audioCtx.resume();
      } catch (err) {
        console.error('[aviatesVoice] Failed to resume AudioContext', err);
      }
    }
    console.log('[aviatesVoice] AudioContext state:', audioCtx.state);
    return audioCtx;
  }

  // AudioContext.setSinkId() is a recent Web Audio API addition —
  // feature-detected since it may not exist on every WebView2 runtime.
  async function applySinkId(ctx, speakerId) {
    if (!speakerId || typeof ctx.setSinkId !== 'function') return;
    try {
      await ctx.setSinkId(speakerId);
    } catch (err) {
      console.error('[aviatesVoice] Failed to set output device', err);
    }
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
      const ctx = await ensureAudioContext();
      if (selectedSpeakerId) await applySinkId(ctx, selectedSpeakerId);
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
    const url = `${myBackendUrl}/voice?pilotId=${encodeURIComponent(myPilotId)}`;
    console.log('[aviatesVoice] Connecting to', url);
    const socket = new WebSocket(url);
    socket.binaryType = 'arraybuffer';
    socket.onopen = () => {
      console.log('[aviatesVoice] Voice socket OPEN, pilotId=', myPilotId);
    };
    socket.onmessage = (event) => {
      if (event.data instanceof ArrayBuffer) {
        console.log('[aviatesVoice] Received binary frame, bytes=', event.data.byteLength);
        handleIncomingBinary(event.data);
      } else {
        console.log('[aviatesVoice] Received marker frame:', event.data);
        handleIncomingMarker(event.data);
      }
    };
    socket.onclose = (event) => {
      console.log('[aviatesVoice] Voice socket CLOSED, code=', event.code, 'reason=', event.reason);
      // Guard against a stale handler from a socket that was already
      // superseded by a later connect() nulling out a NEWER live socket.
      if (ws === socket) ws = null;
      if (shouldStayConnected) {
        reconnectTimer = setTimeout(openSocket, RECONNECT_DELAY_MS);
      }
    };
    socket.onerror = (event) => {
      console.error('[aviatesVoice] Voice socket ERROR', event);
      /* onclose always follows onerror for a WebSocket */
    };
    ws = socket;
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

    setMicrophone(deviceId) {
      selectedMicId = deviceId || '';
    },

    setSpeaker(deviceId) {
      selectedSpeakerId = deviceId || '';
      if (audioCtx) applySinkId(audioCtx, selectedSpeakerId);
    },

    // Device labels are blank until mic permission has been granted at
    // least once (browser privacy protection). This app auto-approves mic
    // access via WebView2's CoreWebView2.PermissionRequested handler, but
    // that only fires in response to an actual getUserMedia() call — prime
    // it here (silently, briefly) so labels are populated the first time
    // Settings asks for the device list, not just after the first real
    // PTT transmission.
    async getAudioDevices() {
      try {
        const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
        stream.getTracks().forEach((t) => t.stop());
      } catch (err) {
        // Permission not available — device labels below may be blank, that's fine.
      }
      try {
        const devices = await navigator.mediaDevices.enumerateDevices();
        return {
          inputs: devices
            .filter((d) => d.kind === 'audioinput')
            .map((d, i) => ({ deviceId: d.deviceId, label: d.label || `Microphone ${i + 1}` })),
          outputs: devices
            .filter((d) => d.kind === 'audiooutput')
            .map((d, i) => ({ deviceId: d.deviceId, label: d.label || `Speaker ${i + 1}` })),
        };
      } catch (err) {
        console.error('[aviatesVoice] Failed to enumerate audio devices', err);
        return { inputs: [], outputs: [] };
      }
    },

    async startTransmit() {
      if (!ws || ws.readyState !== WebSocket.OPEN || mediaRecorder || starting) {
        console.log(
          '[aviatesVoice] startTransmit() ignored — ws=', !!ws,
          'readyState=', ws ? ws.readyState : 'n/a',
          'mediaRecorder=', !!mediaRecorder, 'starting=', starting
        );
        return;
      }
      console.log('[aviatesVoice] startTransmit() begin, pilotId=', myPilotId);
      starting = true;
      try {
        mediaStream = await navigator.mediaDevices.getUserMedia({
          audio: selectedMicId ? { deviceId: { exact: selectedMicId } } : true,
        });
      } catch (err) {
        console.error('[aviatesVoice] Microphone access denied', err);
        starting = false;
        pendingStop = false;
        return;
      }

      // Everything below can throw (unsupported mimeType, ws having closed
      // during the permission prompt, etc.) — all of it must go through
      // finally so `starting` never gets stuck true and permanently
      // no-ops every future startTransmit() call.
      try {
        if (!ws || ws.readyState !== WebSocket.OPEN) {
          // Socket dropped while the (potentially long) permission prompt
          // was pending — abandon this transmit attempt cleanly. Nothing
          // was ever started, so a pending stop request has nothing left
          // to do — clear it rather than letting it leak into (and wrongly
          // auto-stop) the next, unrelated transmit attempt.
          pendingStop = false;
          mediaStream.getTracks().forEach((t) => t.stop());
          mediaStream = null;
          return;
        }

        mediaRecorder = new MediaRecorder(mediaStream, { mimeType: 'audio/webm;codecs=opus' });
        console.log('[aviatesVoice] MediaRecorder created, mimeType=', mediaRecorder.mimeType);
        let chunkCount = 0;
        // Fully synchronous — no await — so successive chunks (and critically
        // the final chunk before stop()) are sent strictly in order, with no
        // async gap for onstop's voice_end to race ahead of.
        mediaRecorder.ondataavailable = (event) => {
          if (event.data.size === 0 || !ws || ws.readyState !== WebSocket.OPEN) {
            console.log('[aviatesVoice] chunk dropped — size=', event.data.size, 'ws open=', !!ws && ws.readyState === WebSocket.OPEN);
            return;
          }
          const idBytes = new TextEncoder().encode(myPilotId);
          const header = new Uint8Array(1 + idBytes.length);
          header[0] = idBytes.length;
          header.set(idBytes, 1);
          // WebSocket.send() accepts a Blob directly; the receiving socket's
          // binaryType governs what its onmessage sees, not what the sender
          // transmits, so this still arrives as an ArrayBuffer on the wire.
          ws.send(new Blob([header, event.data]));
          chunkCount += 1;
          if (chunkCount === 1 || chunkCount % 10 === 0) {
            console.log('[aviatesVoice] sent chunk #', chunkCount, 'bytes=', event.data.size);
          }
        };
        // The browser guarantees the final dataavailable is dispatched (and,
        // since the handler above is synchronous, fully handled/sent) before
        // 'stop' fires — so voice_end sent from here is correctly ordered
        // after the last audio chunk, every time.
        mediaRecorder.onstop = () => {
          console.log('[aviatesVoice] MediaRecorder stopped, total chunks sent=', chunkCount);
          if (ws && ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify({ type: 'voice_end', from: myPilotId }));
            console.log('[aviatesVoice] sent voice_end');
          } else {
            console.log('[aviatesVoice] voice_end NOT sent — ws not open');
          }
        };
        ws.send(JSON.stringify({ type: 'voice_start', from: myPilotId }));
        console.log('[aviatesVoice] sent voice_start');
        mediaRecorder.start(100);

        // A release (stopTransmit()) that arrived while we were still
        // awaiting getUserMedia() couldn't touch mediaRecorder (it didn't
        // exist yet) and instead parked itself as pendingStop. Now that
        // recording has actually started, honor that queued release
        // immediately — otherwise the mic stays open/transmitting until
        // some later, unrelated press+release cycle happens to catch
        // mediaRecorder set, a real open-mic issue.
        //
        // starting must be cleared BEFORE calling stopTransmit() here:
        // stopTransmit() itself checks `starting` and, while it's still
        // true, treats any call as another deferred release rather than
        // an actual stop — so clearing it early (finally below is a
        // harmless no-op repeat) is what lets this call really stop the
        // recorder instead of just re-arming pendingStop forever.
        if (pendingStop) {
          pendingStop = false;
          starting = false;
          this.stopTransmit();
        }
      } catch (err) {
        console.error('[aviatesVoice] Failed to start recording', err);
        pendingStop = false;
        mediaStream?.getTracks().forEach((t) => t.stop());
        mediaStream = null;
        mediaRecorder = null;
      } finally {
        starting = false;
      }
    },

    stopTransmit() {
      if (starting) {
        // getUserMedia()/recorder setup is still in flight — mediaRecorder
        // doesn't exist yet, so there's nothing to stop right now. Defer:
        // startTransmit() checks this flag right after it actually starts
        // recording and immediately stops it for real at that point.
        pendingStop = true;
        return;
      }
      if (mediaRecorder) {
        mediaRecorder.stop(); // onstop (above) sends voice_end once the final chunk is flushed
        mediaRecorder = null;
      }
      if (mediaStream) {
        mediaStream.getTracks().forEach((t) => t.stop());
        mediaStream = null;
      }
    },
  };
})();
