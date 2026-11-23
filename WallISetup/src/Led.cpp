#include "Led.h"

#include <Arduino.h>

#include "pins.h"

namespace led {

namespace {
constexpr uint16_t kBlinkOnMs = 250;
constexpr uint16_t kBlinkOffMs = 250;
constexpr uint16_t kCodePauseMs = 1500;
constexpr uint8_t kRepeats = 5;

void pulse(uint16_t onMs, uint16_t offMs) {
  on();
  delay(onMs);
  off();
  delay(offMs);
}
}  // namespace

void begin() {
  pinMode(PIN_LED, OUTPUT);
  off();
}

void on() { digitalWrite(PIN_LED, LED_ON); }

void off() { digitalWrite(PIN_LED, LED_OFF); }

void report(walli::Err err, bool lowBattery) {
  off();
  delay(500);
  if (lowBattery) {
    for (int i = 0; i < 3; ++i) {
      pulse(100, 100);
      pulse(100, 400);
    }
    delay(700);
  }
  if (err == walli::Err::Ok) {
    pulse(3000, 0);
    return;
  }
  const uint8_t blinks = walli::errBlinks(err);
  for (uint8_t r = 0; r < kRepeats; ++r) {
    for (uint8_t i = 0; i < blinks; ++i) pulse(kBlinkOnMs, kBlinkOffMs);
    delay(kCodePauseMs);
  }
  off();
}

void setupTick() {
  const uint32_t phase = millis() % 2000;
  digitalWrite(PIN_LED, phase < 100 ? LED_ON : LED_OFF);
}

}  // namespace led
