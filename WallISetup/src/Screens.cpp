#include "Screens.h"

#include <Adafruit_GFX.h>
#include <Fonts/FreeSans12pt7b.h>
#include <Fonts/FreeSansBold18pt7b.h>
#include <Gray2.h>
#include <LittleFS.h>

#include "Log.h"
#include "Storage.h"

using namespace walli;

namespace screens {

namespace {
constexpr uint16_t kStripRows = 16;
constexpr int16_t kMargin = 24;
constexpr int16_t kTitleBaseline = 52;
constexpr int16_t kFirstLineBaseline = 96;
constexpr int16_t kLineStep = 34;
constexpr uint16_t kBarsTop = 200;
constexpr uint16_t kBarsBottom = 256;

// Text and frame, drawn with a vertical offset so the same code fills every strip.
void drawText(Adafruit_GFX& g, int16_t yOff, const TextScreen& s) {
  g.fillScreen(1);  // 1 = background (no ink)
  g.drawRect(4, 4 + yOff, gray2::kWidth - 8, gray2::kHeight - 8, 0);
  g.setTextColor(0);
  g.setTextWrap(false);
  g.setFont(&FreeSansBold18pt7b);
  g.setCursor(kMargin, kTitleBaseline + yOff);
  g.print(s.title);
  g.setFont(&FreeSans12pt7b);
  const uint8_t maxLines = s.greyBars ? 3 : 5;
  for (uint8_t i = 0; i < maxLines; ++i) {
    if (s.lines[i].length() == 0) continue;
    g.setCursor(kMargin, kFirstLineBaseline + i * kLineStep + yOff);
    g.print(s.lines[i]);
  }
}

uint8_t backgroundLevel(const TextScreen& s, uint16_t x, uint16_t y) {
  if (s.greyBars && y >= kBarsTop && y < kBarsBottom && x >= kMargin && x < gray2::kWidth - kMargin) {
    const uint16_t barWidth = (gray2::kWidth - 2 * kMargin) / 4;
    const uint16_t idx = (x - kMargin) / barWidth;
    return uint8_t(idx > 3 ? 3 : idx);  // black, dark grey, light grey, white
  }
  return gray2::kWhite;
}
}  // namespace

bool write(const TextScreen& screen) {
  GFXcanvas1 canvas(gray2::kWidth, kStripRows);
  if (canvas.getBuffer() == nullptr) return false;

  File f = LittleFS.open(storage::kImageTmp, "w");
  if (!f) return false;

  uint8_t row[gray2::kRowBytes];
  uint8_t levels[8];
  bool ok = true;
  // Plane-major file layout: render all strips once per plane (text is drawn twice — cheap).
  for (uint8_t plane = 0; plane < 2 && ok; ++plane) {
    for (uint16_t y0 = 0; y0 < gray2::kHeight && ok; y0 += kStripRows) {
      drawText(canvas, -int16_t(y0), screen);
      for (uint16_t dy = 0; dy < kStripRows && y0 + dy < gray2::kHeight && ok; ++dy) {
        const uint16_t y = y0 + dy;
        const uint8_t* ink = canvas.getBuffer() + dy * gray2::kRowBytes;  // 1 = background, MSB first
        const bool barsRow = screen.greyBars && y >= kBarsTop && y < kBarsBottom;
        for (uint16_t bx = 0; bx < gray2::kRowBytes; ++bx) {
          if (!barsRow) {
            row[bx] = ink[bx];  // white background (3 = 0b11) / black ink (0): both planes equal
            continue;
          }
          for (uint8_t i = 0; i < 8; ++i) {
            const uint16_t x = bx * 8 + i;
            levels[i] = (ink[bx] & (0x80 >> i)) ? backgroundLevel(screen, x, y) : gray2::kBlack;
          }
          uint8_t p0, p1;
          gray2::packByte(levels, p0, p1);
          row[bx] = plane == 0 ? p0 : p1;
        }
        ok = f.write(row, sizeof row) == sizeof row;
      }
      yield();
    }
  }
  const size_t size = f.size();
  f.close();
  if (!ok || size != gray2::kImageBytes) {
    LOGF("screens: write failed (%u bytes)\n", static_cast<unsigned>(size));
    return false;
  }
  storage::invalidateImage();
  return LittleFS.rename(storage::kImageTmp, storage::kImagePath);
}

}  // namespace screens
