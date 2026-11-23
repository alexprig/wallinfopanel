// WalliCore — pure logic shared by the setup and admin pages (no DOM access).
// Works as a classic browser script (window.WalliCore) and as a CommonJS module (node --test).
(function (root, factory) {
  'use strict';
  const core = factory();
  if (typeof module === 'object' && module.exports) module.exports = core;
  else root.WalliCore = core;
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  const DEVICE_ID_RE = /^[a-z0-9-]{1,32}$/;
  const MAX_URL = 160;                         // firmware limit (protocol.md §3.1)
  const STEP = 255 / 3;                        // 85: distance between grey levels
  const URL_RE = /^http:\/\/([^/:\s]+)(?::(\d{1,5}))?\/api\/panel\/([^/\s]+)$/;
  // Plain IPv4 or DNS host name: labels of letters/digits/hyphens, no leading/trailing hyphen.
  const LABEL = '[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?';
  const HOST_RE = new RegExp(`^(?=.{1,253}$)${LABEL}(?:\\.${LABEL})*$`);
  const isValidHost = (h) => typeof h === 'string' && HOST_RE.test(h);

  const isValidDeviceId = (id) => typeof id === 'string' && DEVICE_ID_RE.test(id);

  /** http://<address>:<port>/api/panel/<deviceId>; always plain HTTP, port always written. */
  function buildUpdateUrl({ address, port, deviceId }) {
    const host = String(address ?? '').trim();
    if (!host) return { url: null, error: 'Address is required.' };
    if (!isValidHost(host)) return { url: null, error: 'Address: IP or host name only (no http://, port or path).' };
    const p = Number(String(port ?? '').trim());
    if (!Number.isInteger(p) || p < 1 || p > 65535) return { url: null, error: 'Port must be 1-65535.' };
    if (!isValidDeviceId(deviceId)) return { url: null, error: 'Device ID: lowercase letters, digits and - only (max 32).' };
    const url = `http://${host}:${p}/api/panel/${deviceId}`;
    if (url.length > MAX_URL) return { url: null, error: 'URL is longer than 160 characters.' };
    return { url, error: null };
  }

  /** Splits a stored update URL; null when it doesn't match http://host[:port]/api/panel/<id>. */
  function parseUpdateUrl(url) {
    if (typeof url !== 'string') return null;
    const m = URL_RE.exec(url.trim());
    if (!m || !isValidHost(m[1]) || !isValidDeviceId(m[3])) return null;
    const port = m[2] === undefined ? 80 : Number(m[2]);
    if (port < 1 || port > 65535) return null;
    return { address: m[1], port, deviceId: m[3] };
  }

  /** Warning naming a stored URL that doesn't match the pattern; null when it matches or is empty. */
  function storedUrlWarning(url) {
    if (!url || parseUpdateUrl(url)) return null;
    return `The panel's saved URL "${url}" doesn't match http://address:port/api/panel/id — saving will replace it.`;
  }

  /**
   * Overdue = no contact within the interval the server last gave the panel (longer during the
   * night pause) plus two normal intervals. Without that value: 3 × interval.
   */
  function isStale(lastSeenIso, lastIntervalS, intervalS, nowMs) {
    if (!lastSeenIso) return true;
    const allowedS = (lastIntervalS ?? intervalS) + 2 * intervalS;
    return nowMs - Date.parse(lastSeenIso) > allowedS * 1000;
  }

  /** First LAN address reported by the server; else the page host unless it is a loopback name. */
  function defaultAddress(info, locationHostname) {
    const first = info && Array.isArray(info.addresses) ? info.addresses[0] : undefined;
    if (first) return first;
    const h = String(locationHostname ?? '').trim();
    if (!h || h === 'localhost' || h === '::1' || h === '[::1]' || h.startsWith('127.')) return '';
    return h;
  }

  /** Rec. 709 luminance on gamma-encoded values, alpha composited over white (same as the server). */
  function luminance(r, g, b, a) {
    const alpha = a / 255;
    const over = (c) => c * alpha + 255 * (1 - alpha);
    // Integer weights so pure white is exactly 255 (0.2126 + 0.7152 + 0.0722 isn't exactly 1 in binary).
    return (2126 * over(r) + 7152 * over(g) + 722 * over(b)) / 10000;
  }

  /** Mirrors Quantizer.NearestLevel: round half away from zero, clamp to 0..3. */
  function nearestLevel(lum) {
    const v = lum / STEP;
    const rounded = Math.sign(v) * Math.round(Math.abs(v));
    return Math.min(3, Math.max(0, rounded));
  }

  /** RGBA pixels → one level (0 black … 3 white) per pixel; optional Floyd–Steinberg (left to right). */
  function quantizeToLevels(rgba, width, height, dither) {
    const levels = new Uint8Array(width * height);
    const lum = new Float32Array(width * height);
    for (let i = 0; i < lum.length; i++) {
      lum[i] = luminance(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);
    }
    if (!dither) {
      for (let i = 0; i < lum.length; i++) levels[i] = nearestLevel(lum[i]);
      return levels;
    }
    // Two rolling error rows with one guard cell on each side (same layout as Quantizer.cs).
    let cur = new Float32Array(width + 2);
    let next = new Float32Array(width + 2);
    for (let y = 0; y < height; y++) {
      const row = y * width;
      for (let x = 0; x < width; x++) {
        const value = lum[row + x] + cur[x + 1];
        const level = nearestLevel(value);
        levels[row + x] = level;
        const err = value - level * STEP;
        cur[x + 2] += err * 7 / 16;
        next[x] += err * 3 / 16;
        next[x + 1] += err * 5 / 16;
        next[x + 2] += err * 1 / 16;
      }
      [cur, next] = [next, cur];
      next.fill(0);
    }
    return levels;
  }

  /** Levels → opaque grey RGBA (0, 85, 170, 255) for previews. */
  function levelsToRgba(levels, width, height) {
    const out = new Uint8ClampedArray(width * height * 4);
    for (let i = 0; i < width * height; i++) {
      const v = levels[i] * 85;
      out[i * 4] = v; out[i * 4 + 1] = v; out[i * 4 + 2] = v; out[i * 4 + 3] = 255;
    }
    return out;
  }

  /** Scale at which the image just covers the frame. */
  const coverZoom = (imgW, imgH, frameW, frameH) => Math.max(frameW / imgW, frameH / imgH);

  /** Keeps the scaled image covering the frame: pan is the image's top-left offset in frame pixels. */
  function clampPan(pan, scale, imgW, imgH, frameW, frameH) {
    const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
    const minX = Math.min(0, frameW - imgW * scale);
    const minY = Math.min(0, frameH - imgH * scale);
    return { x: clamp(pan.x, minX, 0) + 0, y: clamp(pan.y, minY, 0) + 0 }; // "+ 0" turns -0 into 0
  }

  /** 'never', 'just now', 'N min ago', 'N h ago', 'N d ago'. */
  function relativeTime(iso, nowMs) {
    if (!iso) return 'never';
    const s = Math.max(0, Math.floor((nowMs - Date.parse(iso)) / 1000));
    if (s < 60) return 'just now';
    if (s < 3600) return `${Math.floor(s / 60)} min ago`;
    if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
    return `${Math.floor(s / 86400)} d ago`;
  }

  return {
    isValidDeviceId, buildUpdateUrl, parseUpdateUrl, storedUrlWarning, isStale, defaultAddress,
    luminance, quantizeToLevels, levelsToRgba, coverZoom, clampPan, relativeTime,
  };
});
