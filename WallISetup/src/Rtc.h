#pragma once
// State kept in RTC user memory across deep sleep (lost on power-off / reset by power loss).

#include <stdint.h>

struct RtcState {
  uint32_t magic;
  uint32_t crc;
  uint32_t remainingSleepS;  // long sleeps are split into chunks; >0 = keep sleeping without Wi-Fi
  uint8_t bssid[6];          // Wi-Fi fast-connect cache
  uint8_t channel;
  uint8_t wifiValid;
  uint8_t failures;          // consecutive failed cycles (for backoff)
  uint8_t lastErr;           // walli::Err of the previous cycle
  uint8_t pad[2];
};
static_assert(sizeof(RtcState) % 4 == 0, "RTC memory is accessed in 32-bit words");

namespace rtc {

// Loads the state; returns a zeroed state if the CRC/magic doesn't match (first boot).
RtcState load();
void save(RtcState& state);
void clear();

}  // namespace rtc
