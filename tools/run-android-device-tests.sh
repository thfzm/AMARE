#!/usr/bin/env bash
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APK="${1:-$ROOT/artifacts/android/AMARE-0.12.0-debug-arm64.apk}"
TEST_APK="${2:-$ROOT/artifacts/android/AMARE-0.12.0-debug-androidTest.apk}"
ADB="${ADB:-${ANDROID_HOME:-/home/ubuntu/android-sdk}/platform-tools/adb}"
BUILD_TOOLS="${ANDROID_BUILD_TOOLS:-${ANDROID_HOME:-/home/ubuntu/android-sdk}/build-tools/36.0.0}"
OUT_DIR="${AMARE_DEVICE_RESULT_DIR:-$ROOT/artifacts/android/device-test/results}"
STAMP="$(date +%Y%m%d-%H%M%S)"
REPORT="$OUT_DIR/device-test-$STAMP.md"
INSTRUMENTATION="$OUT_DIR/instrumentation-$STAMP.txt"
LOGCAT="$OUT_DIR/logcat-$STAMP.txt"
mkdir -p "$OUT_DIR"

write_unverified() {
  local reason="$1"
  cat > "$REPORT" <<REPORT
# AMARE Android device test result

- Result: **UNVERIFIED**
- Reason: $reason
- Time: $(date -Is)
- APK: \`$APK\`
- Test APK: \`$TEST_APK\`
- ADB: \`$ADB\`

## Device
\`\`\`
$("$ADB" devices -l 2>&1 || true)
\`\`\`

## Required user action
1. Connect an Android device with USB debugging enabled, or start a KVM-backed emulator.
2. Confirm the device appears as \`device\` in \`adb devices -l\`.
3. Run this script again. Do not uninstall AMARE unless you intentionally want to remove app data.

Instrumentation tests use isolated database names and separate test Keystore alias;
they do not delete the normal app database or password store.
REPORT
  echo "UNVERIFIED: $reason"
  echo "Report: $REPORT"
  exit 2
}

[ -x "$ADB" ] || write_unverified "adb not found at $ADB"
[ -s "$APK" ] || write_unverified "debug APK missing: $APK"
[ -s "$TEST_APK" ] || write_unverified "instrumentation APK missing: $TEST_APK"

DEVICE_LINES="$("$ADB" devices | awk 'NR>1 && $2 == "device" {print $1}')"
if [ -z "$DEVICE_LINES" ]; then
  write_unverified "no connected Android device or emulator"
fi
if [ "$(printf '%s\n' "$DEVICE_LINES" | wc -l)" -gt 1 ] && [ -z "${ANDROID_SERIAL:-}" ]; then
  write_unverified "multiple devices connected; set ANDROID_SERIAL"
fi
SERIAL="${ANDROID_SERIAL:-$(printf '%s\n' "$DEVICE_LINES" | head -1)}"
ADB_DEVICE=("$ADB" -s "$SERIAL")

MODEL="$("${ADB_DEVICE[@]}" shell getprop ro.product.model | tr -d '\r')"
MANUFACTURER="$("${ADB_DEVICE[@]}" shell getprop ro.product.manufacturer | tr -d '\r')"
ANDROID_RELEASE="$("${ADB_DEVICE[@]}" shell getprop ro.build.version.release | tr -d '\r')"
SDK="$("${ADB_DEVICE[@]}" shell getprop ro.build.version.sdk | tr -d '\r')"
ABI="$("${ADB_DEVICE[@]}" shell getprop ro.product.cpu.abi | tr -d '\r')"
RAM="$("${ADB_DEVICE[@]}" shell cat /proc/meminfo 2>/dev/null | awk '/MemTotal/ {print $2 " kB"}' || true)"
AAPT="$BUILD_TOOLS/aapt"
APKSIGNER="$BUILD_TOOLS/apksigner"
APK_BADGING="$("$AAPT" dump badging "$APK" 2>/dev/null || true)"
APK_PACKAGE="$(printf '%s\n' "$APK_BADGING" | sed -n "s/^package: name='\([^']*\)'.*/\1/p")"
APK_VERSION="$(printf '%s\n' "$APK_BADGING" | sed -n "s/^package:.*versionName='\([^']*\)'.*/\1/p")"
APK_CODE="$(printf '%s\n' "$APK_BADGING" | sed -n "s/^package:.*versionCode='\([^']*\)'.*/\1/p")"

INSTALL_OUTPUT="$("${ADB_DEVICE[@]}" install --no-streaming -r "$APK" 2>&1)"
INSTALL_STATUS=$?
INSTALL_TEST_OUTPUT="$("${ADB_DEVICE[@]}" install --no-streaming -r "$TEST_APK" 2>&1)"
TEST_INSTALL_STATUS=$?

"${ADB_DEVICE[@]}" logcat -c >/dev/null 2>&1 || true
set +e
"${ADB_DEVICE[@]}" shell am instrument -w -r \
  org.amare.browser.test/androidx.test.runner.AndroidJUnitRunner > "$INSTRUMENTATION" 2>&1
INSTRUMENTATION_STATUS=$?
set -e
"${ADB_DEVICE[@]}" logcat -d -v time -s AndroidRuntime:E BrowserViewModel:V PasswordRepository:V BrowserRepository:V > "$LOGCAT" 2>&1 || true

RESULT="UNVERIFIED"
if [ "$INSTALL_STATUS" -ne 0 ] || [ "$TEST_INSTALL_STATUS" -ne 0 ]; then
  RESULT="FAIL"
elif [ "$INSTRUMENTATION_STATUS" -eq 0 ] && grep -q "OK (" "$INSTRUMENTATION"; then
  RESULT="PASS"
else
  RESULT="FAIL"
fi

cat > "$REPORT" <<REPORT
# AMARE Android device test result

- Result: **$RESULT**
- Time: $(date -Is)
- Serial: $SERIAL
- Model: $MANUFACTURER $MODEL
- Android: $ANDROID_RELEASE (SDK $SDK)
- ABI: $ABI
- RAM: $RAM
- APK: \`$APK\`
- APK package/version: $APK_PACKAGE / $APK_VERSION ($APK_CODE)
- APK SHA-256: $(sha256sum "$APK" | awk '{print $1}')
- Test APK: \`$TEST_APK\`
- Test APK SHA-256: $(sha256sum "$TEST_APK" | awk '{print $1}')

## Install

\`-r\` was used to preserve existing app data. The test suite uses isolated test databases
and a separate password test Keystore alias; it does not delete normal app data.

### Debug APK
\`\`\`
$INSTALL_OUTPUT
\`\`\`

### Instrumentation APK
\`\`\`
$INSTALL_TEST_OUTPUT
\`\`\`

## Instrumentation

Exit code: $INSTRUMENTATION_STATUS

\`\`\`
$(cat "$INSTRUMENTATION")
\`\`\`

## Filtered logcat

Only AndroidRuntime:E and AMARE repository/view-model tags are captured.
Full GeckoView logs may contain URLs and should be filtered before sharing.

Log file: \`$LOGCAT\`

## Interpretation

- PASS: install and instrumentation completed with OK
- FAIL: install/instrumentation failed; inspect output above
- UNVERIFIED: no device or missing test artifact
REPORT

echo "Result: $RESULT"
echo "Report: $REPORT"
echo "Instrumentation output: $INSTRUMENTATION"
[ "$RESULT" = "PASS" ] && exit 0
exit 1
