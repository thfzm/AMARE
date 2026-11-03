/* Compile-only header smoke test (Windows CI uses MSVC /c). */
#include "amare.h"

int amare_header_smoke(void) {
    const char *version = amare_core_version();
    AmareCore *core = 0;
    int code = amare_core_create(0, &core);
    if (code == AMARE_OK && core != 0) {
        amare_core_destroy(core);
    }
    AmareServices *services = 0;
    (void)amare_services_create(0, &services);
    if (services != 0) amare_services_destroy(services);
    return version != 0 && amare_services_abi_version() == AMARE_SERVICES_ABI_VERSION;
}
