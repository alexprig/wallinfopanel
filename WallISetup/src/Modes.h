#pragma once

namespace runmode {
// Never returns: ends in deep sleep.
[[noreturn]] void run(bool bootInit);
}

namespace setupmode {
void begin();
void loop();
}
