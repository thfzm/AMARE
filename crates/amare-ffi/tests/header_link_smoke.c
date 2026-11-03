/* Link/run smoke test for Linux CI. */
#include <stdio.h>
#include "amare.h"

int main(void) {
    AmareCore *core = 0;
    if (amare_core_create(0, &core) != AMARE_OK || core == 0) return 1;
    char *out = 0;
    if (amare_core_ping(core, "c-link", &out) != AMARE_OK || out == 0) return 2;
    printf("version=%s\nping=%s\n", amare_core_version(), out);
    amare_string_free(out);
    amare_core_destroy(core);
    if (amare_services_abi_version() != AMARE_SERVICES_ABI_VERSION) return 3;
    return 0;
}
