/*
 * AMARE core + service C ABI
 *
 * Version: 1 (core), service ABI version: 1
 * Conventions:
 *   - Return 0 (AMARE_OK) on success.
 *   - Strings written through `out` must be freed with amare_string_free().
 *   - Rust panics are caught and reported as AMARE_ERR_PANIC.
 */

#ifndef AMARE_FFI_H
#define AMARE_FFI_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define AMARE_OK 0
#define AMARE_ERR_INVALID_ARGUMENT 1
#define AMARE_ERR_PANIC 2
#define AMARE_ERR_INTERNAL 3
#define AMARE_ERR_NOT_FOUND 4
#define AMARE_ERR_PROFILE_SWITCHED 5
#define AMARE_ERR_NO_ACTIVE_PROFILE 6
#define AMARE_ERR_LAST_PROFILE 7
#define AMARE_ERR_SETTINGS 8
#define AMARE_ERR_SEARCH 9
#define AMARE_ERR_STORE 10
#define AMARE_ERR_JSON 11
#define AMARE_ERR_UNSUPPORTED 12
#define AMARE_SERVICES_ABI_VERSION 1

typedef struct AmareCore AmareCore;
typedef struct AmareServices AmareServices;

/* Static string; do not free. */
const char* amare_core_version(void);

int amare_core_create(const char* config_json, AmareCore** out_core);
void amare_core_destroy(AmareCore* core);

int amare_core_ping(AmareCore* core, const char* input, char** out_string);

/* Returns AMARE_OK even when no error is present; *out_string is then NULL. */
int amare_core_last_error(char** out_string);

void amare_string_free(char* value);

int64_t amare_core_add(int64_t a, int64_t b);

/*
 * Service API
 * -----------
 * Requests are UTF-8 JSON: {"version":1,"method":"profile.list","params":{...}}
 * Success writes a JSON result to out_json; release it with amare_string_free().
 * Errors return a non-zero code and write the message to amare_core_last_error().
 * AmareServices may be used from multiple threads, but must not be destroyed
 * while another call is in flight.
 */
uint32_t amare_services_abi_version(void);
int amare_services_create(const char* root_dir, AmareServices** out_services);
void amare_services_destroy(AmareServices* services);
int amare_services_call(AmareServices* services, const char* request_json, char** out_json);

#ifdef __cplusplus
}
#endif

#endif /* AMARE_FFI_H */
