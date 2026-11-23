#pragma once
// Which mode the firmware starts in — docs/firmware-design.md §2.

namespace walli {

enum class BootMode { Setup, Run };

// bootInit:    power-on / RST (true) vs deep-sleep wake (false). Only selects init run vs
//              silent cycle, not the mode.
// setupSwitch: the CH340 power switch is ON (RX/GPIO3 reads HIGH)
// forceRun:    bench build that never enters setup mode
//
// The switch decides on every boot, deep-sleep wakes included: the ESP8266 reports an RST press
// during deep sleep as a deep-sleep wake (REASON_DEEP_SLEEP_AWAKE), so the reset reason can't
// tell a manual RST from the timer. Turning the switch ON while the panel sleeps therefore
// enters setup at the next wake as well.
inline BootMode decideBootMode(bool bootInit, bool setupSwitch, bool forceRun) {
  (void)bootInit;
  if (forceRun) return BootMode::Run;
  return setupSwitch ? BootMode::Setup : BootMode::Run;
}

}  // namespace walli
