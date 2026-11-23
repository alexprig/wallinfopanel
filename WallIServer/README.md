# WallIServer

Update server and setup page for the WallInfoPanel ESP8266 e-paper panel (792 × 272, 4 grey levels).

- **Device endpoint** `GET /api/panel/{deviceId}`: serves the current image as `gray2` (full image, a region, or 304) plus parameter headers (`X-Interval` with the night pause applied, `X-Low-Batt-mV`, `X-Url`).
- **Admin API** `/api/devices/...`: manage devices, upload images (PNG/JPEG/BMP/GIF/WebP), preview them as PNG. Needs `X-Api-Key`.
- **Setup page** `/setup/`: configures a panel over USB with Web Serial. Needs no admin key.
- **Admin page** `/admin/`: device list with status (last seen, battery, Wi-Fi signal, firmware, last error), editable settings, add device, new token, delete, and picture upload with zoom/crop and a live 4-grey preview. Asks for the admin key once per browser tab.
- **Server info** `GET /api/server-info`: the server's private LAN IPv4 addresses and port (public; used by the setup page to suggest the update URL).

The contract is [`../docs/protocol.md`](../docs/protocol.md) (image format §1, device endpoint §2, serial protocol §3, error names §4, admin API §5).

## Requirements

- .NET SDK 10 (`global.json` pins 10.0.403 with `latestFeature` roll-forward)
- Images are processed with SkiaSharp. Native assets for macOS/Windows come with `SkiaSharp`; Linux (x64/arm64, including Raspberry Pi OS 64-bit) uses `SkiaSharp.NativeAssets.Linux.NoDependencies`.

## Run

```sh
cd src/WallIServer
dotnet run                 # Development: admin key "dev-admin-key-change-me", listens on http://0.0.0.0:5080
```

Production-style run:

```sh
dotnet publish src/WallIServer -c Release -o out
WallIServer__AdminApiKey='a-long-random-secret' ./out/WallIServer
```

Then open `http://localhost:5080/setup/`. The panel reaches the server through its LAN address, e.g. `http://192.168.1.10:5080/api/panel/livingroom`, so allow port 5080 through the firewall.

## Configuration

`appsettings.json`, section `WallIServer` (override with environment variables such as `WallIServer__AdminApiKey`):

| Key | Default | Meaning |
|-----|---------|---------|
| `AdminApiKey` | — (**required**, at least 8 chars; the app refuses to start without it) | Value of the `X-Api-Key` header for the admin API. A dev value is only in `appsettings.Development.json` |
| `DataDirectory` | `./data` | Storage root, relative to the content root (`src/WallIServer` with `dotnet run`) |
| `MaxVersionsPerDevice` | `8` | Image versions kept per device for region updates |
| `RegionMaxRatio` | `0.6` | A region is sent only if `2·h·w/8 ≤ ratio · 53856` |

The listen address is `Kestrel:Endpoints:Http:Url` (`http://0.0.0.0:5080`). Override it with `Kestrel__Endpoints__Http__Url=http://0.0.0.0:8080`.

### Storage layout

```
data/devices/{id}/device.json          settings, telemetry, SHA-256 hash of the token, current ETag, version list
data/devices/{id}/versions/{etag}.g2   last N gray2 bodies (53,856 B each), oldest pruned
```

Every write goes to a temp file first and is then renamed into place. Requests for the same device are serialised by a per-device lock. Back up the `data` folder to keep devices and images.

## Register a device and upload an image

```sh
B=http://localhost:5080
K='X-Api-Key: dev-admin-key-change-me'

# 1. Create the device. The token appears only in this response.
curl -s -X POST $B/api/devices -H "$K" -H 'Content-Type: application/json' \
  -d '{"id":"livingroom","name":"Living room","intervalSeconds":900,"nightStart":"23:00","nightEnd":"06:00","timeZone":"Europe/Berlin","lowBattMv":3300}'
# → 201 {"device":{...},"token":"GJWPObuPv76..."}

# 2. Upload an image (fit=contain|cover|stretch, dither=true|false)
curl -s -X POST "$B/api/devices/livingroom/image?fit=cover&dither=true" -H "$K" -F file=@photo.jpg
# → {"etag":"\"e29dfc7910449de3\"","bytes":53856,"changedRegion":{...}}

# 3. Paste a small image unscaled at x,y onto the current image (creates a partial change → region update)
curl -s -X POST "$B/api/devices/livingroom/image?x=700&y=20" -H "$K" -F file=@badge.png

# 4. Look at what the panel will show
curl -s -H "$K" -o preview.png $B/api/devices/livingroom/image.png

# Other admin calls
curl -s -H "$K" $B/api/devices                          # list with telemetry
curl -s -H "$K" $B/api/devices/livingroom               # one device
curl -s -X PUT $B/api/devices/livingroom -H "$K" -H 'Content-Type: application/json' -d '{"intervalSeconds":1800}'
curl -s -X POST -H "$K" $B/api/devices/livingroom/token # rotate the token (old one stops working)
curl -s -X DELETE -H "$K" $B/api/devices/livingroom     # 204
```

`PUT` is a partial update: omitted fields stay as they are, and `""` clears `nightStart`/`nightEnd`/`pendingUrl`. Validation errors come back as `400` ProblemDetails with an `errors` object. Rules: `id` `^[a-z0-9-]{1,32}$`, `intervalSeconds` 180–86400, `lowBattMv` 2800–4200, `nightStart`/`nightEnd` `HH:mm` and set together, `timeZone` an IANA id known to the OS (default `UTC`).

**Moving a device to another URL:** set `pendingUrl` (`http://…`, ≤ 160 chars) with `PUT`. The server then sends it as `X-Url` on every update, until a request arrives at exactly that URL on this server, or until you clear it with `"pendingUrl":""`.

### Simulate the panel

```sh
TOKEN=...   # from step 1
curl -s -D - -o img.g2 -H "Authorization: Bearer $TOKEN" \
  -H 'X-Fw: 0.1.0' -H 'X-Batt-mV: 3950' -H 'X-Rssi: -58' -H 'X-Boot: init' -A 'WallISetup/0.1.0' \
  $B/api/panel/livingroom
# 200, ETag, X-Format: gray2, X-Interval, X-Low-Batt-mV, Content-Length: 53856

curl -s -D - -o /dev/null -H "Authorization: Bearer $TOKEN" -H 'If-None-Match: "e29dfc7910449de3"' $B/api/panel/livingroom
# 304 if that is still current; 200 + X-Region/X-Base + a short body if it is an older stored version
```

## Set up a panel (setup page)

1. Slide the panel's setup switch to **SETUP** and plug it into USB.
2. Open **`http://localhost:5080/setup/`** in Chrome or Edge on a desktop. Web Serial needs a secure context: `localhost` or HTTPS. A plain-HTTP LAN address won't work.
3. Click **Connect** and pick "USB-SERIAL CH340". The page waits for the panel's `ready` event, then reads `info` and `get`.
4. Fill in the fields. **Scan Wi-Fi** fills the SSID list. Under **Server**, enter **Address** (defaults to this server's LAN IP), **Port** (default 5080) and **Device ID**; the page shows the resulting `http://<address>:<port>/api/panel/<id>` and saves it. Paste the token from the admin page (**Add device** / **New token**). Fields showing "unchanged" (Wi-Fi password, token) keep their stored value unless you type something.
5. Click **Save**. Then **Test Wi-Fi** / **Test update** if you like. Tests use the *saved* settings.
6. Slide the switch back to **RUN**. The panel restarts and reports the result on its LED.

The log console shows the raw serial lines. Secrets are masked in the log.

## Manage devices (admin page)

Open **`http://localhost:5080/admin/`** and enter the admin key (`WallIServer:AdminApiKey`). It is kept in `sessionStorage` for that tab only.

- **+ Add device**: id, name, interval, night pause, time zone, low-battery level. The token is shown once, with a copy button.
- Each card shows the status from the panel's last contact and refreshes every 30 s. "Last seen" turns red after 3 missed intervals.
- **Upload picture…** (or drop an image on the picture): drag to move, scroll or slider to zoom (cover … 8×), **Fit whole image**, **Dither**. The right pane shows exactly what the panel will display. **Send to server** uploads an exact 792×272 PNG.
- **New token** invalidates the old one immediately; **Delete** asks you to type the device id.

## Development

```sh
dotnet build            # warnings are errors
dotnet test             # xUnit v3 on Microsoft.Testing.Platform (see global.json)
node --test tests/js/   # page logic: URL builder, 4-grey quantiser, crop maths
```

- In Development the OpenAPI document is at `/openapi/v1.json`.
- `WallIServer.http` has sample requests for the VS Code REST Client and Rider/Visual Studio.

Project layout:

```
src/WallIServer/
  Imaging/     gray2 packing, Floyd–Steinberg quantiser, SkiaSharp composer, regions, PNG preview, ETag
  Storage/     file-based device store (atomic writes, per-device locks)
  Scheduling/  night-window interval calculation
  Security/    token hashing, constant-time compares, admin key filter
  Endpoints/   device endpoint and admin API
  wwwroot/setup/  setup page (plain HTML/CSS/JS, Web Serial)
  wwwroot/admin/  admin page (device list, settings, crop/upload)
  wwwroot/js/walli-core.js  pure logic shared by both pages (also loaded by node --test)
tests/WallIServer.Tests/
tests/js/
```
