#!/usr/bin/env bash
set -euo pipefail

APK="${1:-artifacts/android/AMARE-0.11.0-debug-arm64.apk}"
TEST_APK="${2:-artifacts/android/AMARE-0.11.0-debug-androidTest.apk}"
ADB="${ADB:-${ANDROID_HOME:-/home/ubuntu/android-sdk}/platform-tools/adb}"

if [ ! -x "$ADB" ]; then
  echo "UNVERIFIED: adb not found at $ADB" >&2
  exit 2
fi
if [ ! -s "$APK" ] || [ ! -s "$TEST_APK" ]; then
  echo "FAIL: APK or test APK missing" >&2
  exit 1
fi

DEVICES="$("$ADB" devices | awk 'NR>1 && $2 == "device" {print $1}')"
if [ -z "$DEVICES" ]; then
  echo "UNVERIFIED: no connected Android device/emulator" >&2
  "$ADB" devices -l >&2 || true
  exit 2
fi

echo "Device(s): $DEVICES"
"$ADB" install --no-streaming -r "$APK"
"$ADB" install --no-streaming -r "$TEST_APK"

OUT="${TMPDIR:-/tmp}/amare-instrumentation-$(date +%Y%m%d-%H%M%S).txt"
set +e
"$ADB" shell am instrument -w -r \
  org.amare.browser.test/androidx.test.runner.AndroidJUnitRunner | tee "$OUT"
STATUS=${PIPESTATUS[0]}
set -e

echo "Result log: $OUT"
if [ "$STATUS" -ne 0 ] || ! grep -q "OK (" "$OUT"; then
  echo "FAIL: instrumentation tests did not pass" >&2
  exit 1
fi
echo "PASS: instrumentation tests completed on connected device"
