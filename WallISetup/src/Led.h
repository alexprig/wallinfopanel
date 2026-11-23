#pragma once
// Status LED (GPIO2, active LOW) — docs/firmware-design.md §6.

#include <Errors.h>
#include <stdint.h>

namespace led {

void begin();
void on();
void off();

// Blocking result report: optional low-battery warning (3 fast double-blinks), then
// OK = solid 3 s, or the error code (N blinks + pause) repeated 5 times. Ends with LED off.
void report(walli::Err err, bool lowBattery);

// Non-blocking slow blink for SETUP mode (100 ms on / 1900 ms off). Call often.
void setupTick();

}  // namespace led
