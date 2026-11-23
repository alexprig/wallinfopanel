#pragma once
// gray2 image geometry and region rules — docs/protocol.md §1.

#include <stddef.h>
#include <stdint.h>

namespace walli {
namespace gray2 {

constexpr uint16_t kWidth = 792;
constexpr uint16_t kHeight = 272;
constexpr uint16_t kRowBytes = kWidth / 8;                    // 99
constexpr uint32_t kPlaneBytes = uint32_t(kRowBytes) * kHeight; // 26,928
constexpr uint32_t kImageBytes = 2 * kPlaneBytes;              // 53,856

// Dual controller split (Waveshare reference driver): each half is 50 bytes (400 px) wide.
// Master half = row bytes [0, 50), slave half = row bytes [49, 99) — byte 49 is sent to both.
constexpr uint16_t kHalfBytes = 50;
constexpr uint16_t kSlaveFirstByte = 49;

// Pixel levels.
constexpr uint8_t kBlack = 0;
constexpr uint8_t kDarkGrey = 1;
constexpr uint8_t kLightGrey = 2;
constexpr uint8_t kWhite = 3;

struct Region {
  uint16_t x = 0;
  uint16_t y = 0;
  uint16_t w = 0;
  uint16_t h = 0;
};

// Validate a region against the protocol rules (8-px grid, inside the image, non-empty).
bool regionValid(const Region& r);

// Parse "x,y,w,h" (optional spaces around numbers). Returns false if malformed or invalid.
bool parseRegion(const char* text, Region& out);

// Body length of a region response: 2 * h * w/8.
uint32_t regionBodyBytes(const Region& r);

// Offset in the full image file of region row `row` (0..h-1) in plane `plane` (0/1).
uint32_t regionRowOffset(const Region& r, uint8_t plane, uint16_t row);

// Offset in the full image file of image row `row` in plane `plane`.
inline uint32_t rowOffset(uint8_t plane, uint16_t row) {
  return plane * kPlaneBytes + uint32_t(row) * kRowBytes;
}

// Pack 8 pixel levels (leftmost first) into the two plane bytes (MSB = leftmost pixel).
void packByte(const uint8_t levels[8], uint8_t& plane0, uint8_t& plane1);

}  // namespace gray2
}  // namespace walli
