#pragma once
// One complete update cycle (Wi-Fi → HTTP → flash → redraw), shared by RUN mode and the
// setup page's "Test update" command.

#include <Arduino.h>
#include <Config.h>
#include <Errors.h>

#include "Rtc.h"

namespace cycle {

struct Result {
  walli::Err err = walli::Err::Ok;
  int http = 0;
  bool region = false;
  bool redrawn = false;
  uint32_t bytes = 0;
  int32_t rssi = 0;
  uint32_t ms = 0;
};

// `cfgChanged` is set when server parameters changed the configuration (caller saves it).
Result run(walli::Config& cfg, RtcState& rtc, bool bootInit, uint32_t battMv, bool& cfgChanged);

// Redraws the stored image file on the panel. Err::Display on failure.
walli::Err showStoredImage();

// Averaged A0 reading converted with the configured full-scale voltage.
uint32_t readBatteryMv(uint32_t fullScaleMv);

}  // namespace cycle
