#include "Params.h"

#include <string.h>

namespace walli {

bool parseUInt(const char* text, uint32_t& out) {
  if (text == nullptr) return false;
  const char* p = text;
  while (*p == ' ') ++p;
  if (*p < '0' || *p > '9') return false;
  uint64_t v = 0;
  while (*p >= '0' && *p <= '9') {
    v = v * 10 + uint64_t(*p - '0');
    if (v > 0xFFFFFFFFull) return false;
    ++p;
  }
  while (*p == ' ') ++p;
  if (*p != '\0') return false;
  out = uint32_t(v);
  return true;
}

uint32_t clampInterval(uint32_t seconds) {
  if (seconds < kMinIntervalS) return kMinIntervalS;
  if (seconds > kMaxIntervalS) return kMaxIntervalS;
  return seconds;
}

bool lowBattValid(uint32_t mv) { return mv >= 2800 && mv <= 4200; }

bool battScaleValid(uint32_t mv) { return mv >= 1000 && mv <= 15000; }

bool updateUrlValid(const char* url) {
  if (url == nullptr) return false;
  const size_t len = strlen(url);
  if (len > 160) return false;
  if (strncmp(url, "http://", 7) != 0) return false;
  if (len == 7) return false;                    // no host
  const char c = url[7];
  if (c == '/' || c == ':' || c == ' ') return false;
  for (size_t i = 0; i < len; ++i) {
    if (url[i] <= ' ' || url[i] == 0x7F) return false;  // no spaces / control chars
  }
  return true;
}

uint32_t nextSleepSeconds(uint32_t intervalS, uint8_t consecutiveFailures) {
  const uint32_t interval = clampInterval(intervalS);
  if (consecutiveFailures == 0) return interval;
  const uint8_t shift = consecutiveFailures - 1 > 8 ? 8 : consecutiveFailures - 1;
  const uint32_t backoff = kBackoffBaseS << shift;
  return clampInterval(backoff < interval ? backoff : interval);
}

SleepPlan planSleep(uint32_t totalS, uint32_t maxChunkS) {
  if (maxChunkS == 0) maxChunkS = 1;
  SleepPlan plan;
  plan.chunkS = totalS < maxChunkS ? totalS : maxChunkS;
  plan.remainingS = totalS - plan.chunkS;
  return plan;
}

uint32_t batteryMilliVolts(uint32_t raw, uint32_t fullScaleMv) {
  if (raw > 1023) raw = 1023;
  return (raw * fullScaleMv + 511) / 1023;
}

}  // namespace walli
