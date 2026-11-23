#pragma once
// Waveshare 5.79" e-Paper (792x272, dual controller) — 4-grey driver.
// Command sequences ported from Waveshare's reference driver EPD_5in79.c (V1.0, 2024-03-05).

#include <Arduino.h>
#include <FS.h>

class Epd579 {
 public:
  // Power the module (PWR HIGH), start SPI. Lines are held LOW while unpowered.
  void powerOn();
  // Panel deep sleep must be sent before. Cuts power and drives all signal lines LOW.
  void powerOff();

  bool init4Gray();                 // reset + 4-grey registers + LUT
  bool writeImage(File& gray2File); // 4 passes: 0x24/0x26 master, 0xA4/0xA6 slave
  bool refresh();                   // 4-grey display update, waits for BUSY
  void sleep();                     // controller deep sleep (no high voltage left on the panel)

  // Complete sequence: power on → init → write → refresh → sleep → power off.
  bool showFile(const char* path);

 private:
  void reset();
  bool waitIdle(uint32_t timeoutMs);
  void command(uint8_t cmd);
  void data(uint8_t value);
  void data(const uint8_t* buf, size_t len);
  void commandWithData(uint8_t cmd, std::initializer_list<uint8_t> bytes);
};
