// WallInfoPanel setup page: talks to the panel over Web Serial with the JSON-lines
// protocol from docs/protocol.md §3. Plain JS, no dependencies.
'use strict';

(() => {
  const BAUD = 115200;
  const READY_TIMEOUT_MS = 4000;      // wait this long for {"evt":"ready"} before falling back to "info"
  const DEFAULT_TIMEOUT_MS = 5000;
  const LONG_TIMEOUT_MS = 30000;      // scan / test_wifi / test_update / test_display
  const MAX_LINE = 1024;
  const MAX_LOG_LINES = 600;
  const MASK = '***';

  const TIMEOUTS = {
    scan: LONG_TIMEOUT_MS,
    test_wifi: LONG_TIMEOUT_MS,
    test_update: LONG_TIMEOUT_MS,
    test_display: LONG_TIMEOUT_MS,
    factory_reset: 15000,
    set: 10000,
  };

  // protocol.md §4 error names → human text (+ typical fix from firmware-design.md §6.2).
  const ERRORS = {
    ok: ['Success', ''],
    no_config: ['Missing or invalid configuration', 'Fill in the settings and click Save.'],
    wifi_not_found: ['SSID not found', 'Check the network name, the distance and that it is a 2.4 GHz network.'],
    wifi_auth: ['Wi-Fi password rejected', 'Check the Wi-Fi password.'],
    no_ip: ['Connected but no IP (DHCP / static IP problem) or connect timeout', 'Check DHCP on the router or the static IP settings.'],
    server_unreachable: ['Server unreachable: DNS failure, connection refused or timeout', 'Check the URL and that WallIServer is running and reachable from the panel.'],
    auth_rejected: ['Token rejected by the server (HTTP 401 / 403)', 'Check the token, or rotate it on the server and enter the new one.'],
    bad_response: ['Bad response: other HTTP status or malformed response', 'Check the device id in the URL and that an image was uploaded on the server.'],
    display: ['E-paper BUSY timeout', 'Check the display wiring and PWR.'],
    low_battery: ['Battery below the low-battery threshold', 'Charge the battery.'],
    invalid: ['Invalid setting', ''],
    timeout: ['No answer from the device', 'Check that the setup switch is ON (then press RST) and the right port is selected.'],
  };

  const CONFIG_FIELDS = [
    'name', 'wifi.ssid', 'wifi.pass', 'net.ip', 'net.gw', 'net.mask', 'net.dns',
    'url', 'token', 'interval', 'lowBatt_mV', 'battScale_mV',
  ];
  const SECRET_FIELDS = new Set(['wifi.pass', 'token']);
  const INT_FIELDS = new Set(['interval', 'lowBatt_mV', 'battScale_mV']);

  const $ = (sel) => document.querySelector(sel);
  const form = $('#cfg-form');
  const statusEl = $('#status');
  const connBtn = $('#btn-connect');
  const connState = $('#conn-state');
  const logEl = $('#log');
  const resultEl = $('#result');

  /** Device-reported secret state: true when the device said "***" (a value is stored). */
  const secretStored = { 'wifi.pass': false, token: false };

  let link = null;          // SerialLink when connected
  let busy = false;
  let dirty = false;

  // ---------------------------------------------------------------- utilities

  const input = (field) => form.elements.namedItem(field);
  const getPath = (obj, path) => path.split('.').reduce((o, k) => (o == null ? undefined : o[k]), obj);
  const setPath = (obj, path, value) => {
    const keys = path.split('.');
    let o = obj;
    for (let i = 0; i < keys.length - 1; i++) o = (o[keys[i]] ??= {});
    o[keys[keys.length - 1]] = value;
  };
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const escapeHtml = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  function errorText(err, msg, field) {
    const [text, fix] = ERRORS[err] ?? [err ? `Error "${err}"` : 'Error', ''];
    let s = text;
    if (field) s += ` (${field})`;
    if (msg && msg !== text) s += `: ${msg}`;
    if (fix) s += `. ${fix}`;
    return s;
  }

  function setStatus(kind, html) {
    statusEl.className = `status status-${kind}`;
    statusEl.innerHTML = html;
  }

  function setDirty(value) {
    dirty = value;
    $('#dirty').hidden = !value;
  }

  // -------------------------------------------------------------------- log

  function log(kind, text) {
    if (kind === 'noise' && !$('#log-noise').checked) return;
    const atBottom = logEl.scrollHeight - logEl.scrollTop - logEl.clientHeight < 24;
    const line = document.createElement('div');
    const ts = new Date().toTimeString().slice(0, 8);
    const arrow = { tx: '→', rx: '←', ev: '←', noise: '·', sys: '•' }[kind] ?? ' ';
    line.innerHTML = `<span class="ts">${ts}</span> <span class="${kind}">${arrow} ${escapeHtml(text)}</span>`;
    logEl.appendChild(line);
    while (logEl.childElementCount > MAX_LOG_LINES) logEl.firstElementChild.remove();
    if (atBottom) logEl.scrollTop = logEl.scrollHeight;
  }

  /** Hides secrets in logged request lines. */
  function maskForLog(obj) {
    if (obj.cmd !== 'set' || !obj.cfg) return JSON.stringify(obj);
    const copy = JSON.parse(JSON.stringify(obj));
    if (copy.cfg.token) copy.cfg.token = '•••';
    if (copy.cfg.wifi && copy.cfg.wifi.pass) copy.cfg.wifi.pass = '•••';
    return JSON.stringify(copy);
  }

  // ------------------------------------------------------------ serial link

  class DeviceError extends Error {
    constructor(err, msg, field) {
      super(errorText(err, msg, field));
      this.err = err;
      this.msg = msg;
      this.field = field;
    }
  }

  class SerialLink {
    constructor(port) {
      this.port = port;
      this.nextId = 1;
      this.pending = new Map();
      this.reader = null;
      this.closed = false;   // disposed: pending requests rejected, onClose fired
      this.closing = false;  // close() requested by the user
      this.onEvent = () => {};
      this.onClose = () => {};
      this.readLoopDone = null;
    }

    async open() {
      await this.port.open({ baudRate: BAUD, dataBits: 8, stopBits: 1, parity: 'none', bufferSize: 4096 });
      try {
        // Keep the NodeMCU auto-reset circuit (DTR/RTS) idle.
        await this.port.setSignals({ dataTerminalReady: false, requestToSend: false });
      } catch (e) {
        log('sys', `setSignals failed: ${e.message}`);
      }
      this.readLoopDone = this.readLoop();
    }

    async readLoop() {
      const decoder = new TextDecoder();
      let buf = '';
      try {
        while (!this.closing && !this.closed && this.port.readable) {
          this.reader = this.port.readable.getReader();
          try {
            for (;;) {
              const { value, done } = await this.reader.read();
              if (done) break;
              buf += decoder.decode(value, { stream: true });
              let nl;
              while ((nl = buf.indexOf('\n')) >= 0) {
                const line = buf.slice(0, nl).replace(/\r$/, '');
                buf = buf.slice(nl + 1);
                this.handleLine(line);
              }
              if (buf.length > MAX_LINE * 4) {
                log('noise', buf.slice(0, 200) + '…');
                buf = '';
              }
            }
          } catch (e) {
            if (!this.closing) log('sys', `Read error: ${e.message}`);
          } finally {
            this.reader.releaseLock();
            this.reader = null;
          }
          if (!this.closing && !this.closed) await sleep(50); // non-fatal error (e.g. framing at ROM baud): resume
        }
      } finally {
        if (!this.closing) this.dispose('Port closed');
      }
    }

    handleLine(line) {
      const trimmed = line.trim();
      if (!trimmed) return;
      let msg = null;
      if (trimmed.startsWith('{') && trimmed.length <= MAX_LINE) {
        try { msg = JSON.parse(trimmed); } catch { msg = null; }
      }
      if (!msg || typeof msg !== 'object' || Array.isArray(msg)) {
        log('noise', trimmed.replace(/[^\x20-\x7e]/g, '?')); // ROM boot noise at 74880 baud etc.
        return;
      }
      if (typeof msg.evt === 'string' && msg.id === undefined) {
        log('ev', trimmed);
        this.onEvent(msg);
        return;
      }
      log('rx', trimmed);
      const p = this.pending.get(msg.id);
      if (!p) return; // late answer after a timeout
      this.pending.delete(msg.id);
      clearTimeout(p.timer);
      if (msg.ok === true) p.resolve(msg);
      else p.reject(new DeviceError(msg.err ?? 'error', msg.msg, msg.field));
    }

    async send(cmd, extra = {}, timeoutMs = TIMEOUTS[cmd] ?? DEFAULT_TIMEOUT_MS) {
      if (this.closed || this.closing) throw new Error('Not connected');
      const id = this.nextId++;
      const req = { id, cmd, ...extra };
      const text = JSON.stringify(req);
      if (text.length + 1 > MAX_LINE) throw new Error('Request too long for the device (max 1024 bytes per line).');

      const result = new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
          this.pending.delete(id);
          reject(new DeviceError('timeout', `no answer to "${cmd}" within ${Math.round(timeoutMs / 1000)} s`));
        }, timeoutMs);
        this.pending.set(id, { resolve, reject, timer });
      });

      log('tx', maskForLog(req));
      const writer = this.port.writable.getWriter();
      try {
        await writer.write(new TextEncoder().encode(text + '\n'));
      } catch (e) {
        const p = this.pending.get(id);
        if (p) { clearTimeout(p.timer); this.pending.delete(id); }
        throw e;
      } finally {
        writer.releaseLock();
      }
      return result;
    }

    dispose(reason) {
      if (this.closed) return;
      this.closed = true;
      for (const p of this.pending.values()) {
        clearTimeout(p.timer);
        p.reject(new Error(reason));
      }
      this.pending.clear();
      this.onClose(reason);
    }

    async close() {
      this.closing = true;
      try { if (this.reader) await this.reader.cancel(); } catch { /* ignore */ }
      try { await this.readLoopDone; } catch { /* ignore */ }
      try { await this.port.close(); } catch { /* ignore */ }
      this.dispose('Disconnected');
    }
  }

  // ------------------------------------------------------- connect / state

  function updateControls() {
    const connected = !!link;
    for (const el of document.querySelectorAll('[data-needs-conn]')) el.disabled = !connected || busy;
    $('#ssid-list').disabled = !connected || busy || $('#ssid-list').options.length <= 1;
    connBtn.textContent = connected ? 'Disconnect' : 'Connect';
    connBtn.disabled = busy && !connected;
  }

  function setConnState(kind, text) {
    connState.className = `pill pill-${kind}`;
    connState.textContent = text;
  }

  async function connect() {
    let port;
    try {
      port = await navigator.serial.requestPort();
    } catch (e) {
      if (e.name !== 'NotFoundError') setStatus('error', `Couldn't select a port: ${escapeHtml(e.message)}`);
      return; // user cancelled the picker
    }

    const l = new SerialLink(port);
    let readyResolve;
    const ready = new Promise((r) => { readyResolve = r; });
    let initialised = false;

    l.onEvent = (evt) => {
      if (evt.evt === 'ready') {
        showInfo({ fw: evt.fw, mode: evt.mode });
        readyResolve(true);
        if (initialised && !busy) {
          log('sys', 'Device restarted, reading info again');
          runCommand('Reading device…', () => readAll(false));
        }
      }
    };
    l.onClose = (reason) => {
      if (link === l) {
        link = null;
        busy = false;
        setConnState('off', 'Not connected');
        setStatus('warn', `Connection closed (${escapeHtml(reason)}).`);
        updateControls();
      }
    };

    busy = true;
    setConnState('wait', 'Connecting…');
    setStatus('busy', 'Opening the serial port…');
    updateControls();
    try {
      await l.open();
    } catch (e) {
      busy = false;
      setConnState('off', 'Not connected');
      setStatus('error', `Couldn't open the port: ${escapeHtml(e.message)}. Is another program (serial monitor) using it?`);
      updateControls();
      return;
    }
    link = l;
    log('sys', `Port open at ${BAUD} baud`);
    setStatus('busy', 'Waiting for the panel to report <code>ready</code>…');

    const gotReady = await Promise.race([ready, sleep(READY_TIMEOUT_MS).then(() => false)]);
    if (!link) return;
    if (!gotReady) log('sys', 'No "ready" event yet, sending "info" anyway');

    busy = false;
    setConnState('on', 'Connected');
    await runCommand('Reading device…', () => readAll(true));
    initialised = true;
  }

  async function disconnect() {
    const l = link;
    link = null;
    if (l) await l.close();
    setConnState('off', 'Not connected');
    setStatus('info', 'Disconnected. Turn the setup switch OFF when you are done; the panel restarts by itself.');
    busy = false;
    updateControls();
  }

  /** Runs one command flow with the busy indicator and error reporting. */
  async function runCommand(busyText, fn) {
    if (!link || busy) return;
    busy = true;
    updateControls();
    setStatus('busy', busyText);
    try {
      await fn();
    } catch (e) {
      if (e instanceof DeviceError && e.err === 'invalid' && e.field) markFieldError(e.field, e.msg || 'invalid');
      setStatus('error', escapeHtml(e.message));
    } finally {
      busy = false;
      updateControls();
    }
  }

  // ------------------------------------------------------------- info panel

  function showInfo(info) {
    const fmt = {
      flash: (v) => `${(v / 1048576).toFixed(v % 1048576 ? 2 : 0)} MB`,
      heap: (v) => `${(v / 1024).toFixed(1)} KB`,
      batt_mV: (v) => `${(v / 1000).toFixed(2)} V (${v} mV)`,
      hasImage: (v) => (v ? 'yes' : 'no'),
      etag: (v) => v || '(none)',
    };
    for (const [key, value] of Object.entries(info)) {
      const dd = document.querySelector(`[data-info="${key}"]`);
      if (!dd || value === undefined || value === null) continue;
      dd.textContent = fmt[key] ? fmt[key](value) : String(value);
    }
  }

  // ------------------------------------------------------------------ form

  function fillForm(cfg) {
    for (const field of CONFIG_FIELDS) {
      const el = input(field);
      const value = getPath(cfg, field);
      if (SECRET_FIELDS.has(field)) {
        secretStored[field] = value === MASK;
        el.value = '';
        el.placeholder = value === MASK ? 'unchanged' : 'not set';
      } else {
        el.value = value === undefined || value === null ? '' : String(value);
      }
    }
    fillServerFields(cfg.url);
    clearFieldErrors();
    setDirty(false);
  }

  // ------------------------------------------------------------------ server address → update URL

  const srvInput = { address: () => input('srv.address'), port: () => input('srv.port'), device: () => input('srv.device') };

  /** Splits the panel's stored URL into Address / Port / Device ID; warns when it doesn't match. */
  function fillServerFields(url) {
    const parsed = WalliCore.parseUpdateUrl(url);
    const warning = WalliCore.storedUrlWarning(url);   // names the stored value so it isn't lost silently
    $('#url-warning').hidden = !warning;
    if (warning) $('#url-warning').textContent = warning;
    if (parsed) {
      srvInput.address().value = parsed.address;
      srvInput.port().value = String(parsed.port);
      srvInput.device().value = parsed.deviceId;
    }
    updateUrlPreview();
  }

  /** Rebuilds the URL from the three fields; the hidden "url" field is what Save sends. */
  function updateUrlPreview() {
    const result = WalliCore.buildUpdateUrl({
      address: srvInput.address().value, port: srvInput.port().value, deviceId: srvInput.device().value.trim(),
    });
    const out = $('#url-preview');
    out.textContent = result.url ?? result.error;
    out.classList.toggle('bad', !result.url);
    input('url').value = result.url ?? '';
    return result;
  }

  /** Field that an error from buildUpdateUrl belongs to. */
  function urlErrorField(error) {
    if (error.startsWith('Port')) return 'srv.port';
    if (error.startsWith('Device ID')) return 'srv.device';
    return 'srv.address';
  }

  /** Default address: the server's LAN IP (from /api/server-info), never localhost. */
  async function loadServerDefaults() {
    let info = null;
    try {
      const res = await fetch('/api/server-info', { cache: 'no-store' });
      if (res.ok) info = await res.json();
    } catch { /* offline or opened from a file: fall back to the page host */ }
    if (!srvInput.address().value) srvInput.address().value = WalliCore.defaultAddress(info, location.hostname);
    updateUrlPreview();
  }

  function isIPv4(s) {
    // Same rule as the firmware: four 0–255 parts, no leading zeros.
    return /^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$/.test(s);
  }

  /** Client-side validation mirroring protocol.md §3.1. Returns [{field, msg}]. */
  function validate() {
    const v = (f) => input(f).value.trim();
    const errors = [];
    const err = (field, msg) => errors.push({ field, msg });

    const name = v('name');
    if (name.length < 1 || name.length > 32) err('name', 'Must be 1–32 characters.');

    const ssid = input('wifi.ssid').value;
    if (ssid.length < 1 || ssid.length > 32) err('wifi.ssid', 'Required, 1–32 characters.');

    const pass = input('wifi.pass').value;
    if (pass.length > 0 && (pass.length < 8 || pass.length > 63)) err('wifi.pass', 'Empty (open network) or 8–63 characters.');

    const net = { ip: v('net.ip'), gw: v('net.gw'), mask: v('net.mask'), dns: v('net.dns') };
    if (net.ip || net.gw || net.mask || net.dns) {
      for (const k of ['ip', 'gw', 'mask']) {
        if (!isIPv4(net[k])) err(`net.${k}`, net[k] ? 'Not a valid IPv4 address.' : 'Required for a static IP (or clear all four for DHCP).');
      }
      if (net.dns && !isIPv4(net.dns)) err('net.dns', 'Empty or a valid IPv4 address.');
    }

    const built = updateUrlPreview();
    if (built.error) err(urlErrorField(built.error), built.error);

    const token = input('token').value;
    if (token.length > 64) err('token', 'At most 64 characters.');
    else if (token.length === 0 && !secretStored.token) err('token', 'Required, 1–64 characters.');

    const ranges = { interval: [180, 86400], lowBatt_mV: [2800, 4200], battScale_mV: [1000, 15000] };
    for (const [field, [lo, hi]] of Object.entries(ranges)) {
      const text = v(field);
      const n = Number(text);
      if (text === '' || !Number.isInteger(n) || n < lo || n > hi) err(field, `Whole number from ${lo} to ${hi}.`);
    }
    return errors;
  }

  /** Builds the cfg object for "set". Secrets are only sent when typed. */
  function collectConfig() {
    const cfg = {};
    for (const field of CONFIG_FIELDS) {
      const el = input(field);
      if (SECRET_FIELDS.has(field)) {
        if (el.value.length > 0) setPath(cfg, field, el.value);
        continue;
      }
      const raw = field === 'wifi.ssid' ? el.value : el.value.trim();
      setPath(cfg, field, INT_FIELDS.has(field) ? Number(raw) : raw);
    }
    return cfg;
  }

  function clearFieldErrors() {
    for (const el of form.querySelectorAll('.err')) el.textContent = '';
    for (const el of form.querySelectorAll('[aria-invalid]')) el.removeAttribute('aria-invalid');
  }

  function markFieldError(field, msg) {
    const el = input(field);
    const slot = form.querySelector(`.err[data-for="${CSS.escape(field)}"]`);
    if (slot) slot.textContent = msg;
    if (el) el.setAttribute('aria-invalid', 'true');
  }

  function showValidation(errors) {
    clearFieldErrors();
    for (const e of errors) markFieldError(e.field, e.msg);
    if (errors.length) {
      const first = input(errors[0].field);
      if (first) first.focus();
      setStatus('error', `Please fix ${errors.length === 1 ? 'the highlighted field' : `${errors.length} highlighted fields`}: ${escapeHtml(errors[0].msg)}`);
    }
    return errors.length === 0;
  }

  function showResult(title, rows) {
    resultEl.hidden = false;
    resultEl.innerHTML = `<strong>${escapeHtml(title)}</strong><table>${rows
      .map(([k, val]) => `<tr><td>${escapeHtml(k)}</td><td>${escapeHtml(val)}</td></tr>`).join('')}</table>`;
  }

  function warnIfDirty() {
    return dirty ? ' <b>Note:</b> the form has unsaved changes; the test used the settings saved on the device.' : '';
  }

  // ---------------------------------------------------------------- actions

  async function readAll(announce) {
    const info = await link.send('info');
    showInfo(info);
    const got = await link.send('get');
    fillForm(got.cfg ?? {});
    setStatus('ok', announce
      ? `Connected to firmware ${escapeHtml(info.fw ?? '?')} (${escapeHtml(info.mac ?? '')}). Settings loaded.`
      : 'Settings read from the device.');
  }

  async function save() {
    if (!showValidation(validate())) return;
    await runCommand('Saving…', async () => {
      await link.send('set', { cfg: collectConfig() });
      const got = await link.send('get');
      fillForm(got.cfg ?? {});
      setStatus('ok', 'Saved. Turn the setup switch <b>OFF</b> to start the panel, or run a test first.');
    });
  }

  async function scan() {
    await runCommand('Scanning Wi-Fi networks (up to 30 s)…', async () => {
      const res = await link.send('scan');
      const nets = Array.isArray(res.nets) ? res.nets : [];
      const select = $('#ssid-list');
      select.innerHTML = '';
      select.appendChild(new Option(nets.length ? `${nets.length} network${nets.length === 1 ? '' : 's'} found` : 'No networks found', ''));
      for (const n of nets) {
        const label = `${n.ssid || '(hidden)'}  ·  ${n.rssi} dBm  ·  ch ${n.ch}  ·  ${n.enc || '?'}`;
        const opt = new Option(label, n.ssid ?? '');
        opt.disabled = !n.ssid;
        select.appendChild(opt);
      }
      setStatus('ok', `Scan finished: ${nets.length} network${nets.length === 1 ? '' : 's'}. Pick one from the list.`);
    });
  }

  async function testWifi() {
    await runCommand('Testing Wi-Fi with the saved settings (up to 30 s)…', async () => {
      const r = await link.send('test_wifi');
      showResult('Wi-Fi test passed', [['IP address', r.ip], ['RSSI', `${r.rssi} dBm`], ['Time', `${r.ms} ms`]]);
      setStatus('ok', `Wi-Fi OK: got ${escapeHtml(r.ip)} (${escapeHtml(r.rssi)} dBm) in ${escapeHtml(r.ms)} ms.${warnIfDirty()}`);
    });
  }

  async function testUpdate() {
    await runCommand('Running one update cycle: fetch, store, redraw (up to 30 s)…', async () => {
      const r = await link.send('test_update');
      showResult('Update test passed', [
        ['HTTP status', r.http], ['Bytes', r.bytes], ['Region update', r.region ? 'yes' : 'no'],
        ['Next interval', `${r.interval} s`], ['Time', `${r.ms} ms`],
      ]);
      setStatus('ok', `Update OK: HTTP ${escapeHtml(r.http)}, ${escapeHtml(r.bytes)} bytes${r.region ? ' (region)' : ''}, next interval ${escapeHtml(r.interval)} s.${warnIfDirty()}`);
      const info = await link.send('info');
      showInfo(info);
    });
  }

  async function testDisplay() {
    await runCommand('Drawing the test pattern…', async () => {
      const r = await link.send('test_display');
      showResult('Display test done', [['Time', `${r.ms} ms`]]);
      setStatus('ok', `Test pattern drawn in ${escapeHtml(r.ms)} ms: you should see 4 grey bars and text.`);
    });
  }

  async function factoryReset() {
    const dialog = $('#reset-dialog');
    let confirmed;
    if (typeof dialog.showModal === 'function') {
      dialog.returnValue = '';
      dialog.showModal();
      confirmed = await new Promise((resolve) => dialog.addEventListener('close', () => resolve(dialog.returnValue === 'confirm'), { once: true }));
    } else {
      confirmed = window.confirm('Factory reset? This erases the configuration and the stored image.');
    }
    if (!confirmed) return;
    await runCommand('Erasing configuration and stored image…', async () => {
      await link.send('factory_reset', { confirm: true });
      try {
        const info = await link.send('info');
        showInfo(info);
        const got = await link.send('get');
        fillForm(got.cfg ?? {});
      } catch { /* device may restart */ }
      setStatus('ok', 'Factory reset done. Enter the settings again and click Save.');
    });
  }

  async function reboot() {
    await runCommand('Rebooting…', async () => {
      await link.send('reboot');
      setStatus('ok', 'The panel is restarting. With the setup switch ON it comes back in setup mode and the page reads it again.');
    });
  }

  // ------------------------------------------------------------------ wiring

  function init() {
    if (!('serial' in navigator)) {
      $('#no-serial').hidden = false;
      connBtn.disabled = true;
      setStatus('error', 'Web Serial is required for setup. See the note above.');
    } else {
      navigator.serial.addEventListener('disconnect', (e) => {
        if (link && e.target === link.port) {
          log('sys', 'USB device unplugged');
          link.dispose('USB device unplugged');
        }
      });
    }

    connBtn.addEventListener('click', () => (link ? disconnect() : connect()));
    $('#btn-read').addEventListener('click', () => runCommand('Reading device…', () => readAll(false)));
    form.addEventListener('submit', (e) => { e.preventDefault(); save(); });
    $('#btn-scan').addEventListener('click', scan);
    $('#btn-test-wifi').addEventListener('click', testWifi);
    $('#btn-test-update').addEventListener('click', testUpdate);
    $('#btn-test-display').addEventListener('click', testDisplay);
    $('#btn-reset').addEventListener('click', factoryReset);
    $('#btn-reboot').addEventListener('click', reboot);
    $('#btn-log-clear').addEventListener('click', () => { logEl.innerHTML = ''; });

    $('#ssid-list').addEventListener('change', (e) => {
      if (!e.target.value) return;
      input('wifi.ssid').value = e.target.value;
      setDirty(true);
      input('wifi.pass').focus();
    });

    form.addEventListener('input', (e) => {
      if (!e.target.name) return;
      setDirty(true);
      e.target.removeAttribute('aria-invalid');
      const slot = form.querySelector(`.err[data-for="${CSS.escape(e.target.name)}"]`);
      if (slot) slot.textContent = '';
    });

    for (const btn of document.querySelectorAll('[data-toggle]')) {
      btn.addEventListener('click', () => {
        const el = document.getElementById(btn.dataset.toggle);
        const show = el.type === 'password';
        el.type = show ? 'text' : 'password';
        btn.textContent = show ? 'Hide' : 'Show';
      });
    }

    window.addEventListener('beforeunload', (e) => {
      if (link && dirty) { e.preventDefault(); e.returnValue = ''; }
    });

    // Sensible defaults before a device is read.
    input('interval').value = '900';
    input('lowBatt_mV').value = '3300';
    input('battScale_mV').value = '4200';
    for (const el of [srvInput.address(), srvInput.port(), srvInput.device()]) el.addEventListener('input', updateUrlPreview);
    loadServerDefaults();
    updateControls();
  }

  init();
})();
