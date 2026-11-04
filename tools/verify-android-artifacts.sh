#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEBUG_APK="${1:-$ROOT/apps/android/app/build/outputs/apk/debug/app-debug.apk}"
RELEASE_APK="${2:-$ROOT/apps/android/app/build/outputs/apk/release/app-release-unsigned.apk}"
RELEASE_AAB="${3:-$ROOT/apps/android/app/build/outputs/bundle/release/app-release.aab}"
BUILD_TOOLS="${ANDROID_BUILD_TOOLS:-${ANDROID_HOME:-/home/ubuntu/android-sdk}/build-tools/36.0.0}"
APKSIGNER="$BUILD_TOOLS/apksigner"
AAPT="$BUILD_TOOLS/aapt"
EXPECTED_PACKAGE="org.amare.browser"
EXPECTED_VERSION_NAME="0.11.0"

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

[ -s "$DEBUG_APK" ] || fail "debug APK missing: $DEBUG_APK"
[ -s "$RELEASE_APK" ] || fail "release APK missing: $RELEASE_APK"
[ -s "$RELEASE_AAB" ] || fail "release AAB missing: $RELEASE_AAB"

"$APKSIGNER" verify --verbose "$DEBUG_APK" >/dev/null \
  || fail "debug APK signature verification failed"

DEBUG_BADGING="$("$AAPT" dump badging "$DEBUG_APK")"
RELEASE_BADGING="$("$AAPT" dump badging "$RELEASE_APK")"
grep -q "package: name='$EXPECTED_PACKAGE' versionCode='11' versionName='$EXPECTED_VERSION_NAME'" \
  <<<"$DEBUG_BADGING" || fail "debug package/version mismatch"
grep -q "package: name='$EXPECTED_PACKAGE' versionCode='11' versionName='$EXPECTED_VERSION_NAME'" \
  <<<"$RELEASE_BADGING" || fail "release package/version mismatch"
grep -q "application-debuggable" <<<"$DEBUG_BADGING" \
  || fail "debug build is not marked debuggable"
if grep -q "application-debuggable" <<<"$RELEASE_BADGING"; then
  fail "release build is debuggable"
fi
grep -q "native-code: .*arm64-v8a" <<<"$RELEASE_BADGING" \
  || fail "release APK is missing arm64 native libraries"

if "$APKSIGNER" verify --verbose "$RELEASE_APK" >/dev/null 2>&1; then
  RELEASE_SIGNED=1
else
  RELEASE_SIGNED=0
fi

AAB_VERIFY="$(jarsigner -verify "$RELEASE_AAB" 2>&1 || true)"
if grep -qiE 'jar is unsigned|no manifest' <<<"$AAB_VERIFY"; then
  AAB_SIGNED=0
elif grep -qi 'jar verified' <<<"$AAB_VERIFY"; then
  AAB_SIGNED=1
else
  AAB_SIGNED=0
fi

if [ "${EXPECT_SIGNED_RELEASE:-0}" = "1" ]; then
  [ "$RELEASE_SIGNED" = "1" ] || fail "release APK is unsigned"
  [ "$AAB_SIGNED" = "1" ] || fail "release AAB is unsigned"
else
  if [ "$RELEASE_SIGNED" = "1" ] || [ "$AAB_SIGNED" = "1" ]; then
    echo "NOTE: release artifacts are signed; unsigned verification was not asserted."
  else
    echo "Release artifacts are unsigned as expected."
  fi
fi

echo "PASS: debug signature and package metadata verified"
echo "PASS: release package metadata and arm64 native libraries verified"
echo "release_apk_signed=$RELEASE_SIGNED"
echo "release_aab_signed=$AAB_SIGNED"
