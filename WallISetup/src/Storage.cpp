#include "Storage.h"

#include <ArduinoJson.h>
#include <Gray2.h>
#include <LittleFS.h>

#include "Log.h"

namespace storage {

bool begin() {
  if (LittleFS.begin()) return true;
  LOGF("LittleFS mount failed, formatting\n");
  if (!LittleFS.format()) return false;
  return LittleFS.begin();
}

bool loadConfig(walli::Config& cfg) {
  File f = LittleFS.open(kConfigPath, "r");
  if (!f) return false;
  JsonDocument doc;
  const DeserializationError de = deserializeJson(doc, f);
  f.close();
  if (de) {
    LOGF("config.json parse error: %s\n", de.c_str());
    return false;
  }
  walli::Config loaded;
  walli::ConfigError err;
  if (!walli::applyConfigJson(doc.as<JsonObjectConst>(), loaded, err, true)) {
    LOGF("config.json invalid: %s %s\n", err.field, err.msg);
    return false;
  }
  cfg = loaded;
  return true;
}

bool saveConfig(const walli::Config& cfg) {
  JsonDocument doc;
  walli::configToJson(cfg, doc.to<JsonObject>(), true);
  File f = LittleFS.open(kConfigTmp, "w");
  if (!f) return false;
  const size_t written = serializeJson(doc, f);
  f.close();
  if (written == 0) return false;
  return LittleFS.rename(kConfigTmp, kConfigPath);  // littlefs rename replaces atomically
}

bool imageFileComplete() {
  File f = LittleFS.open(kImagePath, "r");
  if (!f) return false;
  const bool ok = f.size() == walli::gray2::kImageBytes;
  f.close();
  return ok;
}

String imageEtag() {
  File f = LittleFS.open(kMetaPath, "r");
  if (!f) return String();
  String etag = f.readStringUntil('\n');
  f.close();
  etag.trim();
  return etag;
}

bool imageValid() { return imageEtag().length() > 0 && imageFileComplete(); }

void setImageEtag(const String& etag) {
  File f = LittleFS.open(kMetaPath, "w");
  if (!f) return;
  f.print(etag);
  f.print('\n');
  f.close();
}

void invalidateImage() {
  if (LittleFS.exists(kMetaPath)) LittleFS.remove(kMetaPath);
}

void eraseAll() {
  for (const char* p : {kConfigPath, kConfigTmp, kImagePath, kImageTmp, kMetaPath}) {
    if (LittleFS.exists(p)) LittleFS.remove(p);
  }
}

}  // namespace storage
