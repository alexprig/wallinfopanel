# WallInfoPanel — Hardware Specification & Feasibility Study

*Date: 2026-11-21 · Source photos: [`hw/`](../hw/)*

**Goal:** a wall-mounted information panel. You change what it shows over Wi-Fi, through API calls or a web interface, and it runs on battery for about a month.

---

## 1. Identified components

| # | Component | How it was identified |
|---|-----------|-----------------------|
| 1 | **Waveshare 5.79inch e-Paper Module, 792×272 px, black/white (4-level grey)** | Silkscreen on the back of the driver board ([Image 4](../hw/Image%204.jpeg)). The front shows a greyscale picture, so this is the B/W panel, not the (B) red or (G) 4-colour version ([Image 3](../hw/Image%203.jpeg)) |
| 2 | **"new NodeMcu V3" (LoLin-style) ESP8266 dev board, "Ver 0.1"** | Silkscreen "new NodeMcu V3", "Install CH340G driver" ([Image](../hw/Image.jpeg)). Shielded module marked "ESP8266MOD, 802.11b/g/n, PA +25dBm" with a CH340G USB-UART chip and a SOT-223 3.3 V LDO ([Image 2](../hw/Image%202.jpeg)) |

---

## 2. Component 1 — Waveshare 5.79" e-Paper Module

### 2.1 Description
A reflective e-ink (EPD) panel on a Waveshare driver PCB (152.5 × 58.5 mm). The driver PCB has a level shifter, the panel power circuitry, an FPC socket and a 9-pin cable connector. The panel only draws power **while the image changes**. A picture stays on screen with no power at all, which suits a battery-powered wall panel.

Internally the panel uses **two controllers (master + slave)**, and each one drives half of the 792 columns (0–395 and 396–791). Waveshare doesn't name the IC. The identical Good Display panel (GDEY0579T93) uses the **SSD1683** controller.

### 2.2 Main parameters

| Parameter | Value |
|-----------|-------|
| Diagonal | 5.79 in |
| Resolution | **792 × 272 px** |
| Active (display) area | 139.00 × 47.74 mm |
| Dot pitch | 0.1755 × 0.1755 mm (~145 dpi) |
| Raw panel outline | 150.92 × 56.94 × 1.00 mm |
| Driver board outline | 152.50 × 58.50 mm |
| Colours | Black / White, **4 grey levels** (2 bits per pixel) |
| Viewing angle | > 170° |
| Interface | SPI (mode 0), 3-wire or 4-wire, selected by the **BS** switch (0 = 4-wire, the default) |
| Supply / logic voltage | 3.3 V or 5 V (on-board level shifter; logic level must match the supply). Panel itself: 2.3–3.6 V |
| Connector pins | VCC, GND, DIN, CLK, CS, DC, RST, BUSY, **PWR** (panel power enable, must be HIGH to run) |
| Full refresh time | ~3.5 s (Waveshare); 2.2 s (GxEPD2 driver timing). Screen flickers during full refresh |
| Partial refresh time | ~0.8 s (Waveshare); 0.45 s (GxEPD2) |
| Refresh power | **< 50 mW typ.** (about 15 mA at 3.3 V) |
| Standby current | **< 0.01 µA** (practically zero) |
| Lifetime | ~1,000,000 refreshes |
| Operating temp. | 0 … 40 °C (typical for Waveshare B/W EPD, not confirmed on the product page) |

### 2.3 Usage rules from Waveshare (these affect the firmware)
1. **Refresh interval ≥ 180 s**, and refresh at least once every 24 h. So the panel suits minute-scale or longer updates, not live data.
2. Partial refresh can't run forever. Do a **full refresh after every ~5 partial refreshes**.
3. After a refresh, put the panel into **deep sleep or cut its power** (PWR = LOW). Otherwise the panel stays under high voltage, which damages it.
4. After deep sleep, the panel must be **re-initialised** before it accepts new data.

### 2.4 Software support
- **GxEPD2** (Arduino library) ≥ v1.5.9 has the class `GxEPD2_579_GDEY0579T93` (792×272, SSD1683). It supports full, fast and partial refresh. It **doesn't support 4-grey** on this panel (black/white only).
- Waveshare has its own demo code for Arduino, STM32 and Raspberry Pi.
- Board quirks noted in GxEPD2: the PWR pin must be driven HIGH (or tied to 3.3 V). Waveshare's "clever" reset circuit may need a 1 kΩ pull-up on RST with an ESP8266. A board with level converters may need a 3.3 kΩ pull-down on CS (GPIO15).

---

## 3. Component 2 — NodeMCU V3 (LoLin-style, ESP8266)

### 3.1 Description
A breadboard-style development board built around an **ESP-12-class ESP8266 module**. It has a micro-USB port, a **CH340G** USB-to-serial chip (for flashing and the serial console), an **AMS1117-3.3** linear regulator, RST and FLASH buttons, and 2 × 15 header pins. LoLin V3 boards add the extra pins **VU** (USB 5 V) and **G** compared with the Amica V2.

### 3.2 Main parameters

| Parameter | Value |
|-----------|-------|
| SoC | Espressif **ESP8266EX**, Tensilica L106 32-bit, 80 MHz (160 MHz max) |
| RAM | ~80 KB user data RAM + 32 KB instruction RAM. **~40–50 KB free heap** with Wi-Fi running |
| Flash | Typically **4 MB** SPI flash on ESP-12E/F (check with `esptool.py flash_id`) |
| Wi-Fi | 802.11 b/g/n, 2.4 GHz, WPA/WPA2, station / soft-AP / both. PCB antenna |
| TX power | Up to +20 dBm (module marking says "+25 dBm PA") |
| GPIO | 11 usable GPIOs (D0–D8, RX, TX). SPI (HSPI), I²C (software), UART, PWM |
| ADC | 1 × 10-bit (A0). The board's 220k/100k divider gives a 0–3.2 V input range |
| Supply | 5 V via micro-USB or **VIN** (≈ 4.5–10 V into the AMS1117), or **3.3 V directly on a 3V pin** |
| Logic level | 3.3 V (pins are **not** 5 V tolerant) |
| USB-UART | CH340G (9600 bps default for the original Lua firmware; flashing up to 921600 bps) |
| Board size | ~58 × 31 mm (LoLin V3 is wider than Amica V2) |

### 3.3 ESP8266 current consumption (from the Espressif datasheet)

| Mode | Current |
|------|---------|
| Wi-Fi TX (802.11b, +20 dBm) | ~170 mA (peaks up to ~300–400 mA during calibration) |
| Wi-Fi RX / connected and active | ~50–80 mA |
| Modem-sleep (CPU on, radio off between beacons) | ~15–20 mA |
| Light-sleep | ~0.9 mA (chip alone) |
| **Deep-sleep (RTC only)** | **~20 µA (chip alone)** |

### 3.4 ⚠️ Board-level problem: deep-sleep current
The bare chip sleeps at ~20 µA, but the **whole NodeMCU V3 board** typically draws **≈ 10–20 mA** in deep sleep. The causes:
- **AMS1117-3.3** regulator: ~5 mA quiescent current (up to 10 mA).
- **CH340G** USB-UART chip powered from the 3.3 V rail: several mA.
- Power LED / pull-ups: small extra draw.

People who removed or bypassed the regulator measured **20–50 µA** for the whole board. This single fact decides whether a one-month battery life is possible (see §5).

---

## 4. Is this hardware enough for the project?

### Short answer: **yes for the function, no for battery life without small changes.**

| Requirement | Status | Notes |
|-------------|--------|-------|
| Drive a 792×272 e-paper over SPI | ✅ | Library support exists (GxEPD2), and enough free pins remain |
| Wi-Fi connectivity | ✅ | ESP8266 2.4 GHz b/g/n |
| Frame buffer in RAM | ✅ (B/W) / ⚠️ (4-grey) | 1-bit buffer = 792×272/8 = **26,928 B**, which fits in the ~40–50 KB free heap. A 4-grey buffer = **53,856 B** doesn't fit with Wi-Fi running, so it would need paged drawing and a custom driver. **Plan for B/W.** |
| HTTPS / TLS | ⚠️ | BearSSL needs ~16–25 KB on its own. Together with the full frame buffer RAM gets tight. Use paged drawing (GxEPD2 `page_height`), TLS max-fragment-length, or plain HTTP inside the LAN |
| Content updated via API / web UI | ✅ with the right design | See the architecture note below |
| ~1 month on battery | ❌ stock board / ✅ modified | The stock board wastes ~15 mA in sleep, which needs ~11 Ah per month. With a low-quiescent power path a 1000–3000 mAh cell lasts one to several months |

### Architecture note: "push" vs "pull"
- A device that **accepts API calls or serves a web page at any time** must keep Wi-Fi on all the time. That costs ~15–20 mA in modem-sleep (~11–14 Ah per month), or ~2–5 mA in auto light-sleep if you're lucky (~1.5–3.6 Ah per month, and it depends heavily on the router). Not realistic on a small battery.
- **Recommended: pull model.** The device sleeps (deep sleep), wakes every *N* minutes, fetches the content, refreshes the screen only if something changed, and goes back to sleep. The **web UI and API run on a server** that is always on: Home Assistant, a Raspberry Pi, a small VPS, Node-RED, or an MQTT broker with retained messages. You "push" to that server, and the panel picks up the change on its next wake.
  - Best variant: the **server renders the 792×272 image** (1-bit PNG/BMP, about 27 KB raw) and the ESP only downloads and blits it. This keeps the firmware tiny and lets the server use any layout.
- **Configuration (decided, rev. 2):** setup runs over USB. A setup switch powers the CH340G (sensed on RX), and a Web Serial page in the browser writes Wi-Fi, URL, token, interval and other settings to the board. See [firmware-design.md](firmware-design.md).

### Missing parts / bill of materials to add

| Item | Why | Example |
|------|-----|---------|
| Battery | Power source | 18650 Li-ion 3000 mAh, **or** LiFePO4 18650 1500–1800 mAh |
| Low-Iq 3.3 V regulator (if using Li-ion) | Replace or bypass the AMS1117 (5 mA wasted) | **RT9080-33** (Iq ~2 µA, 600 mA) or **HT7833** (Iq ~4 µA, 500 mA). The LDO must handle Wi-Fi peaks of ≥ 400 mA |
| Charger + protection (if rechargeable) | Safe USB charging | TP4056 module with DW01 protection (for Li-ion), or a LiFePO4-specific charger |
| Battery voltage divider | Low-battery warning on screen | 1 MΩ/330 kΩ to A0 (draws ~3 µA). Mind the board's 220k/100k divider |
| D0 ↔ RST jumper | Needed for the deep-sleep timer wake-up | Wire / 470 Ω resistor |
| Setup slide switch + 100 kΩ resistor | Powers the CH340G only during USB setup; mode sense on RX | SPST slide switch in the CH340G VCC trace, 100 kΩ RX → G (see [firmware-design.md](firmware-design.md)) |
| External status LED + 1 kΩ | Visible result codes when the board is inside the frame | 3 mm LED on D4 (GPIO2), active LOW |
| Frame / enclosure | Wall mounting | 3D-printed or a picture frame |

**Two ways to fix the sleep current:**
- **A — Modify the NodeMCU:** remove the AMS1117 (or lift its output pin) and feed regulated 3.3 V into a **3V** pin. The CH340G then has no 3.3 V source when USB is unplugged; on some board revisions you must also cut its VCC trace. Expect **~50–80 µA** for the whole board in sleep.
- **B — Replace the board (cleaner):** use a bare **ESP-12F** module, or a board designed for low power (for example a Wemos D1 mini *v3+* clone, ~100–150 µA), or move to an **ESP32-C3/S3** board with a low-Iq LDO. An ESP32-S3 also has PSRAM, enough for 4-grey and HTTPS without any RAM worries.

---

## 5. Energy-consumption prediction

### 5.1 One wake cycle (pull model)

| Phase | Duration | Average current | Charge |
|-------|----------|-----------------|--------|
| Boot + Wi-Fi connect + HTTP GET (optimised: static IP, saved BSSID/channel, keep-alive off) | ~2.0 s | ~80 mA | 160 mA·s |
| …same, **not optimised** (DHCP, scan) | ~5.0 s | ~80 mA | 400 mA·s |
| Render to buffer (Wi-Fi off) | ~0.5 s | ~20 mA | 10 mA·s |
| Full e-paper refresh (ESP waiting with Wi-Fi off ~20 mA + panel ~15 mA) | ~3.5 s | ~35 mA | 122.5 mA·s |
| Panel sleep / PWR off, ESP deep sleep | rest of the interval | see 5.2 | — |

**Per wake:** about **81 µAh** optimised (64 µAh when only half of the wakes need a redraw), or **148 µAh** without Wi-Fi optimisation.

### 5.2 Sleep current
| Configuration | Sleep current | Per day |
|---------------|---------------|---------|
| Stock NodeMCU V3 (AMS1117 + CH340G) | **~15 mA** (10–20 mA, needs measurement) | **360 mAh** |
| Modified board / low-Iq power path (ESP 20 µA + LDO 2–4 µA + flash and leakage + divider 3 µA) | **~60 µA** | **1.44 mAh** |
| E-paper (PWR = LOW or panel deep sleep) | < 0.01 µA | ~0 |

### 5.3 Daily and monthly consumption

| Update interval | Firmware | **Stock board** mAh/day → 30 days | **Low-power board** mAh/day → 30 days |
|-----------------|----------|-----------------------------------|---------------------------------------|
| 5 min | not optimised | 403 → 12,078 | 44.0 → 1,321 |
| 5 min | optimised | 383 → 11,502 | 24.8 → **745** |
| 5 min | optimised, redraw only on change (50 %) | 379 → 11,355 | 19.9 → 598 |
| **15 min** | not optimised | 374 → 11,226 | 15.6 → 469 |
| **15 min** | **optimised** | 368 → 11,034 | **9.2 → 277** |
| 15 min | optimised, redraw on change | 366 → 10,985 | 7.6 → 228 |
| 30 min | optimised | 364 → 10,917 | 5.3 → 160 |
| 60 min | optimised | 362 → 10,858 | 3.4 → 102 |
| *Always-on web server (modem-sleep ~20 mA)* | — | ~840 → ~25,000 | ~480 → ~14,400 |
| *Always-on, auto light-sleep (~3 mA, depends on router)* | — | ~430 → ~13,000 | ~72 → ~2,200 |

**Takeaways**
- With the stock board, the **sleep current is ~97 % of the energy**. The update interval barely matters. A month needs **~11 Ah**: four 18650 cells, or a power bank (most power banks switch off at such low loads, so they're unreliable).
- With a low-power board, the **Wi-Fi wake-ups dominate**. Shorter Wi-Fi connects (static IP, BSSID/channel caching) and redrawing only on change pay off directly.
- A per-wake budget of 81 µAh means **at most ~12 wakes per mAh**. The Waveshare minimum of 180 s between refreshes also caps how often it makes sense to update.

### 5.4 Battery proposal

Design margin: 85 % usable capacity × 1/1.25 reserve for Wi-Fi retries, cold, self-discharge and ageing gives an **effective capacity of ≈ 68 % of nominal**.

| Battery | Effective | 5 min (opt.) | **15 min (opt.)** | 30 min | 60 min | Stock board |
|---------|-----------|--------------|-------------------|--------|--------|-------------|
| LiPo 1000 mAh | 680 mAh | 27 days | **74 days** | 128 days | 200 days | ~2 days |
| LiFePO4 18650 1500 mAh | 1020 mAh | 41 days | **111 days** | 192 days | 300 days | ~3 days |
| **Li-ion 18650 3000 mAh** | 2040 mAh | 82 days | **222 days** | 385 days | 600 days | ~5.5 days |
| 2 × 18650 parallel 6000 mAh | 4080 mAh | 164 days | 443 days | — | — | ~11 days |

### ✅ Recommendation
- **Minimum for one month** (low-power board, optimised firmware): **≥ 500 mAh** at 15-min updates, or **≥ 1100 mAh** at 5-min updates.
- **Recommended:** **one 18650 Li-ion cell, 2500–3500 mAh**, with an RT9080/HT7833 LDO, a TP4056+DW01 charger, and an updates interval of 10–15 minutes. That gives **≈ 6–8 months** per charge, or **> 2 months** even at 5-min updates. The extra headroom covers bad Wi-Fi days and winter temperatures.
- **Simplest wiring alternative:** a **LiFePO4 cell** (3.2 V nominal, 3.0–3.6 V) connected **directly to the 3.3 V rail with no regulator**. The ESP8266 (2.5–3.6 V) and the panel (2.3–3.6 V) are both in range. 1500 mAh gives about 3.5 months at 15 min. Let the cell rest after charging, because 3.65 V straight off the charger is at the ESP8266's absolute limit.
- **Do not** run the stock NodeMCU V3 on battery for a month. It needs ~11 Ah.

---

## 6. Suggested wiring (NodeMCU V3 ↔ Waveshare 5.79" module)

| e-Paper pin | NodeMCU pin | GPIO | Notes |
|-------------|-------------|------|-------|
| VCC | 3V | — | 3.3 V supply (module logic at 3.3 V) |
| GND | G | — | |
| DIN | D7 | GPIO13 | HSPI MOSI |
| CLK | D5 | GPIO14 | HSPI SCK |
| CS | D8 | GPIO15 | Boot strap (must be LOW at boot). Add a 3.3 kΩ pull-down if boot fails |
| DC | D3 | GPIO0 | Boot strap (HIGH at boot), OK as an output |
| RST | D6 | GPIO12 | Moved from D4 (rev. 2) so D4 can drive the status LED. Add a 1 kΩ pull-up if reset misbehaves |
| BUSY | D2 | GPIO4 | Input |
| PWR | D1 | GPIO5 | HIGH = panel powered. Drive LOW before deep sleep |
| — | D0 ↔ RST | GPIO16 | **Required** for the deep-sleep timer wake-up |
| — | D4 → LED (active LOW) | GPIO2 | Status LED: module LED + external 3 mm LED with 1 kΩ to 3V3 |
| — | RX + 100 kΩ to GND | GPIO3 | Setup-mode sense (HIGH when the setup switch powers the CH340). Leave SD2/SD3 unconnected: pulled LOW they stop the boot |
| — | A0 ← divider | ADC | Battery voltage monitor |

> **Rev. 2 (setup over USB):** the config button is dropped. A setup switch now powers the CH340G, and the board detects it on RX. Full details are in [firmware-design.md §3](firmware-design.md#3-hardware-for-the-setup-switch).

The BS switch on the e-paper board stays at **0 (4-wire SPI)**.

```
             +-------------------+         Wi-Fi          +-------------------------+
 [Battery]-->| low-Iq LDO / LFP  |--3V3-->[ESP8266]<~~~~~~~~~>|  Server (always on):     |
             +-------------------+          |  SPI          |  web UI + REST API,       |
                                            v               |  renders 792x272 image  |
                                  [Waveshare 5.79" EPD]     +-------------------------+
                                    (PWR pin gated)
 Loop: deep-sleep -> wake (RTC) -> Wi-Fi -> GET /panel.bin (ETag) -> changed? redraw : skip -> panel off -> deep-sleep
```

---

## 7. Next steps
1. **Measure** the actual deep-sleep current of this NodeMCU board (multimeter in series on VIN). It confirms or corrects the 15 mA assumption.
2. Decide: **modify the NodeMCU** (remove the AMS1117, feed 3.3 V) **or** switch to an ESP-12F / ESP32-C3/S3 board.
3. Bench prototype on USB power: flash `WallISetup` (env `nodemcuv2_debug`), wired as in §6, and check the 4-grey output.
4. Choose the server side (Home Assistant / Node-RED / small Python or Node service) that hosts the web UI and API and renders the image.
5. Firmware: deep sleep, Wi-Fi fast-connect (RTC-memory BSSID/channel, static IP), ETag/hash to skip unchanged frames, a full refresh every 5th partial, battery reporting.

---

## Sources
- Waveshare 5.79inch e-Paper Module — product page: <https://www.waveshare.com/5.79inch-e-paper-module.htm>
- Waveshare 5.79inch e-Paper Module — wiki/manual (parameters, precautions, dual-IC notes): <https://www.waveshare.com/wiki/5.79inch_e-Paper_Module_Manual>
- Good Display GDEY0579T93 (same panel, SSD1683): <https://www.good-display.com/product/439.html>
- GxEPD2 library (driver `GxEPD2_579_GDEY0579T93`, added v1.5.9; wiring notes): <https://github.com/ZinggJM/GxEPD2>, [ConnectingHardware.md](https://github.com/ZinggJM/GxEPD2/blob/master/ConnectingHardware.md)
- NodeMCU V3 CH340 schematic: <https://mischianti.org/wp-content/uploads/2024/01/nodemcu-v3-ch340-schematics.pdf>
- NodeMCU on battery, AMS1117 quiescent current, removing the regulator: <https://tinkeringwithtechnology.pages.dev/2016/05/29/running-nodemcu-on-a-battery-esp8266-low-power-consumption-revisited>
- CH340 clones and high sleep current (Wemos D1 mini): <https://kevinstadler.github.io/notes/wemos-d1-mini-clones-sleep-mode-current-power-consumption-esp8266>
- Espressif ESP8266EX datasheet (power modes, currents): <https://www.espressif.com/sites/default/files/documentation/0a-esp8266ex_datasheet_en.pdf>
