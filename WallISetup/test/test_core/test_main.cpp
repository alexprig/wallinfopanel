// Host-side unit tests for the hardware-independent core (pio test -e native).

#include <unity.h>

#include <ArduinoJson.h>
#include <string.h>

#include "Config.h"
#include "Errors.h"
#include "Gray2.h"
#include "Params.h"
#include "BootMode.h"

using namespace walli;

void setUp() {}
void tearDown() {}

// ------------------------------------------------------------------ gray2

static void test_geometry_constants() {
  TEST_ASSERT_EQUAL_UINT32(99, gray2::kRowBytes);
  TEST_ASSERT_EQUAL_UINT32(26928, gray2::kPlaneBytes);
  TEST_ASSERT_EQUAL_UINT32(53856, gray2::kImageBytes);
  // Slave half ends exactly at the last row byte.
  TEST_ASSERT_EQUAL_UINT32(gray2::kRowBytes, gray2::kSlaveFirstByte + gray2::kHalfBytes);
}

static void test_pack_byte_levels() {
  uint8_t p0, p1;
  const uint8_t white[8] = {3, 3, 3, 3, 3, 3, 3, 3};
  gray2::packByte(white, p0, p1);
  TEST_ASSERT_EQUAL_HEX8(0xFF, p0);
  TEST_ASSERT_EQUAL_HEX8(0xFF, p1);

  // Leftmost pixel light grey (2 = 0b10) -> only plane 1, bit 7.
  const uint8_t lightFirst[8] = {2, 0, 0, 0, 0, 0, 0, 0};
  gray2::packByte(lightFirst, p0, p1);
  TEST_ASSERT_EQUAL_HEX8(0x00, p0);
  TEST_ASSERT_EQUAL_HEX8(0x80, p1);

  // Rightmost pixel dark grey (1 = 0b01) -> only plane 0, bit 0.
  const uint8_t darkLast[8] = {0, 0, 0, 0, 0, 0, 0, 1};
  gray2::packByte(darkLast, p0, p1);
  TEST_ASSERT_EQUAL_HEX8(0x01, p0);
  TEST_ASSERT_EQUAL_HEX8(0x00, p1);
}

static void test_parse_region_valid() {
  gray2::Region r;
  TEST_ASSERT_TRUE(gray2::parseRegion("8,10,64,20", r));
  TEST_ASSERT_EQUAL_UINT16(8, r.x);
  TEST_ASSERT_EQUAL_UINT16(10, r.y);
  TEST_ASSERT_EQUAL_UINT16(64, r.w);
  TEST_ASSERT_EQUAL_UINT16(20, r.h);
  TEST_ASSERT_TRUE(gray2::parseRegion(" 0 , 0 , 792 , 272 ", r));
  TEST_ASSERT_TRUE(gray2::parseRegion("784,271,8,1", r));  // last byte column, last row
}

static void test_parse_region_invalid() {
  gray2::Region r;
  TEST_ASSERT_FALSE(gray2::parseRegion("4,0,8,1", r));      // x not on 8-px grid
  TEST_ASSERT_FALSE(gray2::parseRegion("0,0,12,1", r));     // w not multiple of 8
  TEST_ASSERT_FALSE(gray2::parseRegion("0,0,0,1", r));      // empty
  TEST_ASSERT_FALSE(gray2::parseRegion("0,0,8,0", r));      // empty
  TEST_ASSERT_FALSE(gray2::parseRegion("792,0,8,1", r));    // outside
  TEST_ASSERT_FALSE(gray2::parseRegion("0,200,8,73", r));   // y+h > 272
  TEST_ASSERT_FALSE(gray2::parseRegion("0,0,8", r));        // missing field
  TEST_ASSERT_FALSE(gray2::parseRegion("0,0,8,1,5", r));    // extra field
  TEST_ASSERT_FALSE(gray2::parseRegion("a,0,8,1", r));
  TEST_ASSERT_FALSE(gray2::parseRegion("-8,0,8,1", r));
  TEST_ASSERT_FALSE(gray2::parseRegion("", r));
  TEST_ASSERT_FALSE(gray2::parseRegion(nullptr, r));
}

static void test_region_body_and_offsets() {
  gray2::Region r;
  TEST_ASSERT_TRUE(gray2::parseRegion("16,2,24,3", r));
  TEST_ASSERT_EQUAL_UINT32(2 * 3 * 3, gray2::regionBodyBytes(r));
  TEST_ASSERT_EQUAL_UINT32(2 * 99 + 2, gray2::regionRowOffset(r, 0, 0));
  TEST_ASSERT_EQUAL_UINT32(4 * 99 + 2, gray2::regionRowOffset(r, 0, 2));
  TEST_ASSERT_EQUAL_UINT32(26928 + 3 * 99 + 2, gray2::regionRowOffset(r, 1, 1));

  TEST_ASSERT_TRUE(gray2::parseRegion("0,0,792,272", r));
  TEST_ASSERT_EQUAL_UINT32(gray2::kImageBytes, gray2::regionBodyBytes(r));
}

// ------------------------------------------------------------------ params

static void test_parse_uint() {
  uint32_t v = 0;
  TEST_ASSERT_TRUE(parseUInt("900", v));
  TEST_ASSERT_EQUAL_UINT32(900, v);
  TEST_ASSERT_TRUE(parseUInt(" 4294967295 ", v));
  TEST_ASSERT_EQUAL_UINT32(4294967295u, v);
  TEST_ASSERT_FALSE(parseUInt("4294967296", v));
  TEST_ASSERT_FALSE(parseUInt("-1", v));
  TEST_ASSERT_FALSE(parseUInt("12a", v));
  TEST_ASSERT_FALSE(parseUInt("", v));
  TEST_ASSERT_FALSE(parseUInt(nullptr, v));
}

static void test_clamp_interval() {
  TEST_ASSERT_EQUAL_UINT32(180, clampInterval(0));
  TEST_ASSERT_EQUAL_UINT32(180, clampInterval(60));
  TEST_ASSERT_EQUAL_UINT32(900, clampInterval(900));
  TEST_ASSERT_EQUAL_UINT32(86400, clampInterval(100000));
}

static void test_backoff() {
  TEST_ASSERT_EQUAL_UINT32(900, nextSleepSeconds(900, 0));
  TEST_ASSERT_EQUAL_UINT32(300, nextSleepSeconds(900, 1));
  TEST_ASSERT_EQUAL_UINT32(600, nextSleepSeconds(900, 2));
  TEST_ASSERT_EQUAL_UINT32(900, nextSleepSeconds(900, 3));   // capped by interval
  TEST_ASSERT_EQUAL_UINT32(76800, nextSleepSeconds(86400, 9));
  TEST_ASSERT_EQUAL_UINT32(76800, nextSleepSeconds(86400, 200));  // shift capped, no overflow
  TEST_ASSERT_EQUAL_UINT32(180, nextSleepSeconds(180, 1));
}

static void test_plan_sleep() {
  SleepPlan p = planSleep(900);
  TEST_ASSERT_EQUAL_UINT32(900, p.chunkS);
  TEST_ASSERT_EQUAL_UINT32(0, p.remainingS);
  p = planSleep(36000);
  TEST_ASSERT_EQUAL_UINT32(10800, p.chunkS);
  TEST_ASSERT_EQUAL_UINT32(25200, p.remainingS);
  p = planSleep(10800);
  TEST_ASSERT_EQUAL_UINT32(10800, p.chunkS);
  TEST_ASSERT_EQUAL_UINT32(0, p.remainingS);
}

static void test_url_valid() {
  TEST_ASSERT_TRUE(updateUrlValid("http://192.168.1.10:5080/api/panel/livingroom"));
  TEST_ASSERT_TRUE(updateUrlValid("http://server/api/panel/a"));
  TEST_ASSERT_FALSE(updateUrlValid("https://server/api/panel/a"));
  TEST_ASSERT_FALSE(updateUrlValid("http://"));
  TEST_ASSERT_FALSE(updateUrlValid("http:///x"));
  TEST_ASSERT_FALSE(updateUrlValid("http://a b/"));
  char longUrl[200];
  memset(longUrl, 'a', sizeof longUrl);
  memcpy(longUrl, "http://", 7);
  longUrl[161] = '\0';
  TEST_ASSERT_FALSE(updateUrlValid(longUrl));
  longUrl[160] = '\0';
  TEST_ASSERT_TRUE(updateUrlValid(longUrl));
}

static void test_battery_mv() {
  TEST_ASSERT_EQUAL_UINT32(4200, batteryMilliVolts(1023, 4200));
  TEST_ASSERT_EQUAL_UINT32(0, batteryMilliVolts(0, 4200));
  TEST_ASSERT_EQUAL_UINT32(2100, batteryMilliVolts(511, 4200) + 2);  // rounding: 2098
  TEST_ASSERT_EQUAL_UINT32(4200, batteryMilliVolts(5000, 4200));     // clamped raw
}

// ------------------------------------------------------------------ errors

static void test_error_names() {
  TEST_ASSERT_EQUAL_STRING("wifi_auth", errName(Err::WifiAuth));
  TEST_ASSERT_EQUAL_UINT8(4, errBlinks(Err::WifiAuth));
  TEST_ASSERT_EQUAL_UINT8(0, errBlinks(Err::Ok));
  Err e;
  TEST_ASSERT_TRUE(errFromName("display", e));
  TEST_ASSERT_TRUE(e == Err::Display);
  TEST_ASSERT_FALSE(errFromName("nope", e));
}

// ------------------------------------------------------------------ config

static Config validConfig() {
  Config c;
  strcpy(c.name, "livingroom");
  strcpy(c.ssid, "Home");
  strcpy(c.pass, "secret123");
  strcpy(c.url, "http://192.168.1.10:5080/api/panel/livingroom");
  strcpy(c.token, "tok");
  return c;
}

static void test_ipv4() {
  TEST_ASSERT_TRUE(ipv4Valid("192.168.1.50"));
  TEST_ASSERT_TRUE(ipv4Valid("0.0.0.0"));
  TEST_ASSERT_FALSE(ipv4Valid("256.1.1.1"));
  TEST_ASSERT_FALSE(ipv4Valid("1.1.1"));
  TEST_ASSERT_FALSE(ipv4Valid("1.1.1.1.1"));
  TEST_ASSERT_FALSE(ipv4Valid("01.1.1.1"));
  TEST_ASSERT_FALSE(ipv4Valid(""));
}

static void test_validate_ok_and_rules() {
  ConfigError err;
  Config c = validConfig();
  TEST_ASSERT_TRUE(validateConfig(c, err));

  c = validConfig();
  c.pass[0] = '\0';  // open network allowed
  TEST_ASSERT_TRUE(validateConfig(c, err));

  c = validConfig();
  strcpy(c.pass, "short");
  TEST_ASSERT_FALSE(validateConfig(c, err));
  TEST_ASSERT_EQUAL_STRING("wifi.pass", err.field);

  c = validConfig();
  c.ssid[0] = '\0';
  TEST_ASSERT_FALSE(validateConfig(c, err));
  TEST_ASSERT_EQUAL_STRING("wifi.ssid", err.field);

  c = validConfig();
  c.interval = 179;
  TEST_ASSERT_FALSE(validateConfig(c, err));
  TEST_ASSERT_EQUAL_STRING("interval", err.field);

  c = validConfig();
  strcpy(c.ip, "192.168.1.50");  // static IP without gateway
  TEST_ASSERT_FALSE(validateConfig(c, err));
  TEST_ASSERT_EQUAL_STRING("net.gw", err.field);

  strcpy(c.gw, "192.168.1.1");
  strcpy(c.mask, "255.255.255.0");
  TEST_ASSERT_TRUE(validateConfig(c, err));  // dns optional
}

static void test_apply_json_merges_and_keeps_secrets() {
  Config c = validConfig();
  JsonDocument doc;
  deserializeJson(doc, R"({"interval":1800,"wifi":{"ssid":"Other","pass":"***"},"token":""})");
  ConfigError err;
  TEST_ASSERT_TRUE(applyConfigJson(doc.as<JsonObjectConst>(), c, err));
  TEST_ASSERT_EQUAL_UINT32(1800, c.interval);
  TEST_ASSERT_EQUAL_STRING("Other", c.ssid);
  TEST_ASSERT_EQUAL_STRING("secret123", c.pass);  // "***" keeps
  TEST_ASSERT_EQUAL_STRING("tok", c.token);       // "" keeps
}

static void test_apply_json_rejects_without_partial_change() {
  Config c = validConfig();
  JsonDocument doc;
  deserializeJson(doc, R"({"interval":60,"name":"changed"})");
  ConfigError err;
  TEST_ASSERT_FALSE(applyConfigJson(doc.as<JsonObjectConst>(), c, err));
  TEST_ASSERT_EQUAL_STRING("interval", err.field);
  TEST_ASSERT_EQUAL_STRING("livingroom", c.name);  // untouched on failure
  TEST_ASSERT_EQUAL_UINT32(900, c.interval);
}

static void test_apply_json_type_errors() {
  Config c = validConfig();
  ConfigError err;
  JsonDocument doc;
  deserializeJson(doc, R"({"interval":"900"})");
  TEST_ASSERT_FALSE(applyConfigJson(doc.as<JsonObjectConst>(), c, err));
  TEST_ASSERT_EQUAL_STRING("interval", err.field);

  deserializeJson(doc, R"({"wifi":"Home"})");
  TEST_ASSERT_FALSE(applyConfigJson(doc.as<JsonObjectConst>(), c, err));
  TEST_ASSERT_EQUAL_STRING("wifi", err.field);

  deserializeJson(doc, R"({"name":"0123456789012345678901234567890123"})");  // 34 chars
  TEST_ASSERT_FALSE(applyConfigJson(doc.as<JsonObjectConst>(), c, err));
  TEST_ASSERT_EQUAL_STRING("name", err.field);
}

static void test_to_json_masks_secrets() {
  Config c = validConfig();
  JsonDocument doc;
  configToJson(c, doc.to<JsonObject>(), false);
  TEST_ASSERT_EQUAL_STRING("***", doc["wifi"]["pass"]);
  TEST_ASSERT_EQUAL_STRING("***", doc["token"]);
  TEST_ASSERT_TRUE(doc["pendingUrl"].isNull());

  c.pass[0] = '\0';
  JsonDocument doc2;
  configToJson(c, doc2.to<JsonObject>(), false);
  TEST_ASSERT_EQUAL_STRING("", doc2["wifi"]["pass"]);
}

static void test_storage_round_trip() {
  Config c = validConfig();
  strcpy(c.pendingUrl, "http://new-server/api/panel/livingroom");
  c.pass[0] = '\0';  // empty secret must survive storage
  JsonDocument doc;
  configToJson(c, doc.to<JsonObject>(), true);

  Config loaded;
  ConfigError err;
  TEST_ASSERT_TRUE(applyConfigJson(doc.as<JsonObjectConst>(), loaded, err, true));
  TEST_ASSERT_EQUAL_STRING(c.token, loaded.token);
  TEST_ASSERT_EQUAL_STRING("", loaded.pass);
  TEST_ASSERT_EQUAL_STRING(c.pendingUrl, loaded.pendingUrl);
}

static void test_default_name() {
  Config c;
  const uint8_t mac[6] = {0x5C, 0xCF, 0x7F, 0x12, 0xAB, 0x0C};
  applyDefaultName(c, mac);
  TEST_ASSERT_EQUAL_STRING("wallpanel-ab0c", c.name);
  applyDefaultName(c, mac);  // idempotent, does not overwrite
  TEST_ASSERT_EQUAL_STRING("wallpanel-ab0c", c.name);
}


// ------------------------------------------------------------------ boot mode

static void test_boot_mode_setup_only_on_a_fresh_start_with_switch_on() {
  // Power-on / RST with the CH340 switch ON (RX HIGH) -> SETUP.
  TEST_ASSERT_TRUE(decideBootMode(true, true, false) == BootMode::Setup);
  // CH340 OFF (RX LOW) -> RUN.
  TEST_ASSERT_TRUE(decideBootMode(true, false, false) == BootMode::Run);
}

static void test_boot_mode_switch_also_decides_on_deep_sleep_wakes() {
  // RST pressed during deep sleep is reported as a deep-sleep wake: the switch must still win.
  TEST_ASSERT_TRUE(decideBootMode(false, true, false) == BootMode::Setup);
  TEST_ASSERT_TRUE(decideBootMode(false, false, false) == BootMode::Run);
}

static void test_boot_mode_forced_run_build() {
  TEST_ASSERT_TRUE(decideBootMode(true, true, true) == BootMode::Run);
}

int main() {
  UNITY_BEGIN();
  RUN_TEST(test_geometry_constants);
  RUN_TEST(test_pack_byte_levels);
  RUN_TEST(test_parse_region_valid);
  RUN_TEST(test_parse_region_invalid);
  RUN_TEST(test_region_body_and_offsets);
  RUN_TEST(test_parse_uint);
  RUN_TEST(test_clamp_interval);
  RUN_TEST(test_backoff);
  RUN_TEST(test_plan_sleep);
  RUN_TEST(test_url_valid);
  RUN_TEST(test_battery_mv);
  RUN_TEST(test_error_names);
  RUN_TEST(test_ipv4);
  RUN_TEST(test_validate_ok_and_rules);
  RUN_TEST(test_apply_json_merges_and_keeps_secrets);
  RUN_TEST(test_apply_json_rejects_without_partial_change);
  RUN_TEST(test_apply_json_type_errors);
  RUN_TEST(test_to_json_masks_secrets);
  RUN_TEST(test_storage_round_trip);
  RUN_TEST(test_default_name);
  RUN_TEST(test_boot_mode_setup_only_on_a_fresh_start_with_switch_on);
  RUN_TEST(test_boot_mode_switch_also_decides_on_deep_sleep_wakes);
  RUN_TEST(test_boot_mode_forced_run_build);
  return UNITY_END();
}
