#pragma once
// Pin map — NodeMCU V3 (ESP8266) <-> Waveshare 5.79" e-Paper module.
// Source of truth: docs/firmware-design.md §3.6 (rev. 2).

#include <stdint.h>

// e-Paper (hardware SPI: CLK = D5/GPIO14, DIN = D7/GPIO13)
constexpr uint8_t PIN_EPD_PWR  = 5;   // D1  HIGH = panel powered
constexpr uint8_t PIN_EPD_BUSY = 4;   // D2  input, HIGH while the panel is busy
constexpr uint8_t PIN_EPD_DC   = 0;   // D3  boot strap: must be HIGH at boot
constexpr uint8_t PIN_EPD_RST  = 12;  // D6
constexpr uint8_t PIN_EPD_CS   = 15;  // D8  boot strap: must be LOW at boot

// Status LED: module LED (+ optional external LED to 3V3), active LOW
constexpr uint8_t PIN_LED      = 2;   // D4  boot strap: must be HIGH at boot
constexpr uint8_t LED_ON       = 0;   // LOW
constexpr uint8_t LED_OFF      = 1;   // HIGH

// Setup sense: RX. The CH340 power switch selects the mode: CH340 ON -> its TXD idles HIGH ->
// SETUP; CH340 OFF -> 100k pull-down holds RX LOW -> RUN.
// Not SD3/GPIO10: pulled LOW it stops the ROM loading from flash on this module (tested).
constexpr uint8_t PIN_SETUP_SENSE = 3; // RX

// Deep-sleep wake: D0 (GPIO16) must be wired to RST — no firmware pin needed.
// Battery voltage: A0.
