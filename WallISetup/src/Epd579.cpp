#include "Epd579.h"

#include <Gray2.h>
#include <LittleFS.h>
#include <SPI.h>

#include "Log.h"
#include "epd579_lut.h"
#include "pins.h"

using namespace walli;

namespace {
constexpr uint32_t kSpiHz = 4000000;
constexpr uint32_t kBusyTimeoutMs = 15000;   // 4-grey refresh takes ~3.5-4 s
constexpr uint32_t kInitBusyTimeoutMs = 3000;
constexpr uint16_t kBusySettleMs = 20;       // Waveshare uses 200 ms; 20 ms saves energy, raise if flaky
constexpr uint8_t kSpiPins[] = {14, 13};     // SCK, MOSI

const SPISettings kSpi(kSpiHz, MSBFIRST, SPI_MODE0);

// ESP8266 SPI.writeBytes() reads its source as 32-bit words, so the pointer MUST be 4-byte
// aligned — an unaligned one (e.g. row + 49 for the slave half) raises Exception 9.
// Every SPI data write goes through this helper, which copies via an aligned bounce buffer.
void spiWriteAligned(const uint8_t* data, size_t len) {
  alignas(4) uint8_t chunk[64];  // writeBytes() moves at most 64 bytes per FIFO load anyway
  while (len > 0) {
    const size_t n = len < sizeof chunk ? len : sizeof chunk;
    memcpy(chunk, data, n);
    SPI.writeBytes(chunk, n);
    data += n;
    len -= n;
  }
}

// Idle state while the panel is unpowered. DC (GPIO0) is a boot strap pin that must read
// HIGH at the next deep-sleep wake, so it is released to the board's pull-up instead of
// being driven LOW. CS (GPIO15) LOW matches its boot strap requirement.
void allLinesLow() {
  for (uint8_t p : {PIN_EPD_CS, PIN_EPD_RST, kSpiPins[0], kSpiPins[1]}) {
    pinMode(p, OUTPUT);
    digitalWrite(p, LOW);
  }
  pinMode(PIN_EPD_DC, INPUT);
}
}  // namespace

void Epd579::powerOn() {
  pinMode(PIN_EPD_BUSY, INPUT);
  pinMode(PIN_EPD_PWR, OUTPUT);
  digitalWrite(PIN_EPD_PWR, HIGH);
  delay(10);
  pinMode(PIN_EPD_CS, OUTPUT);
  digitalWrite(PIN_EPD_CS, HIGH);
  pinMode(PIN_EPD_DC, OUTPUT);
  digitalWrite(PIN_EPD_DC, HIGH);
  pinMode(PIN_EPD_RST, OUTPUT);
  digitalWrite(PIN_EPD_RST, HIGH);
  SPI.begin();
}

void Epd579::powerOff() {
  SPI.end();
  allLinesLow();
  pinMode(PIN_EPD_PWR, OUTPUT);
  digitalWrite(PIN_EPD_PWR, LOW);
}

void Epd579::reset() {
  digitalWrite(PIN_EPD_RST, HIGH);
  delay(20);
  digitalWrite(PIN_EPD_RST, LOW);
  delay(2);
  digitalWrite(PIN_EPD_RST, HIGH);
  delay(20);
}

bool Epd579::waitIdle(uint32_t timeoutMs) {
  const uint32_t start = millis();
  while (digitalRead(PIN_EPD_BUSY) == HIGH) {  // HIGH = busy
    if (millis() - start > timeoutMs) {
      LOGF("EPD busy timeout after %lu ms\n", static_cast<unsigned long>(timeoutMs));
      return false;
    }
    delay(10);
  }
  delay(kBusySettleMs);
  return true;
}

void Epd579::command(uint8_t cmd) {
  SPI.beginTransaction(kSpi);
  digitalWrite(PIN_EPD_DC, LOW);
  digitalWrite(PIN_EPD_CS, LOW);
  SPI.transfer(cmd);
  digitalWrite(PIN_EPD_CS, HIGH);
  SPI.endTransaction();
}

void Epd579::data(uint8_t value) { data(&value, 1); }

void Epd579::data(const uint8_t* buf, size_t len) {
  SPI.beginTransaction(kSpi);
  digitalWrite(PIN_EPD_DC, HIGH);
  digitalWrite(PIN_EPD_CS, LOW);
  spiWriteAligned(buf, len);
  digitalWrite(PIN_EPD_CS, HIGH);
  SPI.endTransaction();
}

void Epd579::commandWithData(uint8_t cmd, std::initializer_list<uint8_t> bytes) {
  command(cmd);
  for (uint8_t b : bytes) data(b);
}

bool Epd579::init4Gray() {
  reset();
  if (!waitIdle(kInitBusyTimeoutMs)) return false;
  command(0x12);  // software reset / power on
  if (!waitIdle(kInitBusyTimeoutMs)) return false;

  commandWithData(0x0C, {0x8B, 0x9C, 0xA6, 0x0F});  // booster soft start
  commandWithData(0x3C, {0x81});                    // border waveform
  if (!waitIdle(kInitBusyTimeoutMs)) return false;

  // Master controller (left half): X increment, Y decrement; RAM window 400 x 272.
  commandWithData(0x11, {0x01});
  commandWithData(0x44, {0x00, 0x31});
  commandWithData(0x45, {0x0F, 0x01, 0x00, 0x00});
  commandWithData(0x4E, {0x00});
  commandWithData(0x4F, {0x0F, 0x01});
  if (!waitIdle(kInitBusyTimeoutMs)) return false;

  // Slave controller (right half, mirrored X).
  commandWithData(0x91, {0x00});
  commandWithData(0xC4, {0x31, 0x00});
  commandWithData(0xC5, {0x0F, 0x01, 0x00, 0x00});
  commandWithData(0xCE, {0x31});
  commandWithData(0xCF, {0x0F, 0x01});

  // 4-grey waveform.
  command(0x32);
  data(EPD579_LUT_4GRAY, 227);
  commandWithData(0x3F, {EPD579_LUT_4GRAY[227]});
  commandWithData(0x03, {EPD579_LUT_4GRAY[228]});
  commandWithData(0x04, {EPD579_LUT_4GRAY[229], EPD579_LUT_4GRAY[230], EPD579_LUT_4GRAY[231]});
  commandWithData(0x2C, {EPD579_LUT_4GRAY[232]});
  return true;
}

bool Epd579::writeImage(File& f) {
  struct Pass {
    uint8_t cmd;
    uint8_t plane;
    uint8_t firstByte;
  };
  // Plane 0 (bit 0) -> 0x24/0xA4, plane 1 (bit 1) -> 0x26/0xA6 (docs/protocol.md §1).
  static const Pass kPasses[] = {
      {0x24, 0, 0},
      {0x26, 1, 0},
      {0xA4, 0, gray2::kSlaveFirstByte},
      {0xA6, 1, gray2::kSlaveFirstByte},
  };
  uint8_t row[gray2::kRowBytes];
  for (const Pass& pass : kPasses) {
    command(pass.cmd);
    SPI.beginTransaction(kSpi);
    digitalWrite(PIN_EPD_DC, HIGH);
    digitalWrite(PIN_EPD_CS, LOW);
    bool ok = true;
    for (uint16_t y = 0; y < gray2::kHeight && ok; ++y) {
      ok = f.seek(gray2::rowOffset(pass.plane, y), SeekSet) && f.read(row, sizeof row) == sizeof row;
      if (ok) spiWriteAligned(row + pass.firstByte, gray2::kHalfBytes);  // +49 is unaligned
      if ((y & 0x1F) == 0) yield();
    }
    digitalWrite(PIN_EPD_CS, HIGH);
    SPI.endTransaction();
    if (!ok) {
      LOGF("EPD: image file read failed\n");
      return false;
    }
  }
  return true;
}

bool Epd579::refresh() {
  commandWithData(0x22, {0xCF});  // display update control: 4-grey
  command(0x20);                  // master activation
  delay(1);                       // >= 200 us before polling BUSY
  return waitIdle(kBusyTimeoutMs);
}

void Epd579::sleep() {
  commandWithData(0x10, {0x01});  // deep sleep mode 1
  delay(5);
}

bool Epd579::showFile(const char* path) {
  File f = LittleFS.open(path, "r");
  if (!f || f.size() != gray2::kImageBytes) {
    LOGF("EPD: %s missing or wrong size\n", path);
    return false;
  }
  [[maybe_unused]] const uint32_t t0 = millis();
  powerOn();
  bool ok = init4Gray() && writeImage(f) && refresh();
  f.close();
  if (ok) sleep();
  powerOff();
  LOGF("EPD: show %s %s in %lu ms\n", path, ok ? "ok" : "FAILED", static_cast<unsigned long>(millis() - t0));
  return ok;
}
