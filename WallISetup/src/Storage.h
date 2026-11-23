#pragma once
// LittleFS storage: configuration and the stored gray2 image (flash is the frame buffer).

#include <Arduino.h>
#include <Config.h>

namespace storage {

constexpr const char* kConfigPath = "/config.json";
constexpr const char* kConfigTmp = "/config.tmp";
constexpr const char* kImagePath = "/img.g2";   // 53,856 B: plane 0 ‖ plane 1
constexpr const char* kImageTmp = "/img.tmp";
constexpr const char* kMetaPath = "/img.meta";  // ETag of the image currently on the panel

bool begin();  // mounts LittleFS, formats it if mounting fails

// Loads and validates the configuration. Returns false if missing or invalid.
bool loadConfig(walli::Config& cfg);
bool saveConfig(const walli::Config& cfg);  // atomic (temp file + rename)

// True if the image file has the right size and its ETag is known (= it is on the panel).
bool imageValid();
String imageEtag();                 // "" if none
void setImageEtag(const String& etag);
void invalidateImage();             // keeps the file, forgets the ETag → next update fetches a full image
bool imageFileComplete();           // file exists with the full gray2 size

void eraseAll();                    // factory reset: config + image + meta

}  // namespace storage
