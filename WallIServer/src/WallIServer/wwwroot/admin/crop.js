// Crop / zoom dialog with a live 4-grey preview. Exposes window.WalliCrop.open(file).
(() => {
  'use strict';

  const W = 792;          // panel width  (canvas units = panel pixels)
  const H = 272;          // panel height
  const MAX_ZOOM = 8;     // × cover
  const NUDGE = 10;       // panel px per arrow key

  const $ = (sel) => document.querySelector(sel);
  const dialog = $('#crop-dialog');
  const view = $('#crop-canvas');
  const preview = $('#crop-preview');
  const zoomInput = $('#crop-zoom');
  const fitInput = $('#crop-fit');
  const ditherInput = $('#crop-dither');
  const viewCtx = view.getContext('2d');
  const previewCtx = preview.getContext('2d');
  const out = Object.assign(document.createElement('canvas'), { width: W, height: H });
  const outCtx = out.getContext('2d', { willReadFrequently: true });

  let bitmap = null;
  let cover = 1;
  let scale = 1;
  let pan = { x: 0, y: 0 };
  let finish = null;      // resolves the open() promise
  let frame = 0;

  const zoomToScale = (v) => cover * Math.pow(MAX_ZOOM, v / 1000);
  const scaleToZoom = (s) => Math.round(1000 * Math.log(s / cover) / Math.log(MAX_ZOOM));
  const clamp = () => { pan = WalliCore.clampPan(pan, scale, bitmap.width, bitmap.height, W, H); };

  function center() {
    pan = { x: (W - bitmap.width * scale) / 2, y: (H - bitmap.height * scale) / 2 };
    clamp();
  }

  function reset() {
    cover = WalliCore.coverZoom(bitmap.width, bitmap.height, W, H);
    scale = cover;
    zoomInput.value = '0';
    fitInput.checked = false;
    center();
    update();
  }

  /** Zoom keeping the panel point (px, py) still. */
  function zoomTo(newScale, px = W / 2, py = H / 2) {
    newScale = Math.min(cover * MAX_ZOOM, Math.max(cover, newScale));
    const ix = (px - pan.x) / scale;
    const iy = (py - pan.y) / scale;
    scale = newScale;
    pan = { x: px - ix * scale, y: py - iy * scale };
    clamp();
    zoomInput.value = String(scaleToZoom(scale));
    update();
  }

  /** Renders the crop at panel resolution into `out` (white background, as the server composites). */
  function renderOut() {
    outCtx.fillStyle = '#fff';
    outCtx.fillRect(0, 0, W, H);
    outCtx.imageSmoothingQuality = 'high';
    if (fitInput.checked) {
      const s = Math.min(W / bitmap.width, H / bitmap.height);
      const w = bitmap.width * s;
      const h = bitmap.height * s;
      outCtx.drawImage(bitmap, (W - w) / 2, (H - h) / 2, w, h);
    } else {
      outCtx.drawImage(bitmap, pan.x, pan.y, bitmap.width * scale, bitmap.height * scale);
    }
  }

  function draw() {
    frame = 0;
    if (!bitmap) return;
    renderOut();
    viewCtx.drawImage(out, 0, 0);
    const pixels = outCtx.getImageData(0, 0, W, H);
    const levels = WalliCore.quantizeToLevels(pixels.data, W, H, ditherInput.checked);
    previewCtx.putImageData(new ImageData(WalliCore.levelsToRgba(levels, W, H), W, H), 0, 0);
  }

  function update() {
    const fixed = fitInput.checked;
    view.classList.toggle('fixed', fixed);
    zoomInput.disabled = fixed;
    if (!frame) frame = requestAnimationFrame(draw);
  }

  // ---- interaction
  const toPanel = (e) => {
    const r = view.getBoundingClientRect();
    return { x: (e.clientX - r.left) * (W / r.width), y: (e.clientY - r.top) * (H / r.height), k: W / r.width };
  };
  let drag = null;
  view.addEventListener('pointerdown', (e) => {
    if (!bitmap || fitInput.checked) return;
    view.setPointerCapture(e.pointerId);
    drag = { x: e.clientX, y: e.clientY, k: toPanel(e).k };
    view.classList.add('dragging');
  });
  view.addEventListener('pointermove', (e) => {
    if (!drag) return;
    pan = { x: pan.x + (e.clientX - drag.x) * drag.k, y: pan.y + (e.clientY - drag.y) * drag.k };
    drag.x = e.clientX;
    drag.y = e.clientY;
    clamp();
    update();
  });
  const endDrag = () => { drag = null; view.classList.remove('dragging'); };
  view.addEventListener('pointerup', endDrag);
  view.addEventListener('pointercancel', endDrag);
  view.addEventListener('wheel', (e) => {
    if (!bitmap || fitInput.checked) return;
    e.preventDefault();
    const p = toPanel(e);
    zoomTo(scale * Math.exp(-e.deltaY * 0.0015), p.x, p.y);
  }, { passive: false });
  view.addEventListener('keydown', (e) => {
    if (!bitmap || fitInput.checked) return;
    const d = { ArrowLeft: [NUDGE, 0], ArrowRight: [-NUDGE, 0], ArrowUp: [0, NUDGE], ArrowDown: [0, -NUDGE] }[e.key];
    if (d) { e.preventDefault(); pan = { x: pan.x + d[0], y: pan.y + d[1] }; clamp(); update(); }
    if (e.key === '+' || e.key === '=') zoomTo(scale * 1.15);
    if (e.key === '-') zoomTo(scale / 1.15);
  });
  zoomInput.addEventListener('input', () => zoomTo(zoomToScale(Number(zoomInput.value))));
  fitInput.addEventListener('change', update);
  ditherInput.addEventListener('change', update);
  $('#crop-reset').addEventListener('click', () => bitmap && reset());

  // ---- open / close
  function close(result) {
    const done = finish;
    finish = null;
    if (dialog.open) dialog.close();
    bitmap?.close?.();
    bitmap = null;
    window.WalliAdmin?.resume();
    done?.(result);
  }
  $('#crop-cancel').addEventListener('click', () => close(null));
  dialog.addEventListener('cancel', (e) => { e.preventDefault(); close(null); }); // Escape
  $('#crop-form').addEventListener('submit', (e) => {
    e.preventDefault();
    if (!bitmap) return;
    renderOut();
    const dither = ditherInput.checked;
    out.toBlob((blob) => close(blob ? { blob, dither } : null), 'image/png');
  });

  /**
   * Opens the dialog for an image file. Resolves with {blob, dither} (an exact 792×272 PNG)
   * or null on cancel; rejects if the browser can't decode the file.
   */
  async function open(file, title) {
    let decoded;
    try {
      decoded = await createImageBitmap(file, { imageOrientation: 'from-image' });
    } catch {
      throw new Error("Can't read this image (supported: PNG, JPEG, GIF, WebP, BMP).");
    }
    if (finish) close(null);
    bitmap = decoded;
    $('#crop-title').textContent = title ?? 'Picture';
    ditherInput.checked = true;
    window.WalliAdmin?.pause();
    dialog.showModal();
    reset();
    view.focus();
    return new Promise((resolve) => { finish = resolve; });
  }

  window.WalliCrop = { open };
})();
