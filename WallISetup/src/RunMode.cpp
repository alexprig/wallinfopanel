// RUN mode: one update cycle, optional LED/e-paper status report, then deep sleep.
// docs/firmware-design.md §2, §6, §7.

#include <ESP8266WiFi.h>
#include <Params.h>

#include "Cycle.h"
#include "Led.h"
#include "Log.h"
#include "Modes.h"
#include "Net.h"
#include "Rtc.h"
#include "Screens.h"
#include "Storage.h"

using namespace walli;

namespace runmode {

namespace {

String urlHost(const char* url) {
  String s(url);
  if (s.startsWith("http://")) s.remove(0, 7);
  const int slash = s.indexOf('/');
  if (slash >= 0) s.remove(slash);
  return s;
}

void drawStatus(Err err, const Config* cfg, uint32_t battMv, int32_t rssi, uint32_t nextSleepS) {
  screens::TextScreen s;
  if (err == Err::NoConfig) {
    s.title = "Not configured";
    s.lines[0] = "1. Turn the CH340 switch ON, press RST, connect USB";
    s.lines[1] = "2. Open the setup page in Chrome or Edge: http://<server>:5080/setup/";
    s.lines[2] = "3. Save, then turn the CH340 switch OFF";
  } else {
    s.title = String("Error ") + errBlinks(err) + ": " + errHuman(err);
    if (cfg) {
      s.lines[0] = String("Wi-Fi: ") + cfg->ssid + (rssi ? String("   RSSI: ") + rssi + " dBm" : String());
      s.lines[1] = String("Server: ") + urlHost(cfg->url);
    }
    s.lines[3] = String("Next attempt in ") + (nextSleepS + 59) / 60 + " min";
  }
  s.lines[4] = String("Battery: ") + String(battMv / 1000.0f, 2) + " V    Firmware " + FW_VERSION + "    MAC " +
               WiFi.macAddress();
  if (screens::write(s)) cycle::showStoredImage();
}

[[noreturn]] void sleepFor(RtcState& rtc, uint32_t totalS) {
  uint32_t maxChunk = uint32_t(ESP.deepSleepMax() / 1000000ULL * 95 / 100);
  if (maxChunk == 0 || maxChunk > kMaxSleepChunkS) maxChunk = kMaxSleepChunkS;
  const SleepPlan plan = planSleep(totalS, maxChunk);
  rtc.remainingSleepS = plan.remainingS;
  rtc::save(rtc);
  led::off();
  // The wake after this chunk needs Wi-Fi only if no further chunk follows.
  const RFMode mode = plan.remainingS > 0 ? WAKE_RF_DISABLED : WAKE_RF_DEFAULT;
  LOGF("deep sleep %lu s (remaining %lu s)\n", static_cast<unsigned long>(plan.chunkS),
       static_cast<unsigned long>(plan.remainingS));
#if WALLI_DEBUG
  Serial.flush();
#endif
  ESP.deepSleep(uint64_t(plan.chunkS) * 1000000ULL, mode);
  for (;;) delay(1000);  // not reached
}

}  // namespace

void run(bool bootInit) {
  RtcState rtc = rtc::load();
  if (bootInit) {
    rtc.remainingSleepS = 0;
    rtc.failures = 0;
    rtc.lastErr = 0;
  } else if (rtc.remainingSleepS > 0) {
    sleepFor(rtc, rtc.remainingSleepS);  // intermediate chunk of a long sleep: no Wi-Fi, no work
  }

  led::begin();
  if (bootInit) led::on();  // solid while working
  net::off();               // radio stays off until we actually connect

  storage::begin();
  Config cfg;
  const bool haveCfg = storage::loadConfig(cfg);
  const uint32_t battMv = cycle::readBatteryMv(haveCfg ? cfg.battScaleMv : 4200);
  const bool lowBatt = haveCfg && battMv >= 1000 && battMv < cfg.lowBattMv;  // <1 V = no divider fitted
  LOGF("run: init=%d cfg=%d batt=%lu mV\n", bootInit, haveCfg, static_cast<unsigned long>(battMv));

  if (!haveCfg) {
    if (bootInit) {
      drawStatus(Err::NoConfig, nullptr, battMv, 0, 0);
      led::report(Err::NoConfig, false);
    }
    rtc::save(rtc);
    LOGF("no config: sleeping until reset\n");
    ESP.deepSleep(0);  // wake only by RST / power cycle
    for (;;) delay(1000);
  }

  bool cfgChanged = false;
  const cycle::Result res = cycle::run(cfg, rtc, bootInit, battMv, cfgChanged);
  if (cfgChanged) storage::saveConfig(cfg);

  rtc.failures = res.err == Err::Ok ? 0 : uint8_t(rtc.failures < 250 ? rtc.failures + 1 : 250);
  rtc.lastErr = static_cast<uint8_t>(res.err);
  const uint32_t sleepS = nextSleepSeconds(cfg.interval, rtc.failures);

  if (bootInit && res.err != Err::Ok && res.err != Err::Display) {
    drawStatus(res.err, &cfg, battMv, res.rssi, sleepS);
  }
  if (bootInit) led::report(res.err, lowBatt);
  sleepFor(rtc, sleepS);
}

}  // namespace runmode
