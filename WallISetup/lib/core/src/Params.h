#pragma once
// Server parameter headers, sleep planning and retry backoff — docs/protocol.md §2.3.

#include <stdint.h>

namespace walli {

constexpr uint32_t kMinIntervalS = 180;     // Waveshare: refresh interval >= 180 s
constexpr uint32_t kMaxIntervalS = 86400;
constexpr uint32_t kDefaultIntervalS = 900;
constexpr uint32_t kMaxSleepChunkS = 10800; // ESP8266 deep sleep limit is ~3.5 h; use 3 h chunks
constexpr uint32_t kBackoffBaseS = 300;

// Strict unsigned decimal parse (digits only, optional surrounding spaces, no overflow).
bool parseUInt(const char* text, uint32_t& out);

uint32_t clampInterval(uint32_t seconds);

bool lowBattValid(uint32_t mv);        // 2800 … 4200
bool battScaleValid(uint32_t mv);      // 1000 … 15000
bool updateUrlValid(const char* url);  // "http://…", ≤ 160 chars, host present

// Sleep after a cycle: the interval on success, exponential backoff on failures
// (300 s, 600 s, 1200 s, … capped by the interval, never below 180 s).
uint32_t nextSleepSeconds(uint32_t intervalS, uint8_t consecutiveFailures);

struct SleepPlan {
  uint32_t chunkS;      // sleep now
  uint32_t remainingS;  // still to sleep after this chunk (wake without Wi-Fi)
};

SleepPlan planSleep(uint32_t totalS, uint32_t maxChunkS = kMaxSleepChunkS);

// Battery voltage from a raw A0 reading (0…1023) and the configured full-scale voltage.
uint32_t batteryMilliVolts(uint32_t raw, uint32_t fullScaleMv);

}  // namespace walli
