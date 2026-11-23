// WallInfoPanel admin page — device list, status, settings, pictures (docs/protocol.md §5).
(() => {
  'use strict';

  const KEY_STORAGE = 'walli.adminKey';
  const REFRESH_MS = 30000;

  const $ = (sel, root = document) => root.querySelector(sel);
  const el = (tag, props = {}, ...children) => {
    const node = Object.assign(document.createElement(tag), props);
    for (const c of children) node.append(c ?? '');
    return node;
  };

  const devicesEl = $('#devices');
  const emptyEl = $('#empty');
  const gateEl = $('#key-gate');
  const statusEl = $('#status');
  const previews = new Map();          // id → blob URL of the current preview
  let refreshTimer = null;
  let pauseRefresh = 0;                // >0 while a dialog is open
  let keyWaiters = [];

  // ------------------------------------------------------------------ admin key

  const storage = {
    get() { try { return sessionStorage.getItem(KEY_STORAGE); } catch { return null; } },
    set(v) { try { sessionStorage.setItem(KEY_STORAGE, v); } catch { /* private mode: key lives in memory only */ } memoryKey = v; },
    clear() { try { sessionStorage.removeItem(KEY_STORAGE); } catch { /* ignore */ } memoryKey = null; },
  };
  let memoryKey = null;
  const currentKey = () => storage.get() ?? memoryKey;

  function setUnlocked(on) {
    for (const id of ['#btn-add', '#btn-refresh', '#btn-lock']) $(id).disabled = !on;
  }

  /** Shows the key form and resolves with the key the user enters. */
  function promptKey(message) {
    gateEl.hidden = false;
    devicesEl.hidden = true;
    emptyEl.hidden = true;
    setUnlocked(false);
    $('#key-error').textContent = message ?? '';
    $('#key-input').value = '';
    $('#key-input').focus();
    return new Promise((resolve) => keyWaiters.push(resolve));
  }

  $('#key-form').addEventListener('submit', (e) => {
    e.preventDefault();
    const key = $('#key-input').value.trim();
    if (!key) return;
    storage.set(key);
    gateEl.hidden = true;
    devicesEl.hidden = false;
    setUnlocked(true);
    const waiters = keyWaiters;
    keyWaiters = [];
    for (const w of waiters) w(key);
  });

  /**
   * fetch() with the admin key. Every 401 clears the key and asks again; the retry waits for the
   * user's input, so this never becomes an automatic request loop.
   */
  async function api(path, options = {}) {
    let key = currentKey() ?? await promptKey();
    for (;;) {
      const headers = new Headers(options.headers ?? {});
      headers.set('X-Api-Key', key);
      const res = await fetch(path, { ...options, headers, cache: 'no-store' });
      if (res.status !== 401) return res;
      storage.clear();
      key = await promptKey('That key was rejected by the server. Try again.');
    }
  }

  // ------------------------------------------------------------------ status line

  function setStatus(kind, text) {
    statusEl.hidden = !text;
    statusEl.className = `status status-${kind}`;
    statusEl.textContent = text ?? '';
  }

  /**
   * Wraps a button action: a thrown error (network failure, aborted request) is shown instead of
   * failing silently, and the list is reloaded so the page matches the server again.
   */
  function guard(action) {
    return async (...args) => {
      try {
        await action(...args);
      } catch (e) {
        // Reload first: a successful reload clears old errors, and this one must stay visible.
        try { await loadDevices(); } catch { /* the message below still explains the problem */ }
        setStatus('error', `Action failed: ${e.message}. The list was reloaded from the server.`);
      }
    };
  }

  async function problemText(res) {
    try {
      const body = await res.json();
      if (body.errors) return Object.values(body.errors).flat().join(' ');
      return body.detail ?? body.title ?? body.error ?? `HTTP ${res.status}`;
    } catch { return `HTTP ${res.status}`; }
  }

  // ------------------------------------------------------------------ device list

  const fmt = {
    volts: (mv) => (mv == null ? '—' : `${(mv / 1000).toFixed(2)} V`),
    rssi: (v) => (v == null ? '—' : `${v} dBm`),
    text: (v) => (v == null || v === '' ? '—' : String(v)),
    seconds: (s) => (s == null ? '—' : s >= 3600 ? `${(s / 3600).toFixed(s % 3600 ? 1 : 0)} h` : s >= 60 ? `${Math.round(s / 60)} min` : `${s} s`),
  };

  function stat(label, value, cls = '') {
    return el('div', { className: `stat ${cls}`.trim() }, el('dt', { textContent: label }), el('dd', { textContent: value }));
  }

  function isStale(device, now) {
    const t = device.telemetry ?? {};
    return WalliCore.isStale(t.lastSeen, t.lastIntervalSeconds, device.intervalSeconds, now); // night-pause aware
  }

  /** Status/statistics block, rebuilt on every refresh. */
  function renderStats(device) {
    const t = device.telemetry ?? {};
    const now = Date.now();
    const lowBatt = t.battMv != null && t.battMv < device.lowBattMv;
    return el('dl', { className: 'stats' },
      stat('Last seen', WalliCore.relativeTime(t.lastSeen, now), isStale(device, now) ? 'stale' : 'good'),
      stat('Battery', fmt.volts(t.battMv), lowBatt ? 'bad' : ''),
      stat('Wi-Fi signal', fmt.rssi(t.rssi)),
      stat('Firmware', fmt.text(t.firmware)),
      stat('Last boot', fmt.text(t.boot)),
      stat('Last error', fmt.text(t.lastError), t.lastError ? 'bad' : ''),
      stat('Last response', fmt.text(t.lastResponse)),
      stat('Next update in', fmt.seconds(device.nextInterval)),
      stat('Remote IP', fmt.text(t.remoteIp)),
    );
  }

  function renderPictureMeta(device) {
    return el('div', { className: 'picture-meta' },
      el('span', {}, 'ETag ', el('code', { textContent: device.etag ?? '—' })),
      el('span', { textContent: `updated ${WalliCore.relativeTime(device.imageUpdatedAt, Date.now())}` }),
      el('span', { textContent: `${device.storedVersions} stored version${device.storedVersions === 1 ? '' : 's'}` }),
    );
  }

  /** Builds a device card. Dynamic parts carry data-part so updateCard() can replace them. */
  function renderDevice(device) {
    const card = el('article', { className: 'card device' });
    card.dataset.id = device.id;

    const title = el('h2', {}, el('span', { className: 'name' }), el('span', { className: 'id', textContent: device.id }));
    const actions = el('div', { className: 'device-actions' },
      el('button', { type: 'button', className: 'btn btn-small', textContent: 'New token', onclick: guard(() => rotateToken(card)) }),
      el('button', { type: 'button', className: 'btn btn-small btn-danger', textContent: 'Delete', onclick: guard(() => deleteDevice(card)) }));
    card.append(el('div', { className: 'device-head' }, title, actions));

    const left = el('div', { className: 'left' });
    left.append(el('div', { className: 'stats-slot' }), renderSettings(card));
    card.append(left);

    const frame = el('div', { className: 'picture-frame' }, el('span', { className: 'placeholder', textContent: 'Loading picture…' }));
    const fileInput = el('input', { type: 'file', accept: 'image/*', hidden: true });
    const upload = guard((file) => uploadPicture(card, file));
    fileInput.addEventListener('change', () => { const f = fileInput.files[0]; fileInput.value = ''; if (f) upload(f); });
    const picture = el('div', { className: 'picture' }, frame, el('div', { className: 'meta-slot' }),
      el('div', { className: 'picture-actions' },
        el('button', { type: 'button', className: 'btn btn-small btn-primary', textContent: 'Upload picture…', onclick: () => fileInput.click() }),
        el('span', { className: 'hint', textContent: 'or drop an image on the picture' }), fileInput),
      el('div', { className: 'card-msg picture-msg', role: 'status' }));
    card.append(picture);
    // The whole card is the drop target (the picture frame highlights).
    card.addEventListener('dragover', (e) => { e.preventDefault(); frame.classList.add('drop'); });
    card.addEventListener('dragleave', (e) => { if (!card.contains(e.relatedTarget)) frame.classList.remove('drop'); });
    card.addEventListener('drop', (e) => {
      e.preventDefault();
      e.stopPropagation();
      frame.classList.remove('drop');
      const f = e.dataTransfer?.files?.[0];
      if (f) upload(f);
    });

    updateCard(card, device);
    return card;
  }

  function updateCard(card, device) {
    card._device = device;
    $('.name', card).textContent = device.name;
    $('.stats-slot', card).replaceChildren(renderStats(device));
    $('.meta-slot', card).replaceChildren(renderPictureMeta(device));
    const settings = $('form.settings', card);
    if (settings && !settings.dataset.dirty) fillSettings(settings, device);
    if (card.dataset.etag !== String(device.etag ?? '')) {
      card.dataset.etag = String(device.etag ?? '');
      refreshPreview(device.id, card); // pass the card: a new card isn't in the page yet
    }
  }

  function cardFor(id) {
    return devicesEl.querySelector(`.device[data-id="${CSS.escape(id)}"]`);
  }

  /** Fetches image.png with the admin key and shows it from a blob URL. */
  async function refreshPreview(id, card = cardFor(id)) {
    if (!card) return;
    const frame = $('.picture-frame', card);
    const res = await api(`/api/devices/${encodeURIComponent(id)}/image.png`);
    if (res.status === 404) {
      frame.replaceChildren(el('span', { className: 'placeholder', textContent: 'No picture yet' }));
      return;
    }
    if (!res.ok) {
      frame.replaceChildren(el('span', { className: 'placeholder', textContent: `Preview failed: ${await problemText(res)}` }));
      return;
    }
    const url = URL.createObjectURL(await res.blob());
    const old = previews.get(id);
    previews.set(id, url);
    frame.replaceChildren(el('img', { src: url, alt: `Current picture of ${id} (4-grey preview)` }));
    if (old) URL.revokeObjectURL(old);
  }

  async function loadDevices() {
    const res = await api('/api/devices');
    if (!res.ok) {
      setStatus('error', `Could not load devices: ${await problemText(res)}`);
      return;
    }
    const devices = await res.json();
    devicesEl.hidden = false;
    emptyEl.hidden = devices.length > 0;
    const seen = new Set();
    for (const device of devices) {
      seen.add(device.id);
      const card = cardFor(device.id);
      if (card) updateCard(card, device);
      else devicesEl.append(renderDevice(device));
    }
    for (const card of [...devicesEl.querySelectorAll('.device')]) {
      if (!seen.has(card.dataset.id)) removeCard(card.dataset.id);
    }
    for (const device of devices) devicesEl.append(cardFor(device.id)); // keep the server's order (moves, keeps state)
    $('#refresh-state').textContent = `updated ${new Date().toLocaleTimeString()}`;
    if (statusEl.classList.contains('status-error')) setStatus('info', '');
  }

  function removeCard(id) {
    cardFor(id)?.remove();
    const url = previews.get(id);
    if (url) URL.revokeObjectURL(url);
    previews.delete(id);
    emptyEl.hidden = devicesEl.querySelector('.device') !== null;
  }

  // ------------------------------------------------------------------ settings

  const SETTINGS = [
    { name: 'name', label: 'Name', type: 'text', maxLength: 64, span2: true },
    { name: 'intervalSeconds', label: 'Interval (s)', type: 'number', min: 180, max: 86400, hint: '180–86400 s between updates' },
    { name: 'lowBattMv', label: 'Low battery (mV)', type: 'number', min: 2800, max: 4200, hint: '2800–4200' },
    { name: 'nightStart', label: 'Night pause from', type: 'time', hint: 'Both or neither' },
    { name: 'nightEnd', label: 'Night pause until', type: 'time' },
    { name: 'timeZone', label: 'Time zone', type: 'text', list: 'tz-list', span2: true, hint: 'IANA name, e.g. Europe/Berlin' },
    { name: 'pendingUrl', label: 'Move panel to URL', type: 'text', maxLength: 160, span2: true, hint: 'Optional. Sent as X-Url; the panel switches after one successful call there.' },
  ];
  const NUMBER_FIELDS = new Set(['intervalSeconds', 'lowBattMv']);
  const CLEARABLE = new Set(['nightStart', 'nightEnd', 'pendingUrl']); // "" clears them on the server

  function settingsFields(prefix, fields) {
    const frag = document.createDocumentFragment();
    for (const f of fields) {
      const id = `${prefix}-${f.name}`;
      const input = el('input', { id, name: f.name, type: f.type, spellcheck: false, autocomplete: 'off' });
      for (const attr of ['min', 'max', 'maxLength']) if (f[attr] !== undefined) input[attr] = f[attr];
      if (f.list) input.setAttribute('list', f.list);
      const err = el('small', { className: 'err' });
      err.dataset.for = f.name;
      frag.append(el('div', { className: `field${f.span2 ? ' span2' : ''}` },
        el('label', { htmlFor: id, textContent: f.label }), input,
        f.hint ? el('small', { className: 'hint', textContent: f.hint }) : '', err));
    }
    return frag;
  }

  function fillSettings(form, device) {
    for (const f of SETTINGS) {
      const input = form.elements.namedItem(f.name);
      if (input) input.value = device[f.name] ?? '';
    }
  }

  /** Only the fields that differ from the stored device; "" clears night/pendingUrl. */
  function changedSettings(form, device) {
    const body = {};
    for (const f of SETTINGS) {
      const value = form.elements.namedItem(f.name).value.trim();
      if (value === String(device[f.name] ?? '')) continue;
      if (NUMBER_FIELDS.has(f.name)) body[f.name] = value === '' ? null : Number(value);
      else if (value === '' && !CLEARABLE.has(f.name)) continue;
      else body[f.name] = value;
    }
    return body;
  }

  function clearFieldErrors(form) {
    for (const e of form.querySelectorAll('.err')) e.textContent = '';
    for (const i of form.querySelectorAll('[aria-invalid]')) i.removeAttribute('aria-invalid');
  }

  /** Shows ProblemDetails `errors` next to the matching fields (unknown keys go to the form slot). */
  function showFieldErrors(form, errors) {
    clearFieldErrors(form);
    for (const [key, messages] of Object.entries(errors)) {
      const slot = form.querySelector(`.err[data-for="${CSS.escape(key)}"]`) ?? form.querySelector('.err[data-for="_form"]');
      if (slot) slot.textContent = [].concat(messages).join(' ');
      form.elements.namedItem(key)?.setAttribute?.('aria-invalid', 'true');
    }
  }

  async function errorsFrom(res) {
    try {
      const p = await res.json();
      return p.errors ?? { _form: [p.detail ?? p.title ?? `HTTP ${res.status}`] };
    } catch { return { _form: [`HTTP ${res.status}`] }; }
  }

  function renderSettings(card) {
    const msg = el('span', { className: 'card-msg', role: 'status' });
    const formErr = el('small', { className: 'err' });
    formErr.dataset.for = '_form';
    const form = el('form', { className: 'settings', noValidate: true },
      el('h3', { textContent: 'Settings' }),
      el('div', { className: 'settings-grid' }, settingsFields(`s-${card.dataset.id}`, SETTINGS)),
      formErr,
      el('div', { className: 'actions' }, msg,
        el('button', { type: 'button', className: 'btn btn-small btn-ghost', textContent: 'Revert', onclick: () => {
          fillSettings(form, card._device); delete form.dataset.dirty; clearFieldErrors(form); msg.textContent = '';
        } }),
        el('button', { type: 'submit', className: 'btn btn-small btn-primary', textContent: 'Save' })));
    form.addEventListener('input', () => { form.dataset.dirty = '1'; msg.textContent = ''; msg.className = 'card-msg'; });
    const save = guard(() => saveSettings(card, form, msg));
    form.addEventListener('submit', (e) => { e.preventDefault(); save(); });
    return form;
  }

  async function saveSettings(card, form, msg) {
    const id = card.dataset.id;
    const body = changedSettings(form, card._device);
    if (Object.keys(body).length === 0) { msg.textContent = 'No changes.'; msg.className = 'card-msg'; return; }
    const res = await api(`/api/devices/${encodeURIComponent(id)}`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
    });
    if (!res.ok) {
      showFieldErrors(form, await errorsFrom(res));
      msg.textContent = 'Not saved.'; msg.className = 'card-msg error';
      return;
    }
    clearFieldErrors(form);
    delete form.dataset.dirty;
    updateCard(card, await res.json());
    msg.textContent = 'Saved.'; msg.className = 'card-msg ok';
  }

  // ------------------------------------------------------------------ picture upload

  function pictureMessage(card, kind, text) {
    const msg = $('.picture-msg', card);
    msg.className = `card-msg picture-msg ${kind}`;
    msg.textContent = text;
  }

  /** Crop dialog → exact 792×272 PNG → POST image (fit=stretch, dither per toggle). */
  async function uploadPicture(card, file) {
    const id = card.dataset.id;
    let crop;
    try {
      crop = await WalliCrop.open(file, `Picture for ${card._device?.name ?? id}`);
    } catch (e) {
      pictureMessage(card, 'error', e.message);
      return;
    }
    if (!crop) return;
    pictureMessage(card, '', 'Uploading…');
    const body = new FormData();
    body.append('file', crop.blob, 'crop.png');
    const res = await api(`/api/devices/${encodeURIComponent(id)}/image?fit=stretch&dither=${crop.dither}`, { method: 'POST', body });
    if (!res.ok) { pictureMessage(card, 'error', `Upload failed: ${await problemText(res)}`); return; }
    const result = await res.json();
    const r = result.changedRegion;
    pictureMessage(card, 'ok', r
      ? `Uploaded — changed region ${r.x},${r.y} ${r.w}×${r.h} (${r.bodyLength.toLocaleString()} bytes for the panel)`
      : 'Uploaded — same picture as before, nothing new for the panel');
    await loadDevices();
  }

  // ------------------------------------------------------------------ dialogs

  /** showModal() that pauses auto-refresh and resolves with the dialog's returnValue. */
  function openDialog(dialog) {
    pauseRefresh++;
    dialog.returnValue = '';
    dialog.showModal();
    return new Promise((resolve) => dialog.addEventListener('close', () => {
      pauseRefresh = Math.max(0, pauseRefresh - 1);
      resolve(dialog.returnValue);
    }, { once: true }));
  }

  async function confirmDialog(title, text, okLabel) {
    $('#confirm-title').textContent = title;
    $('#confirm-text').textContent = text;
    $('#confirm-ok').textContent = okLabel;
    return (await openDialog($('#confirm-dialog'))) === 'confirm';
  }

  function showToken(token, title) {
    $('#token-title').textContent = title;
    $('#token-value').textContent = token;
    $('#token-copied').textContent = '';
    return openDialog($('#token-dialog'));
  }

  $('#token-copy').addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText($('#token-value').textContent);
      $('#token-copied').textContent = 'Copied to the clipboard.';
    } catch {
      getSelection().selectAllChildren($('#token-value'));
      $('#token-copied').textContent = 'Selected — press ⌘C / Ctrl+C to copy.';
    }
  });

  async function rotateToken(card) {
    const id = card.dataset.id;
    if (!await confirmDialog(`New token for ${id}?`, 'The current token stops working at once. The panel needs the new token (setup page) before its next update.', 'Create new token')) return;
    const res = await api(`/api/devices/${encodeURIComponent(id)}/token`, { method: 'POST' });
    if (!res.ok) { setStatus('error', `New token failed: ${await problemText(res)}`); return; }
    const { token } = await res.json();
    await showToken(token, `New token for ${id}`);
  }

  const deleteInput = $('#delete-input');
  deleteInput.addEventListener('input', () => { $('#delete-ok').disabled = deleteInput.value !== $('#delete-id').textContent; });

  async function deleteDevice(card) {
    const id = card.dataset.id;
    $('#delete-id').textContent = id;
    deleteInput.value = '';
    $('#delete-ok').disabled = true;
    if (await openDialog($('#delete-dialog')) !== 'confirm' || deleteInput.value !== id) return;
    const res = await api(`/api/devices/${encodeURIComponent(id)}`, { method: 'DELETE' });
    if (!res.ok && res.status !== 404) { setStatus('error', `Delete failed: ${await problemText(res)}`); return; }
    removeCard(id);
    setStatus('ok', `Deleted ${id}.`);
  }

  // ------------------------------------------------------------------ add device

  const ADD_FIELDS = SETTINGS.filter((f) => f.name !== 'pendingUrl');
  const addForm = $('#add-form');
  $('.settings-fields', addForm).append(el('div', { className: 'settings-grid' }, settingsFields('add', ADD_FIELDS)));

  function browserTimeZone() {
    try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; } catch { return 'UTC'; }
  }

  $('#btn-add').addEventListener('click', () => {
    addForm.reset();
    clearFieldErrors(addForm);
    fillSettings(addForm, { intervalSeconds: 900, lowBattMv: 3300, timeZone: browserTimeZone() });
    openDialog($('#add-dialog'));
    $('#add-id').focus();
  });

  $('#add-cancel').addEventListener('click', () => $('#add-dialog').close('cancel'));

  addForm.addEventListener('submit', (e) => { e.preventDefault(); createDevice(); }); // stay open until the server accepted it

  const createDevice = guard(async () => {
    const id = $('#add-id').value.trim();
    if (!WalliCore.isValidDeviceId(id)) {
      showFieldErrors(addForm, { id: ['Lowercase letters, digits and - only (max 32).'] });
      return;
    }
    const body = { id };
    for (const f of ADD_FIELDS) {
      const value = addForm.elements.namedItem(f.name).value.trim();
      if (value !== '') body[f.name] = NUMBER_FIELDS.has(f.name) ? Number(value) : value;
    }
    $('#add-submit').disabled = true;
    try {
      const res = await api('/api/devices', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      if (res.status === 409) { showFieldErrors(addForm, { id: ['A device with this id already exists.'] }); return; }
      if (!res.ok) { showFieldErrors(addForm, await errorsFrom(res)); return; }
      const created = await res.json();
      $('#add-dialog').close('created');
      await loadDevices();
      await showToken(created.token, `Token for ${id}`);
    } finally {
      $('#add-submit').disabled = false;
    }
  });

  // Time zone suggestions for every time-zone field.
  try {
    const zones = Intl.supportedValuesOf ? Intl.supportedValuesOf('timeZone') : [];
    $('#tz-list').append(...['UTC', ...zones].map((z) => el('option', { value: z })));
  } catch { /* old browser: free text only */ }

  // ------------------------------------------------------------------ refresh

  function scheduleRefresh() {
    clearInterval(refreshTimer);
    refreshTimer = setInterval(() => {
      if (pauseRefresh > 0 || document.hidden || !currentKey() || $('form.settings[data-dirty]')) return;
      loadDevices().catch((e) => setStatus('error', `Refresh failed: ${e.message}`));
    }, REFRESH_MS);
  }

  $('#btn-refresh').addEventListener('click', () => loadDevices().catch((e) => setStatus('error', e.message)));
  $('#btn-lock').addEventListener('click', () => {
    storage.clear();
    for (const id of [...previews.keys()]) removeCard(id);
    devicesEl.replaceChildren();
    promptKey('Locked. Enter the admin key to continue.').then(() => loadDevices());
  });

  // A file dropped outside a card must not make the browser open it and leave the admin page.
  for (const type of ['dragover', 'drop']) {
    document.addEventListener(type, (e) => { if (e.dataTransfer?.types?.includes('Files')) e.preventDefault(); });
  }

  // Exposed for the other admin scripts (settings, crop/upload) loaded on this page.
  window.WalliAdmin = {
    api, loadDevices, renderDevice, refreshPreview, cardFor, removeCard, setStatus, problemText, el,
    pause() { pauseRefresh++; }, resume() { pauseRefresh = Math.max(0, pauseRefresh - 1); },
  };

  async function start() {
    if (!currentKey()) await promptKey();
    else setUnlocked(true);
    await loadDevices();
    scheduleRefresh();
  }

  start().catch((e) => setStatus('error', e.message));
})();
