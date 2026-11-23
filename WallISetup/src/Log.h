#pragma once
// Debug logging — only in the nodemcuv2_debug build (Serial must stay off in RUN mode).

#include <Arduino.h>

#if WALLI_DEBUG
#define LOGF(...) Serial.printf(__VA_ARGS__)
#else
#define LOGF(...) \
  do {            \
  } while (0)
#endif
