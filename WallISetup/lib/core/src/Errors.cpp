#include "Errors.h"

#include <string.h>

namespace walli {

namespace {
struct Entry {
  Err err;
  const char* name;
  const char* human;
};

constexpr Entry kErrors[] = {
    {Err::Ok, "ok", "OK"},
    {Err::NoConfig, "no_config", "Not configured"},
    {Err::WifiNotFound, "wifi_not_found", "Wi-Fi network not found"},
    {Err::WifiAuth, "wifi_auth", "Wi-Fi password rejected"},
    {Err::NoIp, "no_ip", "No IP address (DHCP / static IP)"},
    {Err::ServerUnreachable, "server_unreachable", "Server unreachable"},
    {Err::AuthRejected, "auth_rejected", "Token rejected by the server"},
    {Err::BadResponse, "bad_response", "Bad response from the server"},
    {Err::Display, "display", "Display not responding"},
};
}  // namespace

const char* errHuman(Err e) {
  for (const auto& entry : kErrors) {
    if (entry.err == e) return entry.human;
  }
  return "Unknown error";
}

const char* errName(Err e) {
  for (const auto& entry : kErrors) {
    if (entry.err == e) return entry.name;
  }
  return "bad_response";
}

bool errFromName(const char* name, Err& out) {
  if (name == nullptr) return false;
  for (const auto& entry : kErrors) {
    if (strcmp(entry.name, name) == 0) {
      out = entry.err;
      return true;
    }
  }
  return false;
}

}  // namespace walli
