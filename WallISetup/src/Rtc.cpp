#include "Rtc.h"

#include <Arduino.h>
#include <stddef.h>
#include <string.h>

namespace rtc {

namespace {
constexpr uint32_t kMagic = 0x57414C31;  // "WAL1"
constexpr uint32_t kOffsetWords = 0;     // first user RTC block

uint32_t crc32(const uint8_t* data, size_t len) {
  uint32_t crc = 0xFFFFFFFFu;
  for (size_t i = 0; i < len; ++i) {
    crc ^= data[i];
    for (int b = 0; b < 8; ++b) crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1u)));
  }
  return ~crc;
}

uint32_t stateCrc(const RtcState& s) {
  const uint8_t* p = reinterpret_cast<const uint8_t*>(&s);
  const size_t skip = offsetof(RtcState, remainingSleepS);
  return crc32(p + skip, sizeof(RtcState) - skip);
}
}  // namespace

RtcState load() {
  RtcState s;
  if (ESP.rtcUserMemoryRead(kOffsetWords, reinterpret_cast<uint32_t*>(&s), sizeof s) &&
      s.magic == kMagic && s.crc == stateCrc(s)) {
    return s;
  }
  memset(&s, 0, sizeof s);
  s.magic = kMagic;
  return s;
}

void save(RtcState& state) {
  state.magic = kMagic;
  state.crc = stateCrc(state);
  ESP.rtcUserMemoryWrite(kOffsetWords, reinterpret_cast<uint32_t*>(&state), sizeof state);
}

void clear() {
  RtcState s;
  memset(&s, 0, sizeof s);
  ESP.rtcUserMemoryWrite(kOffsetWords, reinterpret_cast<uint32_t*>(&s), sizeof s);
}

}  // namespace rtc
