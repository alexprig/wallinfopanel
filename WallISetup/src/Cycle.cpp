#include "Cycle.h"

#include <ESP8266WiFi.h>
#include <Params.h>
#include <string.h>

#include "Epd579.h"
#include "Log.h"
#include "Net.h"
#include "Storage.h"

using namespace walli;

namespace cycle {

namespace {

void applyParams(Config& cfg, const net::UpdateResponse& res, bool& changed) {
  if (res.interval && res.interval != cfg.interval) {
    cfg.interval = res.interval;
    changed = true;
  }
  if (res.lowBattMv && res.lowBattMv != cfg.lowBattMv) {
    cfg.lowBattMv = uint16_t(res.lowBattMv);
    changed = true;
  }
  if (res.url.length() && strcmp(res.url.c_str(), cfg.url) != 0 && strcmp(res.url.c_str(), cfg.pendingUrl) != 0 &&
      res.url.length() < sizeof cfg.pendingUrl) {
    strcpy(cfg.pendingUrl, res.url.c_str());  // tried on the next cycle (protocol.md §2.3)
    changed = true;
  }
}

}  // namespace

uint32_t readBatteryMv(uint32_t fullScaleMv) {
  uint32_t sum = 0;
  for (int i = 0; i < 8; ++i) {
    sum += analogRead(A0);
    delay(1);
  }
  return batteryMilliVolts(sum / 8, fullScaleMv);
}

Err showStoredImage() {
  Epd579 epd;
  return epd.showFile(storage::kImagePath) ? Err::Ok : Err::Display;
}

Result run(Config& cfg, RtcState& rtc, bool bootInit, uint32_t battMv, bool& cfgChanged) {
  Result result;
  const uint32_t t0 = millis();

  result.err = net::connect(cfg, rtc);
  if (result.err != Err::Ok) {
    net::off();
    result.ms = millis() - t0;
    return result;
  }
  result.rssi = WiFi.RSSI();

  const bool usingPending = cfg.pendingUrl[0] != '\0';
  net::UpdateRequest req;
  req.url = usingPending ? cfg.pendingUrl : cfg.url;
  req.token = cfg.token;
  req.etag = (!bootInit && storage::imageValid()) ? storage::imageEtag() : String();
  req.bootInit = bootInit;
  req.battMv = battMv;
  req.rssi = result.rssi;
  req.lastErr = static_cast<Err>(rtc.lastErr);

  net::UpdateResponse res;
  result.err = net::update(req, res);
  if (res.retryWithoutEtag) {
    LOGF("region base mismatch, retrying for a full image\n");
    req.etag = String();
    result.err = net::update(req, res);
  }
  result.http = res.http;
  net::off();  // radio off before the 4 s panel refresh

  if (usingPending) {
    if (result.err == Err::Ok) strcpy(cfg.url, cfg.pendingUrl);  // new URL confirmed
    cfg.pendingUrl[0] = '\0';                                       // either way: drop pending
    cfgChanged = true;
  }
  if (result.err == Err::Ok) applyParams(cfg, res, cfgChanged);

  if (result.err == Err::Ok && res.changed) {
    result.err = showStoredImage();
    if (result.err == Err::Ok) {
      storage::setImageEtag(res.etag);
      result.redrawn = true;
      result.region = res.region;
      result.bytes = res.bytes;
    }
  }
  result.ms = millis() - t0;
  LOGF("cycle: %s http=%d bytes=%lu region=%d %lu ms\n", errName(result.err), result.http,
       static_cast<unsigned long>(result.bytes), result.region, static_cast<unsigned long>(result.ms));
  return result;
}

}  // namespace cycle
