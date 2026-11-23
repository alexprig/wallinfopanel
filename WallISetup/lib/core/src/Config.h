#pragma once
// Device configuration — docs/protocol.md §3.1.

#include <ArduinoJson.h>
#include <stdint.h>

namespace walli {

struct Config {
  char name[33] = "";
  char ssid[33] = "";
  char pass[65] = "";
  char ip[16] = "";
  char gw[16] = "";
  char mask[16] = "";
  char dns[16] = "";
  char url[161] = "";
  char pendingUrl[161] = "";  // internal: URL announced by X-Url, not yet confirmed
  char token[65] = "";
  uint32_t interval = 900;
  uint16_t lowBattMv = 3300;
  uint16_t battScaleMv = 4200;
};

struct ConfigError {
  const char* field = nullptr;  // protocol key, e.g. "wifi.pass"
  const char* msg = nullptr;    // human-readable rule
};

// Check every rule of protocol.md §3.1. Returns false and fills `err` on the first violation.
bool validateConfig(const Config& cfg, ConfigError& err);

// Merge a (full or partial) configuration object into a copy of `cfg`, validate, and on
// success replace `cfg`. Secret fields that are absent, empty or "***" keep their value.
// `includeInternal` also accepts internal keys (pendingUrl) — used when loading from flash.
bool applyConfigJson(JsonObjectConst in, Config& cfg, ConfigError& err, bool includeInternal = false);

// Serialise. `forStorage` writes secrets and internal keys in clear text; otherwise secrets
// are replaced by "***" (set) or "" (unset), as the `get` command requires.
void configToJson(const Config& cfg, JsonObject out, bool forStorage);

// Dotted IPv4 check, e.g. "192.168.1.50".
bool ipv4Valid(const char* text);

// Sets the default name "wallpanel-<last 4 hex of MAC>" if the name is empty.
void applyDefaultName(Config& cfg, const uint8_t mac[6]);

}  // namespace walli
