#pragma once
// Wi-Fi connect (with RTC fast-connect cache) and the HTTP update call — docs/protocol.md §2.

#include <Arduino.h>
#include <Config.h>
#include <Errors.h>

#include "Rtc.h"

namespace net {

walli::Err connect(const walli::Config& cfg, RtcState& rtc, uint32_t timeoutMs = 10000);
void off();

struct UpdateRequest {
  const char* url;
  const char* token;
  String etag;            // sent as If-None-Match when non-empty
  bool bootInit;
  uint32_t battMv;
  int32_t rssi;
  walli::Err lastErr;     // sent as X-Last-Error when not Ok
};

struct UpdateResponse {
  int http = 0;
  bool changed = false;   // image file was replaced or patched → redraw needed
  bool region = false;
  uint32_t bytes = 0;
  String etag;
  // Parameter headers; 0 / empty = not sent by the server.
  uint32_t interval = 0;
  uint32_t lowBattMv = 0;
  String url;
  bool retryWithoutEtag = false;  // region did not match the stored image
};

// Performs one GET. On 200 the body is written to flash (full image via temp file + rename,
// or a region patched into the stored image). The stored ETag is cleared before any write;
// the caller stores the new ETag after a successful redraw.
walli::Err update(const UpdateRequest& req, UpdateResponse& res);

}  // namespace net
