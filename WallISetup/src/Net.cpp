#include "Net.h"

#include <ESP8266HTTPClient.h>
#include <ESP8266WiFi.h>
#include <Gray2.h>
#include <LittleFS.h>
#include <Params.h>

#include "Log.h"
#include "Storage.h"

using namespace walli;

namespace net {

namespace {

constexpr uint32_t kHttpTimeoutMs = 10000;
constexpr uint32_t kBodyIdleTimeoutMs = 8000;

Err mapStatus(int8_t st) {
  switch (st) {
    case WL_CONNECTED:
      return Err::Ok;
    case WL_NO_SSID_AVAIL:
      return Err::WifiNotFound;
    case WL_WRONG_PASSWORD:
    case WL_CONNECT_FAILED:
      return Err::WifiAuth;
    default:  // timeout (-1), idle, disconnected
      return Err::NoIp;
  }
}

bool readExact(WiFiClient& s, uint8_t* buf, size_t n) {
  size_t got = 0;
  uint32_t last = millis();
  while (got < n) {
    const int avail = s.available();
    if (avail > 0) {
      const size_t want = n - got < size_t(avail) ? n - got : size_t(avail);
      const int k = s.read(buf + got, want);
      if (k > 0) {
        got += size_t(k);
        last = millis();
        continue;
      }
    }
    if (!s.connected() && s.available() <= 0) return false;
    if (millis() - last > kBodyIdleTimeoutMs) return false;
    delay(1);
  }
  return true;
}

bool receiveFull(WiFiClient& s) {
  File f = LittleFS.open(storage::kImageTmp, "w");
  if (!f) return false;
  static uint8_t buf[1024];
  uint32_t remaining = gray2::kImageBytes;
  bool ok = true;
  while (remaining > 0 && ok) {
    const size_t n = remaining < sizeof buf ? remaining : sizeof buf;
    ok = readExact(s, buf, n) && f.write(buf, n) == n;
    remaining -= n;
  }
  f.close();
  if (!ok) {
    LittleFS.remove(storage::kImageTmp);
    return false;
  }
  return LittleFS.rename(storage::kImageTmp, storage::kImagePath);
}

bool receiveRegion(WiFiClient& s, const gray2::Region& r) {
  File f = LittleFS.open(storage::kImagePath, "r+");
  if (!f) return false;
  uint8_t buf[gray2::kRowBytes];
  const size_t rowLen = r.w / 8;
  bool ok = true;
  for (uint8_t plane = 0; plane < 2 && ok; ++plane) {
    for (uint16_t row = 0; row < r.h && ok; ++row) {
      ok = readExact(s, buf, rowLen) && f.seek(gray2::regionRowOffset(r, plane, row), SeekSet) &&
           f.write(buf, rowLen) == rowLen;
    }
  }
  f.close();
  return ok;
}

}  // namespace

Err connect(const Config& cfg, RtcState& rtc, uint32_t timeoutMs) {
  WiFi.persistent(false);  // never write credentials to the SDK flash area
  WiFi.forceSleepWake();
  delay(1);
  WiFi.mode(WIFI_STA);
  WiFi.hostname(cfg.name);
  if (cfg.ip[0]) {
    IPAddress ip, gw, mask, dns;
    ip.fromString(cfg.ip);
    gw.fromString(cfg.gw);
    mask.fromString(cfg.mask);
    if (!(cfg.dns[0] && dns.fromString(cfg.dns))) dns = gw;
    WiFi.config(ip, gw, mask, dns);
  }
  const char* pass = cfg.pass[0] ? cfg.pass : nullptr;

  int8_t st = -1;
  if (rtc.wifiValid) {
    WiFi.begin(cfg.ssid, pass, rtc.channel, rtc.bssid, true);
    st = WiFi.waitForConnectResult(timeoutMs);
    LOGF("Wi-Fi fast connect: %d\n", st);
    if (st != WL_CONNECTED) {
      rtc.wifiValid = 0;
      WiFi.disconnect();
    }
  }
  if (st != WL_CONNECTED) {
    WiFi.begin(cfg.ssid, pass);
    st = WiFi.waitForConnectResult(timeoutMs);
    LOGF("Wi-Fi connect: %d\n", st);
  }
  const Err err = mapStatus(st);
  if (err == Err::Ok) {
    rtc.channel = uint8_t(WiFi.channel());
    memcpy(rtc.bssid, WiFi.BSSID(), 6);
    rtc.wifiValid = 1;
    LOGF("Wi-Fi ok: %s rssi %d ch %u\n", WiFi.localIP().toString().c_str(), WiFi.RSSI(), rtc.channel);
  }
  return err;
}

void off() {
  WiFi.disconnect(true);
  WiFi.mode(WIFI_OFF);
  WiFi.forceSleepBegin();
  delay(1);
}

Err update(const UpdateRequest& req, UpdateResponse& res) {
  res = UpdateResponse();
  WiFiClient client;
  HTTPClient http;
  http.setTimeout(kHttpTimeoutMs);
  http.setReuse(false);
  http.useHTTP10(true);  // forces a plain Content-Length body (no chunked transfer encoding)
  if (!http.begin(client, req.url)) return Err::BadResponse;

  http.setUserAgent(String("WallISetup/") + FW_VERSION);
  http.addHeader("Authorization", String("Bearer ") + req.token);
  if (req.etag.length()) http.addHeader("If-None-Match", req.etag);
  http.addHeader("X-Fw", FW_VERSION);
  http.addHeader("X-Batt-mV", String(req.battMv));
  http.addHeader("X-Rssi", String(req.rssi));
  http.addHeader("X-Boot", req.bootInit ? "init" : "timer");
  if (req.lastErr != Err::Ok) http.addHeader("X-Last-Error", errName(req.lastErr));

  const char* keys[] = {"ETag", "X-Format", "X-Region", "X-Base", "X-Interval", "X-Low-Batt-mV", "X-Url"};
  http.collectHeaders(keys, sizeof keys / sizeof keys[0]);

  const int code = http.GET();
  res.http = code;
  LOGF("HTTP GET %s -> %d\n", req.url, code);
  if (code < 0) {
    http.end();
    return Err::ServerUnreachable;
  }

  if (code == 200 || code == 304) {
    uint32_t v;
    if (parseUInt(http.header("X-Interval").c_str(), v)) res.interval = clampInterval(v);
    if (parseUInt(http.header("X-Low-Batt-mV").c_str(), v) && lowBattValid(v)) res.lowBattMv = v;
    const String url = http.header("X-Url");
    if (url.length() && updateUrlValid(url.c_str())) res.url = url;
    res.etag = http.header("ETag");
  }

  Err err = Err::Ok;
  if (code == 304) {
    // Parameters only.
  } else if (code == 401 || code == 403) {
    err = Err::AuthRejected;
  } else if (code != 200) {
    err = Err::BadResponse;
  } else if (http.header("X-Format") != "gray2" || res.etag.length() == 0 || res.etag.length() > 64) {
    err = Err::BadResponse;
  } else {
    const String regionText = http.header("X-Region");
    gray2::Region region;
    const bool isRegion = regionText.length() > 0;
    if (isRegion && !gray2::parseRegion(regionText.c_str(), region)) {
      err = Err::BadResponse;
    } else if (isRegion && (http.header("X-Base") != req.etag || req.etag.length() == 0 || !storage::imageFileComplete())) {
      res.retryWithoutEtag = true;  // region for an image we don't have
      err = Err::BadResponse;
    } else {
      const uint32_t expected = isRegion ? gray2::regionBodyBytes(region) : gray2::kImageBytes;
      if (http.getSize() != int(expected)) {
        err = Err::BadResponse;
      } else {
        storage::invalidateImage();  // panel content and file diverge until the redraw succeeds
        WiFiClient& stream = http.getStream();
        const bool ok = isRegion ? receiveRegion(stream, region) : receiveFull(stream);
        if (ok) {
          res.changed = true;
          res.region = isRegion;
          res.bytes = expected;
        } else {
          err = Err::BadResponse;
        }
      }
    }
  }
  http.end();
  return err;
}

}  // namespace net
