#include "Config.h"

#include <stdio.h>
#include <string.h>

#include "Params.h"

namespace walli {

namespace {

constexpr const char* kMasked = "***";

bool fail(ConfigError& err, const char* field, const char* msg) {
  err.field = field;
  err.msg = msg;
  return false;
}

// Copy a JSON string into a fixed buffer. Absent key → untouched.
// Wrong type or too long → error.
bool copyString(JsonVariantConst v, char* dst, size_t cap, const char* field, ConfigError& err) {
  if (v.isNull()) return true;
  if (!v.is<const char*>()) return fail(err, field, "must be a string");
  const char* s = v.as<const char*>();
  const size_t len = strlen(s);
  if (len >= cap) return fail(err, field, "too long");
  memcpy(dst, s, len + 1);
  return true;
}

// Secrets: absent, "" or "***" keep the stored value.
bool copySecret(JsonVariantConst v, char* dst, size_t cap, const char* field, ConfigError& err) {
  if (v.isNull()) return true;
  if (!v.is<const char*>()) return fail(err, field, "must be a string");
  const char* s = v.as<const char*>();
  if (s[0] == '\0' || strcmp(s, kMasked) == 0) return true;
  return copyString(v, dst, cap, field, err);
}

bool copyUInt(JsonVariantConst v, uint32_t& dst, const char* field, ConfigError& err) {
  if (v.isNull()) return true;
  if (!v.is<uint32_t>()) return fail(err, field, "must be a non-negative integer");
  dst = v.as<uint32_t>();
  return true;
}

bool inRange(size_t len, size_t lo, size_t hi) { return len >= lo && len <= hi; }

}  // namespace

bool ipv4Valid(const char* text) {
  if (text == nullptr || *text == '\0') return false;
  const char* p = text;
  for (int part = 0; part < 4; ++part) {
    if (*p < '0' || *p > '9') return false;
    int value = 0;
    int digits = 0;
    while (*p >= '0' && *p <= '9') {
      value = value * 10 + (*p - '0');
      if (++digits > 3 || value > 255) return false;
      ++p;
    }
    if (digits > 1 && p[-digits] == '0') return false;  // no leading zeros
    if (part < 3) {
      if (*p != '.') return false;
      ++p;
    }
  }
  return *p == '\0';
}

bool validateConfig(const Config& cfg, ConfigError& err) {
  if (!inRange(strlen(cfg.name), 1, 32)) return fail(err, "name", "must be 1-32 characters");
  if (!inRange(strlen(cfg.ssid), 1, 32)) return fail(err, "wifi.ssid", "required, 1-32 characters");
  const size_t passLen = strlen(cfg.pass);
  if (passLen != 0 && !inRange(passLen, 8, 63)) {
    return fail(err, "wifi.pass", "must be empty (open network) or 8-63 characters");
  }

  const bool anyNet = cfg.ip[0] || cfg.gw[0] || cfg.mask[0] || cfg.dns[0];
  if (anyNet) {
    if (!ipv4Valid(cfg.ip)) return fail(err, "net.ip", "must be a valid IPv4 address");
    if (!ipv4Valid(cfg.gw)) return fail(err, "net.gw", "must be a valid IPv4 address");
    if (!ipv4Valid(cfg.mask)) return fail(err, "net.mask", "must be a valid IPv4 address");
    if (cfg.dns[0] && !ipv4Valid(cfg.dns)) return fail(err, "net.dns", "must be empty or a valid IPv4 address");
  }

  if (!updateUrlValid(cfg.url)) return fail(err, "url", "required, must start with http:// (max 160 characters)");
  if (cfg.pendingUrl[0] && !updateUrlValid(cfg.pendingUrl)) return fail(err, "pendingUrl", "invalid URL");
  if (!inRange(strlen(cfg.token), 1, 64)) return fail(err, "token", "required, 1-64 characters");
  if (cfg.interval < kMinIntervalS || cfg.interval > kMaxIntervalS) {
    return fail(err, "interval", "must be 180-86400 seconds");
  }
  if (!lowBattValid(cfg.lowBattMv)) return fail(err, "lowBatt_mV", "must be 2800-4200");
  if (!battScaleValid(cfg.battScaleMv)) return fail(err, "battScale_mV", "must be 1000-15000");
  return true;
}

bool applyConfigJson(JsonObjectConst in, Config& cfg, ConfigError& err, bool includeInternal) {
  if (in.isNull()) return fail(err, "cfg", "must be an object");
  Config next = cfg;

  if (!copyString(in["name"], next.name, sizeof next.name, "name", err)) return false;

  JsonVariantConst wifi = in["wifi"];
  if (!wifi.isNull()) {
    if (!wifi.is<JsonObjectConst>()) return fail(err, "wifi", "must be an object");
    if (!copyString(wifi["ssid"], next.ssid, sizeof next.ssid, "wifi.ssid", err)) return false;
    if (includeInternal) {
      if (!copyString(wifi["pass"], next.pass, sizeof next.pass, "wifi.pass", err)) return false;
    } else {
      if (!copySecret(wifi["pass"], next.pass, sizeof next.pass, "wifi.pass", err)) return false;
    }
  }

  JsonVariantConst net = in["net"];
  if (!net.isNull()) {
    if (!net.is<JsonObjectConst>()) return fail(err, "net", "must be an object");
    if (!copyString(net["ip"], next.ip, sizeof next.ip, "net.ip", err)) return false;
    if (!copyString(net["gw"], next.gw, sizeof next.gw, "net.gw", err)) return false;
    if (!copyString(net["mask"], next.mask, sizeof next.mask, "net.mask", err)) return false;
    if (!copyString(net["dns"], next.dns, sizeof next.dns, "net.dns", err)) return false;
  }

  if (!copyString(in["url"], next.url, sizeof next.url, "url", err)) return false;
  if (includeInternal) {
    if (!copyString(in["token"], next.token, sizeof next.token, "token", err)) return false;
    if (!copyString(in["pendingUrl"], next.pendingUrl, sizeof next.pendingUrl, "pendingUrl", err)) return false;
  } else {
    if (!copySecret(in["token"], next.token, sizeof next.token, "token", err)) return false;
  }

  uint32_t interval = next.interval;
  uint32_t lowBatt = next.lowBattMv;
  uint32_t battScale = next.battScaleMv;
  if (!copyUInt(in["interval"], interval, "interval", err)) return false;
  if (!copyUInt(in["lowBatt_mV"], lowBatt, "lowBatt_mV", err)) return false;
  if (!copyUInt(in["battScale_mV"], battScale, "battScale_mV", err)) return false;
  if (lowBatt > 0xFFFF) return fail(err, "lowBatt_mV", "must be 2800-4200");
  if (battScale > 0xFFFF) return fail(err, "battScale_mV", "must be 1000-15000");
  next.interval = interval;
  next.lowBattMv = uint16_t(lowBatt);
  next.battScaleMv = uint16_t(battScale);

  if (!validateConfig(next, err)) return false;
  cfg = next;
  return true;
}

void configToJson(const Config& cfg, JsonObject out, bool forStorage) {
  auto secret = [&](const char* value) -> const char* {
    if (forStorage) return value;
    return value[0] ? kMasked : "";
  };
  out["name"] = cfg.name;
  JsonObject wifi = out["wifi"].to<JsonObject>();
  wifi["ssid"] = cfg.ssid;
  wifi["pass"] = secret(cfg.pass);
  JsonObject net = out["net"].to<JsonObject>();
  net["ip"] = cfg.ip;
  net["gw"] = cfg.gw;
  net["mask"] = cfg.mask;
  net["dns"] = cfg.dns;
  out["url"] = cfg.url;
  out["token"] = secret(cfg.token);
  out["interval"] = cfg.interval;
  out["lowBatt_mV"] = cfg.lowBattMv;
  out["battScale_mV"] = cfg.battScaleMv;
  if (forStorage) out["pendingUrl"] = cfg.pendingUrl;
}

void applyDefaultName(Config& cfg, const uint8_t mac[6]) {
  if (cfg.name[0] != '\0') return;
  snprintf(cfg.name, sizeof cfg.name, "wallpanel-%02x%02x", mac[4], mac[5]);
}

}  // namespace walli
