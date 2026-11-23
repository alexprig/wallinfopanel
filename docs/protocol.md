# WallInfoPanel — Protocol v1 (device ↔ server, setup page ↔ device)

*Date: 2026-11-21. This is the authoritative contract between **WallISetup** (ESP8266 firmware) and **WallIServer** (.NET API + setup page). Both implementations must follow it exactly.*

---

## 1. Image format `gray2`

| Property | Value |
|----------|-------|
| Size | 792 × 272 px, landscape, row 0 = top, column 0 = left |
| Levels | 4: **0 = black, 1 = dark grey, 2 = light grey, 3 = white** |
| Encoding | Two **bit planes**. Each plane is 1 bit/pixel, **99 bytes per row**, 272 rows, row-major, **MSB = leftmost pixel** of the byte |
| Plane size | 99 × 272 = **26,928 B** |
| Plane 0 | **bit 0** (LSB) of each pixel value. Goes to controller RAM `0x24` (master half) / `0xA4` (slave half) |
| Plane 1 | **bit 1** (MSB) of each pixel value. Goes to controller RAM `0x26` / `0xA6` |
| Full image body | plane 0 ‖ plane 1 = **53,856 B** |

The mapping comes from the Waveshare reference driver (`EPD_5in79_4GrayDisplay`): white = `11`, light grey = `10`, dark grey = `01`, black = `00`. RAM `0x24` gets the low bit, `0x26` the high bit.

**Example:** a white pixel (3 = `11`) is 1 in both planes. A black pixel (0) is 0 in both. Light grey (2 = `10`) is 0 in plane 0 and 1 in plane 1.

### 1.1 Region
A rectangle `x,y,w,h` with:
- `x % 8 == 0`, `w % 8 == 0`, `w ≥ 8`, `h ≥ 1`
- `x + w ≤ 792`, `y + h ≤ 272`

**Region body:** plane 0 region rows ‖ plane 1 region rows. Each region row is `w/8` bytes, `h` rows per plane, so the length is **`2 · h · w/8`** bytes.

A region byte at plane `p`, region row `r`, region byte column `c` maps to full-image offset `p·26928 + (y+r)·99 + x/8 + c`.

---

## 2. Update endpoint (device → server)

```
GET /api/panel/{deviceId}
```
`deviceId` matches `^[a-z0-9-]{1,32}$`. The device's configured `url` is the full URL of this endpoint, e.g. `http://192.168.1.10:5080/api/panel/livingroom`.

### 2.1 Request headers

| Header | Required | Value |
|--------|----------|-------|
| `Authorization` | yes | `Bearer <token>` |
| `If-None-Match` | no | The ETag of the image the device currently shows, **byte-for-byte as received** (including quotes). Omitted on an initialisation run, when no valid image is stored, or on the retry after a rejected region |
| `X-Fw` | yes | Firmware version, e.g. `0.1.0` |
| `X-Batt-mV` | yes | Battery voltage in mV (integer) |
| `X-Rssi` | yes | Wi-Fi RSSI in dBm (integer) |
| `X-Boot` | yes | `init` (power-on / reset / after setup) or `timer` (deep-sleep wake) |
| `X-Last-Error` | no | Error name (§4) of the previous failed cycle |
| `User-Agent` | yes | `WallISetup/<fw>` |

### 2.2 Responses

| Status | Headers | Body | Device action |
|--------|---------|------|---------------|
| **200, full image** | `ETag`, `X-Format: gray2`, `Content-Type: application/octet-stream`, `Content-Length: 53856`, parameter headers | 53,856 B | Replace the stored image, store the ETag, redraw |
| **200, region** | as above plus `X-Region: x,y,w,h` and `X-Base: <etag>`, `Content-Length: 2·h·w/8` | region body | Apply **only if** `X-Base` equals the device's stored ETag **and** a valid stored image exists. Otherwise discard and **retry once in the same wake without `If-None-Match`**. After applying: store the new ETag, redraw the full screen |
| **304 Not Modified** | `ETag`, parameter headers | none | Apply the parameters, no redraw |
| **401** | `WWW-Authenticate: Bearer` | ProblemDetails | Wrong token **or unknown device id**. Both look identical, so ids can't be discovered without a token. Error `auth_rejected` |
| other | — | — | Error `bad_response` (`server_unreachable` on a connect failure or timeout) |

A `200` with an `X-Format` other than `gray2`, a missing or wrong `Content-Length`, an invalid `X-Region`, or a short body → error `bad_response`. The previously shown image stays.

### 2.3 Parameter headers (on 200 and 304)

| Header | Meaning | Device rule |
|--------|---------|-------------|
| `X-Interval` | Seconds until the next wake. **The server already accounts for the night pause**: during the night it sends the seconds until the night ends | Clamp to **180 … 86400**, persist if changed |
| `X-Low-Batt-mV` | Low-battery threshold for the LED warning on init runs | Accept 2800 … 4200, persist if changed |
| `X-Url` | Move the device to a new update URL | Must start with `http://` and be ≤ 160 chars. Stored as *pending*: the next cycle tries it. On success it becomes `url`; on failure the device falls back to the old `url` and drops the pending one |

### 2.4 Server rules
- **ETag:** `"` + the first 16 hex chars of SHA-256(full image body) + `"`.
- **Region selection:** the server keeps the last **8 image versions** per device, keyed by ETag.
  - If `If-None-Match` equals the current ETag: respond **304**.
  - If it matches an older stored version: compute the bounding box of the differing pixels and widen it to the 8-pixel grid. If `2·h·w/8 ≤ 0.6 · 53856`, send a **region** with `X-Base` = that older ETag. Otherwise send the **full image**.
  - Unknown or missing `If-None-Match`: send the **full image**.
- **Interval and night:** each device has `intervalSeconds` (default 900), an optional `nightStart`/`nightEnd` (`HH:mm`) and a `timeZone` (IANA, e.g. `Europe/Berlin`). If `now + interval` falls inside the night window, `X-Interval` = seconds until `nightEnd` (local time), capped at 86400.
- **Telemetry:** store last-seen time, firmware, battery, RSSI, boot type and last error from the request headers.
- **No image yet** for a device: respond **404** with JSON `{"error":"no_image"}`. The device reports `bad_response`.

---

## 3. Setup serial protocol (setup page ↔ device)

- **Link:** USB serial 115200 8N1 through the CH340G, with **one JSON object per line** (`\n`). Lines that aren't JSON (ROM boot noise at 74880 baud) are ignored by both sides.
- **Request:** `{"id":<int>,"cmd":"<name>", ...}`
- **Response:** `{"id":<same>,"ok":true, ...}` or `{"id":<same>,"ok":false,"err":"<code>","msg":"<text>"[,"field":"<key>"]}`
- **Events** (no `id`): `{"evt":"ready","fw":"0.1.0","mode":"setup"}` once the device is ready to receive commands, and `{"evt":"log","msg":"..."}`.
- Maximum line length: 1024 bytes.

| `cmd` | Extra request fields | Success response fields |
|-------|----------------------|-------------------------|
| `info` | — | `fw`, `mac`, `chip` (`"ESP8266"`), `flash` (bytes), `heap` (bytes), `batt_mV`, `hasImage` (bool), `etag` (string, may be empty) |
| `get` | — | `cfg`: the configuration object (§3.1), with secrets replaced by `"***"` when set or `""` when unset |
| `set` | `cfg`: full or partial configuration object | — . A secret field that is absent, empty or `"***"` keeps its stored value. Validation errors: `err:"invalid"`, `field`, `msg` |
| `scan` | — | `nets`: `[{"ssid","rssi","ch","enc"}]`, sorted by RSSI, duplicates removed |
| `test_wifi` | — | `ip`, `rssi`, `ms`. Failure: `err` = error name (§4) |
| `test_update` | — | `http` (status code), `bytes`, `region` (bool), `interval`, `ms`. Runs one complete update cycle (fetch + store + redraw). Failure: `err` = error name |
| `test_display` | — | `ms`. Draws a test pattern (4 grey bars + text) |
| `factory_reset` | `confirm: true` | — . Erases the configuration and the stored image |
| `reboot` | — | — . The device answers, then restarts |
| `run` | — | `interval`. The device answers, runs one update cycle (like an init run, with LED result code), then deep-sleeps. The next boot follows the switches: with the setup (CH340) switch still ON it comes back in setup mode. Fails with `no_config` when nothing is saved. Not on the setup page: the switch is the way to leave setup |

### 3.1 Configuration object
Dotted keys mean **nested objects**: `{"name":…, "wifi":{"ssid":…,"pass":…}, "net":{"ip":…,"gw":…,"mask":…,"dns":…}, "url":…, "token":…, "interval":…, "lowBatt_mV":…, "battScale_mV":…}`.

**Known limitation (v1):** an empty secret means "keep", so a stored Wi-Fi password can only be removed (switching to an open network) with `factory_reset`.


| Key | Type | Rule | Default |
|-----|------|------|---------|
| `name` | string | 1–32 chars | `wallpanel-<last 4 hex of MAC>` |
| `wifi.ssid` | string | 1–32 chars, **required** | — |
| `wifi.pass` | string, secret | empty (open network) or 8–63 chars | — |
| `net.ip`, `net.gw`, `net.mask`, `net.dns` | string | dotted IPv4. Either all four empty (DHCP) or `ip`, `gw`, `mask` valid (`dns` optional, defaults to `gw`) | all empty |
| `url` | string | starts with `http://`, ≤ 160 chars, **required** | — |
| `token` | string, secret | 1–64 chars, **required** | — |
| `interval` | int | 180 … 86400 s | 900 |
| `lowBatt_mV` | int | 2800 … 4200 | 3300 |
| `battScale_mV` | int | 1000 … 15000. Battery mV that reads as 1023 on A0 (depends on the divider) | 4200 |

---

## 4. Error names and LED codes

| LED blinks | Name | Meaning |
|------------|------|---------|
| solid 3 s | `ok` | Success |
| 2 | `no_config` | Missing or invalid configuration |
| 3 | `wifi_not_found` | SSID not found |
| 4 | `wifi_auth` | Wi-Fi password rejected |
| 5 | `no_ip` | Connected but no IP (DHCP / static IP problem) or connect timeout |
| 6 | `server_unreachable` | DNS failure, connection refused, timeout |
| 7 | `auth_rejected` | HTTP 401 / 403 |
| 8 | `bad_response` | Any other HTTP status or malformed response |
| 9 | `display` | E-paper BUSY timeout |
| 3 fast double-blinks before the code | `low_battery` | Battery below `lowBatt_mV` (init runs only) |

---

## 5. Admin API (server only)

All admin endpoints require the header `X-Api-Key: <WallIServer:AdminApiKey>`.

| Method | Path | Body / query | Response |
|--------|------|--------------|----------|
| GET | `/api/devices` | — | list of devices (without token) with telemetry |
| POST | `/api/devices` | JSON `{id, name?, intervalSeconds?, nightStart?, nightEnd?, timeZone?, lowBattMv?, pendingUrl?}`. Defaults: name = id, interval 900, timeZone `UTC`, lowBattMv 3300. 409 if the id exists | **201** `{device, token}`. The token is shown only once |
| GET | `/api/devices/{id}` | — | device + telemetry + current ETag |
| PUT | `/api/devices/{id}` | JSON with the same fields as POST (except `id`). **Partial update**; `""` clears `nightStart`, `nightEnd`, `pendingUrl` | updated device |
| DELETE | `/api/devices/{id}` | — | 204 |
| POST | `/api/devices/{id}/token` | — | `{token}` (rotates it; the old token stops working) |
| POST | `/api/devices/{id}/image` | multipart field `file` (PNG/JPEG/BMP/GIF/WebP). Query: `fit=contain\|cover\|stretch` (default `contain`, white padding), `dither=true\|false` (default `true`, Floyd–Steinberg), optional `x`,`y` (paste an image without resizing at that position onto the current image; creates a partial change) | `{etag, bytes, changedRegion}`: `etag` quoted exactly like the header, `bytes` = 53,856 (stored body), `changedRegion` = 8-px aligned box vs the previous image |
| GET | `/api/devices/{id}/image.png` | — | PNG preview of the current gray2 image (4 grey levels) |
| GET | `/api/server-info` | — (**no key**, public) | `{addresses: string[], port: number}`: the server's private (RFC 1918) LAN IPv4 addresses and listening port. Never reports public/VPN addresses |

### 5.1 Server behaviour notes
- **Night window** is `[nightStart, nightEnd)` in the device's time zone. It may cross midnight. `nightStart == nightEnd` is rejected. Across DST changes the first `nightEnd` at or after the wake time is used.
- **`X-Url`** is sent while the device's `pendingUrl` is set. The server clears it when a request arrives at exactly that URL, or when the admin sets it to `""`.
- **Missing telemetry headers** are accepted; whatever is present is recorded.
- **Behind a reverse proxy:** make sure custom headers on `304` responses (`X-Interval`) are passed through.
