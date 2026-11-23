#pragma once
// Error names and LED blink codes — docs/protocol.md §4.

#include <stdint.h>

namespace walli {

enum class Err : uint8_t {
  Ok = 0,                 // LED: solid 3 s
  NoConfig = 2,
  WifiNotFound = 3,
  WifiAuth = 4,
  NoIp = 5,
  ServerUnreachable = 6,
  AuthRejected = 7,
  BadResponse = 8,
  Display = 9,
};

// Protocol name, e.g. "wifi_auth".
const char* errName(Err e);

// Short English text for status screens, e.g. "Wi-Fi password rejected".
const char* errHuman(Err e);

// Number of LED blinks for an error (0 for Ok).
inline uint8_t errBlinks(Err e) { return e == Err::Ok ? 0 : static_cast<uint8_t>(e); }

// Parse a protocol name back to an error. Returns false for unknown names.
bool errFromName(const char* name, Err& out);

}  // namespace walli
