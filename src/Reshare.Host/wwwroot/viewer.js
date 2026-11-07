// Reshare viewer: decodes the host's H.264 stream with WebCodecs and forwards mouse/keyboard input.
(() => {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const canvas = $('screen');
  const ctx = canvas.getContext('2d', { alpha: false, desynchronized: true });

  const store = {
    get(k) { try { return localStorage.getItem('reshare.' + k); } catch { return null; } },
    set(k, v) { try { localStorage.setItem('reshare.' + k, v); } catch { /* storage unavailable */ } },
  };

  const state = {
    pin: new URLSearchParams(location.search).get('pin') || store.get('pin') || '',
    config: null,
    streamWs: null,
    inputWs: null,
    decoder: null,
    avcC: null,
    codec: '',
    waitingKey: true,
    connected: false,
    backoff: 500,
    retryTimer: 0,
    decoderErrors: 0,
    // stats
    frames: 0,
    bytes: 0,
    rtt: null,
    showStats: false,
    wakeLock: null,
  };

  const viewerIsMac = /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);

  // ---------- UI helpers ----------

  function setStatus(text, cls = '') {
    const el = $('status');
    el.textContent = text;
    el.className = 'status ' + cls;
  }

  function showMessage(title, text, { askPin = false, retry = false } = {}) {
    $('msgTitle').textContent = title;
    $('msgText').textContent = text;
    $('pinForm').hidden = !askPin && !retry;
    $('pin').hidden = !askPin;
    $('pinForm').querySelector('button').textContent = askPin ? 'Connect' : 'Reconnect';
    $('message').hidden = false;
    if (askPin) setTimeout(() => $('pin').focus(), 0);
  }

  function hideMessage() { $('message').hidden = true; }
  const messageVisible = () => !$('message').hidden;

  // ---------- startup ----------

  async function start() {
    if (!('VideoDecoder' in window)) {
      if (!window.isSecureContext) {
        showMessage('Secure connection required',
          'Browsers only allow video decoding on secure pages.\nOpen the https:// address printed by reshare-host and accept the certificate warning.');
      } else {
        showMessage('Browser not supported',
          'This viewer needs WebCodecs: a recent Chrome, Edge, Safari 16.4+ or Firefox 130+.');
      }
      return;
    }

    try {
      const res = await fetch('config', { cache: 'no-store' });
      state.config = await res.json();
    } catch {
      setStatus('Host unreachable, retrying…', 'warn');
      setTimeout(start, 2000);
      return;
    }
    document.title = `Reshare – ${state.config.name}`;

    const swap = store.get('swapCmd');
    $('swapCmd').checked = swap != null ? swap === '1' : (viewerIsMac && state.config.os !== 'mac');
    $('swapWrap').hidden = !viewerIsMac;
    $('localCursor').checked = store.get('localCursor') === '1';
    document.body.classList.toggle('local-cursor', $('localCursor').checked);

    if (state.config.pinRequired && !state.pin) {
      showMessage('Enter PIN', `Enter the PIN shown in the reshare-host console on ${state.config.name}.`, { askPin: true });
      return;
    }
    connect();
  }

  function wsUrl(path) {
    const u = new URL(path, location.href);
    u.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    u.search = '';
    if (state.pin) u.searchParams.set('pin', state.pin);
    return u.href;
  }

  // ---------- connection ----------

  function closeSockets() {
    clearTimeout(state.retryTimer);
    const { streamWs, inputWs } = state;
    state.streamWs = state.inputWs = null;
    for (const ws of [streamWs, inputWs]) {
      if (ws) { ws.onclose = null; try { ws.close(); } catch { /* already closed */ } }
    }
    resetDecoder();
    state.connected = false;
  }

  function connect() {
    closeSockets();
    hideMessage();
    setStatus('Connecting…');

    const input = new WebSocket(wsUrl('input'));
    state.inputWs = input;
    input.onmessage = (e) => {
      const m = JSON.parse(e.data);
      if (m.t === 'pong') state.rtt = performance.now() - m.ts;
    };
    input.onclose = (e) => { if (e.code === 4001) authFailed(); };

    const stream = new WebSocket(wsUrl('stream'));
    stream.binaryType = 'arraybuffer';
    state.streamWs = stream;
    stream.onopen = () => { state.backoff = 500; requestWakeLock(); };
    stream.onmessage = (e) => onStreamMessage(e.data);
    stream.onclose = (e) => {
      if (state.streamWs !== stream) return;
      if (e.code === 4001) return authFailed();
      state.connected = false;
      resetDecoder();
      if (e.code === 4002) {
        closeSockets();
        setStatus('Disconnected', 'warn');
        showMessage('Disconnected', 'Another viewer connected to this host.', { retry: true });
        return;
      }
      setStatus(e.reason ? `Disconnected (${e.reason}), retrying…` : 'Disconnected, retrying…', 'warn');
      state.retryTimer = setTimeout(connect, state.backoff);
      state.backoff = Math.min(state.backoff * 2, 5000);
    };
  }

  function authFailed() {
    closeSockets();
    state.pin = '';
    store.set('pin', '');
    setStatus('Wrong PIN', 'warn');
    showMessage('Wrong PIN', 'The PIN did not match. Check the reshare-host console.', { askPin: true });
  }

  $('pinForm').addEventListener('submit', (e) => {
    e.preventDefault();
    if (!$('pin').hidden) {
      const v = $('pin').value.trim();
      if (!v) return;
      state.pin = v;
      store.set('pin', v);
    }
    connect();
  });

  // ---------- video ----------

  function resetDecoder() {
    if (state.decoder && state.decoder.state !== 'closed') {
      try { state.decoder.close(); } catch { /* ignore */ }
    }
    state.decoder = null;
    state.waitingKey = true;
  }

  function sameBytes(a, b) {
    if (!a || !b || a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
    return true;
  }

  function makeDecoder() {
    resetDecoder();
    const decoder = new VideoDecoder({
      output: onFrame,
      error: (err) => {
        console.error('decoder error', err);
        if (state.decoder !== decoder) return;
        state.decoder = null;
        if (++state.decoderErrors <= 5 && state.avcC) {
          setStatus('Decoder error, recovering…', 'warn');
          setTimeout(makeDecoder, 100);
        } else {
          setStatus('Decoder error: ' + err.message, 'warn');
        }
      },
    });
    decoder.configure({
      codec: state.codec,
      description: state.avcC,
      optimizeForLatency: true,
      hardwareAcceleration: 'no-preference',
    });
    state.decoder = decoder;
    state.waitingKey = true;
  }

  function onStreamMessage(buf) {
    const u8 = new Uint8Array(buf);
    state.bytes += buf.byteLength;
    const type = u8[0];

    if (type === 1) {
      const avcC = u8.slice(1);
      const codec = 'avc1.' + [1, 2, 3].map((i) => avcC[i].toString(16).padStart(2, '0')).join('');
      const live = state.decoder && state.decoder.state === 'configured';
      if (live && codec === state.codec && sameBytes(avcC, state.avcC)) return;
      state.avcC = avcC;
      state.codec = codec;
      state.decoderErrors = 0;
      makeDecoder();
      return;
    }

    const decoder = state.decoder;
    if (!decoder || decoder.state !== 'configured') return;
    const key = type === 2;
    if (state.waitingKey) {
      if (!key) return;
      state.waitingKey = false;
    }
    // If decoding falls behind, skip ahead to the next key frame instead of building latency.
    if (!key && decoder.decodeQueueSize > 8) {
      state.waitingKey = true;
      return;
    }
    const timestamp = Number(new DataView(buf).getBigUint64(1));
    try {
      decoder.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp, data: u8.subarray(9) }));
    } catch (err) {
      console.warn(err);
      state.waitingKey = true;
    }
  }

  function onFrame(frame) {
    if (canvas.width !== frame.displayWidth || canvas.height !== frame.displayHeight) {
      canvas.width = frame.displayWidth;
      canvas.height = frame.displayHeight;
    }
    ctx.drawImage(frame, 0, 0);
    frame.close();
    state.frames++;
    if (!state.connected) {
      state.connected = true;
      state.decoderErrors = 0;
      setStatus(`Connected to ${state.config.name}`, 'ok');
    }
  }

  // ---------- input ----------

  function send(msg) {
    const ws = state.inputWs;
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(msg));
  }

  /** Map client coordinates to 0..1 within the letterboxed video. */
  function normalize(e) {
    const vw = canvas.width, vh = canvas.height;
    if (!vw || !vh) return null;
    const r = canvas.getBoundingClientRect();
    const scale = Math.min(r.width / vw, r.height / vh);
    const w = vw * scale, h = vh * scale;
    const x = (e.clientX - r.left - (r.width - w) / 2) / w;
    const y = (e.clientY - r.top - (r.height - h) / 2) / h;
    return { x: Math.min(1, Math.max(0, x)), y: Math.min(1, Math.max(0, y)) };
  }

  let pendingMove = null;
  let moveQueued = false;

  function flushMove() {
    moveQueued = false;
    if (pendingMove) {
      send({ t: 'mm', x: pendingMove.x, y: pendingMove.y });
      pendingMove = null;
    }
  }

  canvas.addEventListener('pointermove', (e) => {
    const p = normalize(e);
    if (!p) return;
    pendingMove = p;
    if (!moveQueued) {
      moveQueued = true;
      requestAnimationFrame(flushMove);
    }
  });

  canvas.addEventListener('pointerdown', (e) => {
    e.preventDefault();
    canvas.focus();
    try { canvas.setPointerCapture(e.pointerId); } catch { /* ignore */ }
    const p = normalize(e);
    if (p) { pendingMove = p; flushMove(); }
    send({ t: 'md', b: e.button < 0 ? 0 : e.button });
  });

  canvas.addEventListener('pointerup', (e) => {
    e.preventDefault();
    const p = normalize(e);
    if (p) { pendingMove = p; flushMove(); }
    send({ t: 'mu', b: e.button < 0 ? 0 : e.button });
  });

  canvas.addEventListener('pointercancel', () => send({ t: 'release' }));
  canvas.addEventListener('contextmenu', (e) => e.preventDefault());
  canvas.addEventListener('auxclick', (e) => e.preventDefault());

  canvas.addEventListener('wheel', (e) => {
    e.preventDefault();
    const mult = e.deltaMode === 1 ? 40 : e.deltaMode === 2 ? 800 : 1;
    send({ t: 'wh', dx: e.deltaX * mult, dy: e.deltaY * mult });
  }, { passive: false });

  function mapCode(code) {
    if ($('swapCmd').checked) {
      if (code === 'MetaLeft') return 'ControlLeft';
      if (code === 'MetaRight') return 'ControlRight';
    }
    return code;
  }

  window.addEventListener('keydown', (e) => {
    if (messageVisible()) return;
    if (e.code === 'F9') {
      e.preventDefault();
      state.showStats = !state.showStats;
      $('overlay').hidden = !state.showStats;
      return;
    }
    if (!e.code) return;
    e.preventDefault();
    send({ t: 'kd', code: mapCode(e.code) });
  });

  window.addEventListener('keyup', (e) => {
    if (messageVisible() || e.code === 'F9' || !e.code) return;
    e.preventDefault();
    send({ t: 'ku', code: mapCode(e.code) });
  });

  window.addEventListener('blur', () => send({ t: 'release' }));
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) send({ t: 'release' });
    else requestWakeLock();
  });

  // ---------- toolbar ----------

  $('fullscreen').addEventListener('click', async () => {
    const el = document.documentElement;
    try {
      if (el.requestFullscreen) await el.requestFullscreen({ navigationUI: 'hide' });
      else if (el.webkitRequestFullscreen) el.webkitRequestFullscreen();
      // Lets Esc, Alt+Tab, Win etc. reach the host while fullscreen (Chromium only).
      if (navigator.keyboard && navigator.keyboard.lock) await navigator.keyboard.lock();
    } catch (err) {
      console.warn('fullscreen failed', err);
    }
    canvas.focus();
  });

  for (const ev of ['fullscreenchange', 'webkitfullscreenchange']) {
    document.addEventListener(ev, () => {
      const fs = !!(document.fullscreenElement || document.webkitFullscreenElement);
      document.body.classList.toggle('fullscreen', fs);
      if (!fs && navigator.keyboard && navigator.keyboard.unlock) navigator.keyboard.unlock();
    });
  }

  $('localCursor').addEventListener('change', (e) => {
    document.body.classList.toggle('local-cursor', e.target.checked);
    store.set('localCursor', e.target.checked ? '1' : '0');
  });
  $('swapCmd').addEventListener('change', (e) => store.set('swapCmd', e.target.checked ? '1' : '0'));

  async function requestWakeLock() {
    try {
      if ('wakeLock' in navigator && !state.wakeLock && !document.hidden) {
        state.wakeLock = await navigator.wakeLock.request('screen');
        state.wakeLock.addEventListener('release', () => { state.wakeLock = null; });
      }
    } catch { /* not allowed, fine */ }
  }

  // ---------- stats & keepalive ----------

  setInterval(() => {
    send({ t: 'ping', ts: performance.now() });
    if (state.showStats) {
      const d = state.decoder;
      $('overlay').textContent = [
        `${canvas.width}x${canvas.height}  ${state.codec || '-'}`,
        `fps     ${state.frames}`,
        `bitrate ${(state.bytes * 8 / 1000).toFixed(0)} kbit/s`,
        `rtt     ${state.rtt == null ? '-' : state.rtt.toFixed(1) + ' ms'}`,
        `queue   ${d ? d.decodeQueueSize : '-'}`,
        'F9 to hide',
      ].join('\n');
    }
    state.frames = 0;
    state.bytes = 0;
  }, 1000);

  start();
})();
