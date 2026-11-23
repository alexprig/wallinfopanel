// Unit tests for wwwroot/js/walli-core.js — run: node --test tests/js/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const core = require('../../src/WallIServer/wwwroot/js/walli-core.js');

const DEVICE_ID_ERROR = 'Device ID: lowercase letters, digits and - only (max 32).';

test('isValidDeviceId follows ^[a-z0-9-]{1,32}$', () => {
  assert.equal(core.isValidDeviceId('livingroom'), true);
  assert.equal(core.isValidDeviceId('living-room-2'), true);
  assert.equal(core.isValidDeviceId('LivingRoom'), false);
  assert.equal(core.isValidDeviceId(''), false);
  assert.equal(core.isValidDeviceId('a'.repeat(33)), false);
  assert.equal(core.isValidDeviceId('a_b'), false);
});

test('buildUpdateUrl builds http://address:port/api/panel/id', () => {
  assert.deepEqual(core.buildUpdateUrl({ address: '192.168.1.112', port: 5080, deviceId: 'livingroom' }),
    { url: 'http://192.168.1.112:5080/api/panel/livingroom', error: null });
  assert.equal(core.buildUpdateUrl({ address: ' h ', port: '80', deviceId: 'x' }).url, 'http://h:80/api/panel/x');
});

test('buildUpdateUrl reports errors and returns no url', () => {
  const cases = [
    [{ address: '', port: 5080, deviceId: 'a' }, 'Address is required.'],
    [{ address: 'h', port: 0, deviceId: 'a' }, 'Port must be 1-65535.'],
    [{ address: 'h', port: 70000, deviceId: 'a' }, 'Port must be 1-65535.'],
    [{ address: 'h', port: 'abc', deviceId: 'a' }, 'Port must be 1-65535.'],
    [{ address: 'h', port: 5080, deviceId: 'Bad' }, DEVICE_ID_ERROR],
    [{ address: 'a'.repeat(150), port: 5080, deviceId: 'a' }, 'URL is longer than 160 characters.'],
  ];
  for (const [input, error] of cases) {
    assert.deepEqual(core.buildUpdateUrl(input), { url: null, error }, JSON.stringify(input));
  }
});

test('parseUpdateUrl splits a matching URL; no port means 80', () => {
  assert.deepEqual(core.parseUpdateUrl('http://192.168.1.112:5080/api/panel/livingroom'),
    { address: '192.168.1.112', port: 5080, deviceId: 'livingroom' });
  assert.deepEqual(core.parseUpdateUrl('http://server/api/panel/a'), { address: 'server', port: 80, deviceId: 'a' });
});

test('parseUpdateUrl returns null for URLs that do not match the pattern', () => {
  for (const u of ['https://h:5080/api/panel/a', 'http://h:5080/api/panel/Bad', 'http://h:5080/api/panel/a/extra',
    'http://h:5080/other/a', '', null]) {
    assert.equal(core.parseUpdateUrl(u), null, String(u));
  }
});

test('build(parse(url)) round-trips', () => {
  const u = 'http://10.0.0.5:5080/api/panel/kitchen';
  assert.equal(core.buildUpdateUrl(core.parseUpdateUrl(u)).url, u);
});

test('defaultAddress prefers the server LAN address and never suggests localhost', () => {
  assert.equal(core.defaultAddress({ addresses: ['192.168.1.112'] }, 'localhost'), '192.168.1.112');
  assert.equal(core.defaultAddress({ addresses: [] }, 'localhost'), '');
  assert.equal(core.defaultAddress(null, '127.0.0.1'), '');
  assert.equal(core.defaultAddress(null, 'walli.lan'), 'walli.lan');
});

test('luminance uses Rec. 709 on gamma values and composites alpha over white', () => {
  assert.equal(core.luminance(255, 255, 255, 255), 255);
  assert.equal(core.luminance(0, 0, 0, 0), 255);
  assert.equal(Math.round(core.luminance(255, 0, 0, 255)), 54);
});

const grey = (values) => {
  const rgba = new Uint8ClampedArray(values.length * 4);
  values.forEach((v, i) => rgba.set([v, v, v, 255], i * 4));
  return rgba;
};

test('quantizeToLevels without dither maps to the nearest of 0/85/170/255', () => {
  assert.deepEqual([...core.quantizeToLevels(grey([0, 85, 170, 255]), 4, 1, false)], [0, 1, 2, 3]);
  assert.deepEqual([...core.quantizeToLevels(grey([42, 43]), 2, 1, false)], [0, 1]);
});

test('quantizeToLevels with dither mixes neighbouring levels and keeps the mean', () => {
  const levels = core.quantizeToLevels(grey(new Array(256).fill(128)), 16, 16, true);
  const used = new Set(levels);
  assert.deepEqual([...used].sort(), [1, 2]);
  const mean = levels.reduce((s, l) => s + l, 0) / levels.length;
  assert.ok(Math.abs(mean * 85 - 128) <= 4, `mean ${mean * 85}`);
});

test('levelsToRgba renders levels as grey pixels', () => {
  assert.deepEqual([...core.levelsToRgba(Uint8Array.of(0, 1, 2, 3), 4, 1)],
    [0, 0, 0, 255, 85, 85, 85, 255, 170, 170, 170, 255, 255, 255, 255, 255]);
});

test('coverZoom scales so the image covers the frame', () => {
  assert.equal(core.coverZoom(1584, 544, 792, 272), 0.5);
  assert.equal(core.coverZoom(100, 40, 792, 272), 7.92);
  assert.ok(Math.abs(core.coverZoom(4000, 300, 792, 272) - 0.90667) < 1e-4);
});

test('clampPan never shows a gap at the frame edges', () => {
  assert.deepEqual(core.clampPan({ x: 50, y: 50 }, 1, 1000, 400, 792, 272), { x: 0, y: 0 });
  assert.deepEqual(core.clampPan({ x: -500, y: -500 }, 1, 1000, 400, 792, 272), { x: -208, y: -128 });
});

test('relativeTime', () => {
  const now = Date.parse('2026-11-21T12:00:00Z');
  const ago = (s) => new Date(now - s * 1000).toISOString();
  assert.equal(core.relativeTime(null, 0), 'never');
  assert.equal(core.relativeTime(ago(30), now), 'just now');
  assert.equal(core.relativeTime(ago(5 * 60), now), '5 min ago');
  assert.equal(core.relativeTime(ago(3 * 3600), now), '3 h ago');
  assert.equal(core.relativeTime(ago(2 * 86400), now), '2 d ago');
});

// ---- final review fixes

const ADDRESS_ERROR = 'Address: IP or host name only (no http://, port or path).';

test('buildUpdateUrl rejects addresses that are not a plain IPv4 or host name', () => {
  for (const address of ['http://192.168.1.10', '192.168.1.10:5080', '192.168.1.10/x', 'my host', 'fe80::1', '-bad.lan', 'bad-.lan', 'a..b']) {
    assert.deepEqual(core.buildUpdateUrl({ address, port: 5080, deviceId: 'a' }), { url: null, error: ADDRESS_ERROR }, address);
  }
  for (const address of ['192.168.1.10', 'walli-server', 'walli.lan', 'Server-2.home.arpa']) {
    assert.equal(core.buildUpdateUrl({ address, port: 5080, deviceId: 'a' }).error, null, address);
  }
});

test('parseUpdateUrl accepts exactly the hosts that buildUpdateUrl accepts', () => {
  assert.equal(core.parseUpdateUrl('http://my%20host:5080/api/panel/a'), null);
  assert.equal(core.parseUpdateUrl('http://-bad:5080/api/panel/a'), null);
  assert.deepEqual(core.parseUpdateUrl('http://walli.lan:5080/api/panel/a'), { address: 'walli.lan', port: 5080, deviceId: 'a' });
});

test('storedUrlWarning names the stored URL when it does not match the pattern', () => {
  assert.equal(core.storedUrlWarning(''), null);
  assert.equal(core.storedUrlWarning(null), null);
  assert.equal(core.storedUrlWarning('http://10.0.0.5:5080/api/panel/kitchen'), null);
  assert.equal(core.storedUrlWarning('https://old.example/api/panel/kitchen'),
    'The panel\'s saved URL "https://old.example/api/panel/kitchen" doesn\'t match http://address:port/api/panel/id — saving will replace it.');
});

test('isStale allows for the interval the server last gave the panel (night pause)', () => {
  const now = Date.parse('2026-11-22T03:00:00Z');
  const iso = (msAgo) => new Date(now - msAgo).toISOString();
  // 5 h ago, but the server told it to sleep 9 h (night pause): healthy.
  assert.equal(core.isStale(iso(5 * 3600e3), 9 * 3600, 900, now), false);
  // Overdue: last interval 900 s, seen 50 min ago (> 900 + 2 x 900 = 45 min).
  assert.equal(core.isStale(iso(50 * 60e3), 900, 900, now), true);
  // No last interval known: fall back to 3 x interval.
  assert.equal(core.isStale(iso(40 * 60e3), null, 900, now), false);
  assert.equal(core.isStale(iso(46 * 60e3), null, 900, now), true);
  // Never seen.
  assert.equal(core.isStale(null, null, 900, now), true);
});
