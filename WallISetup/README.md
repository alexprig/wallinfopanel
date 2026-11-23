# WallISetup — ESP8266 firmware

Firmware for the WallInfoPanel. It runs on a NodeMCU V3 (ESP8266) and drives a Waveshare 5.79" e-paper in **4-grey** mode, pulling images and settings from **WallIServer** over plain HTTP.

- Protocol (authoritative): [`../docs/protocol.md`](../docs/protocol.md)
- Design (modes, setup switch, LED codes): [`../docs/firmware-design.md`](../docs/firmware-design.md)
- Hardware and power budget: [`../docs/hardware-spec.md`](../docs/hardware-spec.md)

## Build, flash, test

Open **this folder** in VS Code with the PlatformIO IDE extension, or use the CLI:

```bash
pio run                                  # build release firmware (env nodemcuv2)
pio run -t upload                        # flash over USB (CH340; port is auto-detected)
pio device monitor                       # serial console, 115200
pio test -e native                       # unit tests of lib/core on this computer
pio run -e nodemcuv2_debug -t upload     # bench build: RUN mode forced, logs on Serial
```

| Environment | Use |
|-------------|-----|
| `nodemcuv2` | Release. The mode comes from the setup switch (CH340 power, sensed on RX). No serial output in RUN mode |
| `nodemcuv2_debug` | Bench testing on USB without the setup switch: always RUN mode, logs every step. Don't use on battery |
| `native` | Host unit tests for config validation, protocol parsing, gray2 geometry, backoff |

## How it behaves

| Boot | What happens |
|------|--------------|
| Setup switch **ON** (CH340 powered → RX HIGH), then RST or the next wake | **SETUP mode.** The panel shows instructions and the LED blinks slowly. The device answers JSON commands from the setup page (`http://<server>:5080/setup/`). Turning it **OFF** restarts it into RUN mode |
| Power-on / RST / after setup (switch OFF) | **Init run:** LED on, Wi-Fi, full image download, redraw. Then the LED result code (OK = 3 s solid, N blinks = error N), and an error screen on the panel if something failed. Then deep sleep |
| Deep-sleep timer wake | Silent cycle: `If-None-Match` → `304` (nothing to do), a region, or a full image. Redraw if changed, then deep sleep for `X-Interval` |

LED error codes: 2 not configured · 3 Wi-Fi not found · 4 Wi-Fi password · 5 no IP · 6 server unreachable · 7 token rejected · 8 bad response · 9 display. Three fast double-blinks before the code = low battery.

## Hardware checklist (see firmware-design.md §3)

- [ ] **D0 ↔ RST** wire (deep-sleep wake)
- [ ] **100 kΩ from RX to GND** (setup-mode sense)
- [ ] **Setup switch** in the CH340G VCC line (pin 16). Check first that pin 16 is on 3.3 V
- [ ] **Nothing on SD2 / SD3**: pulled LOW they stop the boot
- [ ] e-Paper: VCC→3V, GND→G, DIN→D7, CLK→D5, CS→D8, DC→D3, **RST→D6**, BUSY→D2, **PWR→D1**. BS switch = 0 (4-wire SPI)
- [ ] Status LED: module LED on D4 + optional external LED (1 kΩ to 3V3)
- [ ] Battery divider to A0. Set `battScale_mV` = the battery voltage that reads 1023 (default 4200 = 100 kΩ series + the board's 220k/100k divider)
- [ ] Low-power supply: AMS1117 removed or bypassed (see hardware-spec.md §4)

## Code map

| Path | What |
|------|------|
| `src/main.cpp` | Boot: picks SETUP or RUN mode |
| `src/RunMode.cpp` | Update cycle, status screen, LED report, chunked deep sleep |
| `src/SetupMode.cpp` | JSON-lines command server for the setup page, exit on switch-off |
| `src/Cycle.cpp` | Wi-Fi → HTTP → flash → redraw, shared by RUN mode and `test_update` |
| `src/Net.cpp` | Wi-Fi fast connect (RTC cache), HTTP request, body → flash (full / region patch) |
| `src/Epd579.cpp`, `src/epd579_lut.h` | 4-grey panel driver, ported from Waveshare `EPD_5in79.c` |
| `src/Screens.cpp` | Text and test-pattern screens rendered in 16-row strips into the image file |
| `src/Storage.cpp` | LittleFS: `/config.json`, `/img.g2` (53,856 B, plane 0 ‖ plane 1), `/img.meta` (ETag) |
| `src/Rtc.cpp`, `src/Led.cpp` | RTC memory state, LED patterns |
| `lib/core/` | Hardware-independent logic (unit-tested): config, error names, gray2 geometry, params/backoff |

## Not yet verified on hardware
- The 4-grey output and grey-level order on the real panel (mapping taken from Waveshare's reference driver).
- `kBusySettleMs` = 20 ms after BUSY (Waveshare uses 200 ms). Raise it in `Epd579.cpp` if refreshes misbehave.
- RX-based setup detection and the automatic restart when the switch goes OFF.
- Real deep-sleep current and the battery reading.
