# WallInfoPanel — Firmware, Setup & Update Protocol Design

*Date: 2026-11-21 · Companion to [hardware-spec.md](hardware-spec.md)*

## 1. Decisions so far

| # | Decision |
|---|----------|
| D1 | Transport is **plain HTTP** (no TLS). The update server lives on the LAN or behind a reverse proxy that adds HTTPS on the server side |
| D2 | **Setup over USB.** One **setup switch** powers the CH340G (3V3 → CH340G VCC). ON = setup mode (sensed on RX), OFF = run. A **web page** (Web Serial) writes all settings to the board |
| D3 | **Each update call returns the image *and* updated parameters** in a single HTTP request |
| D4 | After setup, the switch goes back to RUN. The board initialises, **reports the result with LED blink codes**, waits, then turns the LED off and starts its normal cycle |
| D5 | **Toolchain: VS Code + PlatformIO**, Arduino framework for ESP8266 (C++). Firmware project: [`WallISetup/`](../WallISetup/). Server + setup page: [`WallIServer/`](../WallIServer/) (.NET) |

### Toolchain & libraries (D5)

| Purpose | Choice | Version (pinned in `platformio.ini`) |
|---------|--------|--------------------------------------|
| IDE / build system | VS Code + PlatformIO IDE extension (CLI: `pio`) | — |
| Platform / core | `espressif8266` (ESP8266 Arduino core 3.1.x) | 4.2.1 |
| Board | `nodemcuv2` (4 MB flash, 1 MB LittleFS) | — |
| E-paper driver | Own 4-grey driver (`src/Epd579.cpp`), ported from Waveshare `EPD_5in79.c`. GxEPD2 dropped: no grey support for this panel | — |
| Graphics / fonts | `adafruit/Adafruit GFX Library` (`GFXcanvas1` strips for status screens) | ^1.13.0 |
| JSON (config + serial protocol) | `bblanchon/ArduinoJson` | ^7.4.3 |
| Wi-Fi, HTTP client, LittleFS, deep sleep, RTC memory | built into the ESP8266 core | — |
| Unit tests | Unity, `native` environment on the host | — |
| Setup page | Plain HTML + JavaScript, Web Serial API (optional later: esptool-js for flashing from the browser) | — |
| Optional | `olikraus/U8g2_for_Adafruit_GFX` if on-device screens need Unicode fonts | — |

---

## 2. Operating modes

```
                 power-on / RST button / switch moved back to RUN
                                   │
                                   ▼
                      ┌──────────────────────────┐
                      │ Boot: read setup sense   │
                      │ (RX pin, CH340 powered?) │
                      └─────┬──────────────┬─────┘
                SETUP (high)           RUN (low)
                        │                      │
                        ▼                      ▼
        ┌───────────────────────────┐   ┌────────────────────────────────┐
        │ SETUP MODE                │   │ RUN MODE                        │
        │ • Serial 115200 on        │   │ • Serial OFF, TX/RX as inputs   │
        │ • e-paper: "Setup mode"   │   │ • load config, connect Wi-Fi    │
        │ • LED: slow breathe/blink │   │ • HTTP update (image + params)  │
        │ • JSON commands from page │   │ • refresh e-paper if changed    │
        │ • never sleeps            │   │ • first boot only: LED code     │
        │ • switch moved to RUN     │   │ • deep sleep for <interval>     │
        │   → ESP.restart()         │   └───────────────┬─────────────────┘
        └───────────────────────────┘                   │ RTC timer (D0→RST)
                                                        └──────► RUN MODE again
```

- **First boot vs timer wake:** the firmware checks `ESP.getResetInfoPtr()->reason`.
  - `REASON_DEEP_SLEEP_AWAKE`: a normal periodic update. **The LED stays off** to save energy.
  - Anything else (power-on, RST button, restart after setup): an **initialisation run**. It shows the LED result code (§6) and also draws a status screen on the e-paper.
- **Mode rule (`walli::decideBootMode`, lib/core/BootMode.h):** the setup (CH340) switch decides on **every** boot, deep-sleep wakes included. ON → SETUP, OFF → RUN.
  - Why not only on a fresh start: the ESP8266 reports an **RST press during deep sleep as a deep-sleep wake** (`REASON_DEEP_SLEEP_AWAKE`), so the reset reason can't tell a manual RST from the timer.
  - Turning the switches ON while the panel sleeps enters setup at the next wake, or at once with RST.
  - Leave setup mode by turning the switches OFF: the firmware restarts by itself.

---

## 3. Hardware for the setup switch

### 3.1 Setup switch: CH340G power
- **One switch selects the mode.** It powers the CH340G only during setup, which also saves the CH340G's current on battery.
- On the LoLin V3 the CH340G normally runs from the **3.3 V rail**. Battery power reaches it even in deep sleep and wastes a few mA.
- Cut the trace to **CH340G pin 16 (VCC)** and route it through a small slide switch to 3V3. If pin 4 (V3) is tied to pin 16, it moves with it.
- **Check first:** plug in USB and measure pin 16.
  - **3.3 V:** use the design above.
  - **5 V:** the chip is powered from USB only. Then "USB plugged in" is the setup signal and no switch is needed.
- **Flashing** needs the switch ON (the CH340 does the upload). After the upload the board starts in setup mode.

### 3.2 Mode detection on RX
- Add a **100 kΩ pull-down from RX (GPIO3) to GND**.
- **CH340 ON:** its TXD idles HIGH and pulls RX high through the board's series resistor (~470 Ω, which easily beats 100 kΩ). The firmware reads **HIGH → SETUP**.
- **CH340 OFF:** the pull-down holds RX LOW. The firmware reads **LOW → RUN**.
- The firmware switches GPIO3 to a plain input (pull-up off) before reading it, and reads it on **every** boot (§2 mode rule).
- **Leaving setup:** in setup mode the firmware samples RX every 10 ms (via the UART status bit `USRXD`; the GPIO register reads LOW while the pin is in UART mode). If it stays LOW for 500 ms (impossible during UART traffic), it calls `ESP.restart()` and comes back in RUN mode. **Turning the switch OFF is enough. No RST press is needed.**
- **Entering setup:** switch ON, then press **RST** (or wait for the next wake).
- **Not SD3 (GPIO10):** tried as a separate setup switch to G. Pulled LOW, it stops the boot ROM loading from flash on this module (`rst cause:2, boot mode:(3,5)` and nothing after). SD2 (GPIO9) is a flash line too. Leave both unconnected.

### 3.3 Leakage rules in RUN mode
- Never call `Serial.begin()` in RUN mode. Leave GPIO1 (TX) and GPIO3 (RX) as **inputs**. A HIGH TX into an unpowered CH340 back-feeds it through its ESD diodes and costs hundreds of µA.
- Before deep sleep, drive **all EPD signal lines LOW** after PWR goes LOW, so nothing back-powers the e-paper board.

### 3.4 Power during setup
- After the low-power modification the AMS1117 is gone, so **USB carries data only** and the board runs from its battery during setup.
- A TP4056 charger fed from the same USB connector charges the cell at the same time.

### 3.5 Status LED
- **GPIO2 (D4)**, the blue LED on the ESP-12 module, **active LOW**.
- The module LED is hidden once the board is in a frame. Add an **external 3 mm LED + 1 kΩ from 3V3 to D4**, in parallel with the module LED, and place it where it's visible.
- Wiring the LED to 3V3 also keeps GPIO2 HIGH at boot, which the boot strap requires.

### 3.6 Revised pin map

| Signal | NodeMCU | GPIO | Change vs hardware-spec v1 |
|--------|---------|------|----------------------------|
| Deep-sleep wake | D0 ↔ RST | 16 | — |
| EPD PWR | D1 | 5 | — |
| EPD BUSY | D2 | 4 | — |
| EPD DC | D3 | 0 | — |
| **Status LED** (active LOW) | **D4** | **2** | **new**, freed by moving EPD RST |
| EPD CLK | D5 | 14 | — |
| **EPD RST** | **D6** | **12** | **moved from D4** (config button dropped, setup is via USB now) |
| EPD DIN | D7 | 13 | — |
| EPD CS | D8 | 15 | — |
| **Setup sense** | **RX** | **3** | **new**: 100 kΩ pull-down, CH340 TXD pulls high while the setup switch powers it |
| Battery voltage | A0 | ADC | — |

---

## 4. Setup web page (Web Serial)

A static page served by **WallIServer** at `/setup/` (`WallIServer/src/WallIServer/wwwroot/setup/`). It talks to the board over USB through the browser's **Web Serial API**.
- **Browsers:** Chrome, Edge or Opera on desktop. Firefox and Safari don't support Web Serial.
- **Secure context required:** serve the page over `https://` or `http://localhost`, for example `python3 -m http.server` in the folder, or host it on the update server behind HTTPS. Opening it from `file://` isn't reliable.
- **No installation:** the user plugs in the board and clicks **Connect**, and the browser lists the CH340 port.

### 4.1 User flow
1. Turn the **setup switch ON** (CH340 power) and press **RST**. The board starts in setup mode, the e-paper shows "Setup mode", and the LED blinks slowly.
2. Connect USB and open the setup page. Click **Connect** and pick the "USB-SERIAL CH340" port.
3. The page sends `info` and `get`. It shows the firmware version, MAC address, battery level and current settings (secrets masked).
4. Edit the fields. Optional: **Scan Wi-Fi** fills the SSID list, **Test Wi-Fi** checks the credentials, **Test update** fetches from the URL once and draws the result on the e-paper.
5. Click **Save**. The board validates, writes the settings to flash and confirms.
6. Turn the **setup switch OFF**. The board restarts by itself, initialises, and reports the result on the LED and the e-paper (§6).

> When the port opens, the CH340's DTR/RTS lines may pulse the NodeMCU auto-reset circuit and restart the ESP once. That's harmless: the switch is still on SETUP, so it comes back in setup mode. The page waits for the `{"evt":"ready"}` line before sending commands. After opening, the page calls `port.setSignals({dataTerminalReady:false, requestToSend:false})`.

### 4.2 Configuration fields and serial protocol
The configuration keys, validation rules and the JSON-lines serial protocol are defined in **[protocol.md §3](protocol.md#3-setup-serial-protocol-setup-page--device)**.
- The night pause and timezone moved to the **server**, which folds them into `X-Interval`, so the device needs no clock.
- **Storage:** `/config.json` on LittleFS, written atomically (write a temp file, then rename).
- **Secrets:** `wifi.pass` and `token` are write-only. `get` returns `"***"`.
- **Fast-connect cache:** the Wi-Fi channel, BSSID and IP settings go to RTC memory (not flash).

---

## 5. Update protocol
Defined in **[protocol.md §1–2](protocol.md)**: `gray2` image format (two bit planes, 53,856 B), region updates (`X-Region` / `X-Base`), and the parameter headers (`X-Interval`, `X-Low-Batt-mV`, `X-Url`).

Key decisions:
- **Format:** `gray2` only (4 grey levels). Every redraw is a full-screen grey refresh. Regions only reduce the download.
- **Stored image:** the device keeps the current image in flash (`/img.g2`, 53,856 B). Region updates patch this file in place, then the whole image streams from flash to the panel.
- **Night pause:** the server computes it. The device just sleeps `X-Interval` seconds, split into deep-sleep chunks of at most 3 h.

---

## 6. Status reporting (LED + e-paper)

### 6.1 When the LED is used
- **SETUP mode:** slow blink (100 ms on / 1900 ms off) the whole time. Solid during a `test_*` command.
- **Initialisation run** (power-on, RST, after setup): **solid ON while working**, then the **result code**, then **OFF**.
- **Timer wakes:** **LED never on**. Errors are reported in the next HTTP request (`X-Last-Error`) and drawn as a small icon on the e-paper.

### 6.2 Result codes

Pattern: `N` short blinks (250 ms on / 250 ms off), then a 1.5 s pause. The pattern repeats **5 times (~15 s)**, then the LED goes off and the device goes to deep sleep.

| Code | LED pattern | Name (`err` in the serial protocol) | Meaning / typical fix |
|------|-------------|-------------------------------------|-----------------------|
| **OK** | **one 3 s solid glow** | `ok` | Wi-Fi connected, update fetched, screen drawn |
| 2 | ●● | `no_config` | Missing or corrupt config. Run the setup again |
| 3 | ●●● | `wifi_not_found` | SSID not visible. Check the name, distance, 2.4 GHz band |
| 4 | ●●●● | `wifi_auth` | Wrong Wi-Fi password |
| 5 | ●●●●● | `no_ip` | No DHCP lease / wrong static IP |
| 6 | ●●●●●● | `server_unreachable` | DNS, connection refused or timeout. Check the URL and that the server is running |
| 7 | ●●●●●●● | `auth_rejected` | HTTP 401/403. Check the token |
| 8 | ●●●●●●●● | `bad_response` | Other HTTP error, or the image size is wrong |
| 9 | ●●●●●●●●● | `display` | EPD BUSY timeout. Check the wiring and PWR |
| — | 3 fast double-blinks | `low_battery` | Shown *before* the result code when battery < threshold |

### 6.3 E-paper status screen (initialisation run only)
- If the display works, the result is also drawn as text, because the LED code is gone after 15 s and the e-paper keeps it.
- **On success:** the fetched image appears.
- **On error:** a status screen with the code, a one-line explanation, the SSID, IP, RSSI, URL host, battery voltage and firmware version. For example "Error 4 – Wi-Fi password rejected (SSID: Home)".

---

## 7. RUN-mode cycle (pseudo-code)

```cpp
setup() {
  bool init = resetReason() != REASON_DEEP_SLEEP_AWAKE;
  if (digitalRead(RX_PIN) == HIGH) return setupMode();          // §2 mode rule, §3.2
  pinMode(TX_PIN, INPUT); pinMode(RX_PIN, INPUT);           // §3.3
  if (init) ledOn();
  Config cfg; if (!cfg.load()) return finish(init, ERR_NO_CONFIG);
  if (rtc.remainingSleep > 0) return sleepNextChunk();       // long sleeps are split into ≤3 h chunks
  int err = wifiFastConnect(cfg);                           // RTC-cached BSSID/channel/IP
  Response r; if (!err) err = httpUpdate(cfg, init, r);     // §5
  WiFi.forceSleepBegin();                                   // radio off before the refresh
  if (!err && r.status == 200) err = drawImage(r, cfg);     // PWR on → init → write → refresh → panel sleep → PWR off
  if (init && err) drawStatusScreen(err);
  applyParams(cfg, r);                                      // validated, persisted only if changed
  finish(init, err);
}

finish(bool init, int err) {
  if (init) ledReport(err);                                 // §6, ~15 s, then LED off
  epdLinesLow();
  // WAKE_RF_DISABLED only when the next wake is a night-window skip (no Wi-Fi needed)
  ESP.deepSleep(nextSleepSeconds(err) * 1e6, nextWakeNeedsWifi() ? WAKE_RF_DEFAULT : WAKE_RF_DISABLED);
}
```

---

## 8. Memory plan
- The bring-up build with a full-screen GxEPD2 buffer used 55.6 KB of 80 KB static RAM. The real firmware has **no RAM frame buffer**: **flash is the frame buffer**.
  - The HTTP body streams in ≤ 1 KB chunks into `/img.g2` (or patches a region of it).
  - The renderer then streams the file to the panel: master half = bytes 0–49 of each row → `0x24`/`0x26`; slave half = bytes 49–98 → `0xA4`/`0xA6`. The split follows the Waveshare reference driver.
  - On-device status screens are drawn in 16-row strips with an Adafruit GFX `GFXcanvas1` (1.6 KB) and written into the same file.
- The own 4-grey driver (ported from Waveshare `EPD_5in79.c`) replaces GxEPD2, which has no grey support for this panel.

## 9. Open points
- **Point 4** from the requirements list (not provided yet).
- Server side: technology choice (Python/FastAPI, Node, Home Assistant add-on, Node-RED) and how the 792×272 image gets composed.
- Physical button for a manual refresh? It would have to reuse RST: RST plus `init` gives an immediate update with an LED report, at no extra cost.
- Measure the real deep-sleep current after the hardware mods.
