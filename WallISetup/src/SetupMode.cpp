// SETUP mode: JSON-lines command server on USB serial for the setup page.
// docs/protocol.md §3, docs/firmware-design.md §3.2 and §4.

#include <ArduinoJson.h>
#include <ESP8266WiFi.h>
#include <LittleFS.h>

#include "Cycle.h"
#include "Led.h"
#include "Modes.h"
#include "Net.h"
#include "Rtc.h"
#include "Screens.h"
#include "Storage.h"
#include "pins.h"

using namespace walli;

namespace setupmode {

namespace {

constexpr size_t kMaxLine = 1024;
constexpr uint32_t kExitLowMs = 500;   // RX LOW this long = CH340 switched off → restart into RUN
constexpr size_t kMaxNetworks = 12;

char g_line[kMaxLine + 1];
size_t g_len = 0;
bool g_overflow = false;
Config g_cfg;
bool g_haveCfg = false;
#if !WALLI_FORCE_RUN
uint32_t g_rxLowSince = 0;
uint32_t g_lastSample = 0;
#endif

void send(JsonDocument& doc) {
  serializeJson(doc, Serial);
  Serial.print('\n');
}

void logEvent(const String& msg) {
  JsonDocument doc;
  doc["evt"] = "log";
  doc["msg"] = msg;
  send(doc);
}

void fail(JsonDocument& res, const char* code, const char* msg, const char* field = nullptr) {
  res["ok"] = false;
  res["err"] = code;
  res["msg"] = msg;
  if (field) res["field"] = field;
}

String hostOf(const char* url) {
  String h(url);
  if (h.startsWith("http://")) h.remove(0, 7);
  const int slash = h.indexOf('/');
  if (slash >= 0) h.remove(slash);
  return h;
}

Config defaults() {
  Config c;
  uint8_t mac[6];
  WiFi.macAddress(mac);
  applyDefaultName(c, mac);
  return c;
}

void cmdInfo(JsonDocument& res) {
  res["ok"] = true;
  res["fw"] = FW_VERSION;
  res["mac"] = WiFi.macAddress();
  res["chip"] = "ESP8266";
  res["flash"] = ESP.getFlashChipRealSize();
  res["heap"] = ESP.getFreeHeap();
  res["batt_mV"] = cycle::readBatteryMv(g_haveCfg ? g_cfg.battScaleMv : 4200);
  res["hasImage"] = storage::imageFileComplete();
  res["etag"] = storage::imageEtag();
}

void cmdGet(JsonDocument& res) {
  res["ok"] = true;
  configToJson(g_haveCfg ? g_cfg : defaults(), res["cfg"].to<JsonObject>(), false);
}

void cmdSet(JsonDocument& req, JsonDocument& res) {
  Config next = g_haveCfg ? g_cfg : defaults();
  ConfigError err;
  if (!applyConfigJson(req["cfg"].as<JsonObjectConst>(), next, err)) {
    fail(res, "invalid", err.msg, err.field);
    return;
  }
  next.pendingUrl[0] = '\0';  // a manual setup overrides any pending server move
  if (!storage::saveConfig(next)) {
    fail(res, "storage", "could not write the configuration to flash");
    return;
  }
  g_cfg = next;
  g_haveCfg = true;
  res["ok"] = true;
}

void cmdScan(JsonDocument& res) {
  WiFi.forceSleepWake();
  delay(1);
  WiFi.mode(WIFI_STA);
  WiFi.disconnect();
  const int n = WiFi.scanNetworks(false, false);
  struct Net {
    String ssid;
    int32_t rssi;
    int32_t ch;
    bool enc;
  };
  Net nets[kMaxNetworks];
  size_t count = 0;
  for (int i = 0; i < n; ++i) {
    const String ssid = WiFi.SSID(i);
    if (ssid.length() == 0) continue;  // hidden
    size_t j = 0;
    while (j < count && nets[j].ssid != ssid) ++j;
    const int32_t rssi = WiFi.RSSI(i);
    if (j < count) {  // duplicate SSID (mesh / several APs): keep the strongest
      if (rssi > nets[j].rssi) nets[j] = {ssid, rssi, WiFi.channel(i), WiFi.encryptionType(i) != ENC_TYPE_NONE};
      continue;
    }
    Net entry{ssid, rssi, WiFi.channel(i), WiFi.encryptionType(i) != ENC_TYPE_NONE};
    if (count < kMaxNetworks) {
      nets[count++] = entry;
    } else {  // replace the weakest if this one is stronger
      size_t weakest = 0;
      for (size_t k = 1; k < count; ++k)
        if (nets[k].rssi < nets[weakest].rssi) weakest = k;
      if (rssi > nets[weakest].rssi) nets[weakest] = entry;
    }
  }
  WiFi.scanDelete();
  net::off();
  // Sort by RSSI, strongest first (tiny list: insertion sort).
  for (size_t i = 1; i < count; ++i)
    for (size_t k = i; k > 0 && nets[k].rssi > nets[k - 1].rssi; --k) std::swap(nets[k], nets[k - 1]);
  res["ok"] = true;
  JsonArray arr = res["nets"].to<JsonArray>();
  for (size_t i = 0; i < count; ++i) {
    JsonObject o = arr.add<JsonObject>();
    o["ssid"] = nets[i].ssid;
    o["rssi"] = nets[i].rssi;
    o["ch"] = nets[i].ch;
    o["enc"] = nets[i].enc;
  }
}

bool requireConfig(JsonDocument& res) {
  if (g_haveCfg) return true;
  fail(res, errName(Err::NoConfig), "save a configuration first");
  return false;
}

void cmdTestWifi(JsonDocument& res) {
  if (!requireConfig(res)) return;
  RtcState rtc = rtc::load();
  const uint32_t t0 = millis();
  const Err err = net::connect(g_cfg, rtc);
  if (err == Err::Ok) {
    res["ok"] = true;
    res["ip"] = WiFi.localIP().toString();
    res["rssi"] = WiFi.RSSI();
    res["ms"] = millis() - t0;
    rtc::save(rtc);
  } else {
    fail(res, errName(err), errHuman(err));
  }
  net::off();
}

void cmdTestUpdate(JsonDocument& res) {
  if (!requireConfig(res)) return;
  RtcState rtc = rtc::load();
  bool cfgChanged = false;
  const uint32_t batt = cycle::readBatteryMv(g_cfg.battScaleMv);
  const cycle::Result r = cycle::run(g_cfg, rtc, true, batt, cfgChanged);
  if (cfgChanged) storage::saveConfig(g_cfg);
  rtc::save(rtc);
  if (r.err != Err::Ok) {
    fail(res, errName(r.err), errHuman(r.err));
    res["http"] = r.http;
    return;
  }
  res["ok"] = true;
  res["http"] = r.http;
  res["bytes"] = r.bytes;
  res["region"] = r.region;
  res["interval"] = g_cfg.interval;
  res["ms"] = r.ms;
}

void cmdTestDisplay(JsonDocument& res) {
  const uint32_t t0 = millis();
  screens::TextScreen s;
  s.title = "Display test";
  s.lines[0] = "Levels below: black | dark grey | light grey | white";
  s.lines[1] = String("Firmware ") + FW_VERSION + "    MAC " + WiFi.macAddress();
  s.lines[2] = String("Free heap ") + ESP.getFreeHeap() + " B";
  s.greyBars = true;
  if (!screens::write(s)) {
    fail(res, "storage", "could not render the test pattern");
    return;
  }
  const Err err = cycle::showStoredImage();
  if (err != Err::Ok) {
    fail(res, errName(err), errHuman(err));
    return;
  }
  res["ok"] = true;
  res["ms"] = millis() - t0;
}

void cmdFactoryReset(JsonDocument& req, JsonDocument& res) {
  if (!(req["confirm"] | false)) {
    fail(res, "invalid", "send \"confirm\": true to erase the configuration", "confirm");
    return;
  }
  storage::eraseAll();
  rtc::clear();
  g_haveCfg = false;
  g_cfg = Config();
  res["ok"] = true;
}

void handleLine(char* line) {
  JsonDocument req;
  if (deserializeJson(req, line)) return;  // not JSON (boot noise) → ignore
  const char* cmd = req["cmd"];
  if (cmd == nullptr) return;

  JsonDocument res;
  res["id"] = req["id"];
  bool reboot = false;
  bool startRun = false;
  if (!strcmp(cmd, "info")) cmdInfo(res);
  else if (!strcmp(cmd, "get")) cmdGet(res);
  else if (!strcmp(cmd, "set")) cmdSet(req, res);
  else if (!strcmp(cmd, "scan")) cmdScan(res);
  else if (!strcmp(cmd, "test_wifi")) cmdTestWifi(res);
  else if (!strcmp(cmd, "test_update")) cmdTestUpdate(res);
  else if (!strcmp(cmd, "test_display")) cmdTestDisplay(res);
  else if (!strcmp(cmd, "factory_reset")) cmdFactoryReset(req, res);
  else if (!strcmp(cmd, "reboot")) {
    res["ok"] = true;
    reboot = true;
  } else if (!strcmp(cmd, "run")) {
    // One update cycle now, then deep sleep. The next boot follows the switches again
    // (decideBootMode): with the CH340 switch still ON it comes back in setup mode.
    if (requireConfig(res)) {
      res["ok"] = true;
      res["interval"] = g_cfg.interval;
      startRun = true;
    }
  } else {
    fail(res, "unknown_cmd", "unknown command");
  }
  send(res);
  if (reboot) {
    Serial.flush();
    delay(100);
    ESP.restart();
  }
  if (startRun) {
    Serial.flush();
    delay(100);
    led::off();
    runmode::run(true);  // never returns: ends in deep sleep
  }
}

void pollSerial() {
  while (Serial.available() > 0) {
    const char c = char(Serial.read());
    if (c == '\r') continue;
    if (c == '\n') {
      if (!g_overflow && g_len > 0) {
        g_line[g_len] = '\0';
        handleLine(g_line);
      }
      g_len = 0;
      g_overflow = false;
      continue;
    }
    if (g_len < kMaxLine) {
      g_line[g_len++] = c;
    } else {
      g_overflow = true;  // drop the whole line
    }
  }
}

#if !WALLI_FORCE_RUN
// RX line level as seen by UART0. While GPIO3 is muxed to the UART, the GPIO input register
// (GPIP) does not reflect the pad — it reads LOW and caused restart loops. The UART status
// register's USRXD bit is the RX pin level in UART mode.
bool rxLineHigh() { return (USS(0) >> USRXD) & 1; }
#endif

// The CH340's TXD idles HIGH while it is powered. If RX stays LOW for 500 ms (impossible during
// UART traffic) the CH340 switch was turned OFF → restart into RUN mode (firmware-design.md §3.2).
void checkExit() {
#if !WALLI_FORCE_RUN
  const uint32_t now = millis();
  if (now - g_lastSample < 10) return;
  g_lastSample = now;
  if (rxLineHigh()) {
    g_rxLowSince = 0;
  } else if (g_rxLowSince == 0) {
    g_rxLowSince = now;
  } else if (now - g_rxLowSince > kExitLowMs) {
    logEvent("CH340 switched off (RX low), restarting into RUN mode");  // lost with the CH340 off
    Serial.flush();
    led::off();
    ESP.restart();
  }
#endif
}

}  // namespace

void begin() {
  Serial.setRxBufferSize(kMaxLine + 64);
  Serial.begin(115200);
  led::begin();
  net::off();
  storage::begin();
  g_haveCfg = storage::loadConfig(g_cfg);

  // Show the setup instructions on the panel (also proves the display works).
  screens::TextScreen s;
  s.title = "Setup mode";
  s.lines[0] = "1. Connect this panel to a computer with USB";
  s.lines[1] = String("2. Open http://") + (g_haveCfg ? hostOf(g_cfg.url) : String("<server>:5080")) +
               "/setup/ in Chrome or Edge";
  s.lines[2] = "3. Save, then turn the CH340 switch OFF";
  s.lines[4] = String("Firmware ") + FW_VERSION + "    MAC " + WiFi.macAddress() +
               (g_haveCfg ? "    configured" : "    not configured");
  if (screens::write(s)) {
    if (cycle::showStoredImage() != Err::Ok) logEvent("display not responding");
  }

  JsonDocument ready;
  ready["evt"] = "ready";
  ready["fw"] = FW_VERSION;
  ready["mode"] = "setup";
  send(ready);
}

void loop() {
  led::setupTick();
  pollSerial();
  checkExit();
  delay(2);
}

}  // namespace setupmode
