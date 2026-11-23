#include "Gray2.h"

namespace walli {
namespace gray2 {

bool regionValid(const Region& r) {
  if (r.w < 8 || r.h < 1) return false;
  if (r.x % 8 != 0 || r.w % 8 != 0) return false;
  if (uint32_t(r.x) + r.w > kWidth) return false;
  if (uint32_t(r.y) + r.h > kHeight) return false;
  return true;
}

namespace {
// Parses one unsigned number with optional surrounding spaces; advances `p`.
bool parseNumber(const char*& p, uint32_t& out) {
  while (*p == ' ') ++p;
  if (*p < '0' || *p > '9') return false;
  uint32_t v = 0;
  int digits = 0;
  while (*p >= '0' && *p <= '9') {
    v = v * 10 + uint32_t(*p - '0');
    if (++digits > 5) return false;  // nothing in a region exceeds 5 digits
    ++p;
  }
  while (*p == ' ') ++p;
  out = v;
  return true;
}
}  // namespace

bool parseRegion(const char* text, Region& out) {
  if (text == nullptr) return false;
  const char* p = text;
  uint32_t v[4];
  for (int i = 0; i < 4; ++i) {
    if (!parseNumber(p, v[i])) return false;
    if (i < 3) {
      if (*p != ',') return false;
      ++p;
    }
  }
  if (*p != '\0') return false;
  for (uint32_t n : v) {
    if (n > 0xFFFF) return false;
  }
  Region r;
  r.x = uint16_t(v[0]);
  r.y = uint16_t(v[1]);
  r.w = uint16_t(v[2]);
  r.h = uint16_t(v[3]);
  if (!regionValid(r)) return false;
  out = r;
  return true;
}

uint32_t regionBodyBytes(const Region& r) {
  return 2u * uint32_t(r.h) * (r.w / 8u);
}

uint32_t regionRowOffset(const Region& r, uint8_t plane, uint16_t row) {
  return rowOffset(plane, uint16_t(r.y + row)) + r.x / 8u;
}

void packByte(const uint8_t levels[8], uint8_t& plane0, uint8_t& plane1) {
  uint8_t p0 = 0;
  uint8_t p1 = 0;
  for (int i = 0; i < 8; ++i) {
    const uint8_t mask = uint8_t(0x80u >> i);
    if (levels[i] & 0x01) p0 |= mask;
    if (levels[i] & 0x02) p1 |= mask;
  }
  plane0 = p0;
  plane1 = p1;
}

}  // namespace gray2
}  // namespace walli
