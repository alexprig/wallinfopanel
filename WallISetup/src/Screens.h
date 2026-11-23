#pragma once
// On-device screens, rendered into the gray2 image file in 16-row strips (no frame buffer).

#include <Arduino.h>

namespace screens {

struct TextScreen {
  String title;
  String lines[5];
  bool greyBars = false;  // draw 4 grey level bars (test pattern)
};

// Renders the screen into storage::kImagePath (via a temp file) and forgets the stored ETag,
// so the next update fetches a full image from the server.
bool write(const TextScreen& screen);

}  // namespace screens
