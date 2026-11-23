// WallISetup — ESP8266 firmware for the WallInfoPanel 5.79" 4-grey e-paper wall panel.
//
// Boot decides the mode (docs/firmware-design.md §2):
//   Any boot with the CH340 switch ON (RX/GPIO3 HIGH) → SETUP mode: JSON commands over USB.
//   CH340 OFF (RX LOW) → RUN mode: one update, then deep sleep. Deep-sleep wakes follow the switch too.

#include <Arduino.h>
#include <user_interface.h>

#include <BootMode.h>

#include "Modes.h"
#include "pins.h"

namespace {
bool g_setupMode = false;
}

void setup() {
  const rst_info* reset = ESP.getResetInfoPtr();
  const bool bootInit = reset->reason != REASON_DEEP_SLEEP_AWAKE;

#if WALLI_FORCE_RUN
  constexpr bool kForceRun = true;
  Serial.begin(115200);  // bench build: logs on, setup switch ignored
  delay(50);
  Serial.printf("\nWallISetup %s (debug, RUN forced) reset reason %u\n", FW_VERSION, reset->reason);
  const bool ch340On = false;
#else
  constexpr bool kForceRun = false;
  pinMode(PIN_SETUP_SENSE, INPUT);  // GPIO mode, pull-up off: CH340 TXD pulls HIGH, 100k pulls LOW
  delay(2);
  const bool ch340On = digitalRead(PIN_SETUP_SENSE) == HIGH;
#endif
  // The switch decides on every boot (an RST press during deep sleep looks like a timer wake).
  g_setupMode = walli::decideBootMode(bootInit, ch340On, kForceRun) == walli::BootMode::Setup;
#if !WALLI_FORCE_RUN
  if (!g_setupMode) pinMode(1, INPUT);  // TX released: no back-feeding into an unpowered CH340
#endif

  if (g_setupMode) {
    setupmode::begin();
    return;
  }
  runmode::run(bootInit);
}

void loop() {
  if (g_setupMode) setupmode::loop();
}
