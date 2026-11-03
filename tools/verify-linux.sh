#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

export PATH="${HOME}/.cargo/bin:${PATH}"
mkdir -p artifacts/linux

cargo fmt --all -- --check 2>&1 | tee artifacts/linux/rust-fmt.log
cargo clippy --workspace --all-targets -- -D warnings 2>&1 | tee artifacts/linux/rust-clippy.log
cargo test --workspace 2>&1 | tee artifacts/linux/rust-tests.log
cargo test -p amare-security 2>&1 | tee artifacts/linux/security-tests.log
cargo test -p amare-services -- --test-threads=1 2>&1 | tee artifacts/linux/service-tests.log
cargo run -p amare-services --bin service-ci --release -- --output artifacts/linux/service-ci.json 2>&1 | tee artifacts/linux/service-ci.log
cargo run -p amare-services --bin service-benchmark --release -- --output artifacts/linux/service-benchmark.json 2>&1 | tee artifacts/linux/service-benchmark.log
cargo test -p amare-update 2>&1 | tee artifacts/linux/update-tests.log
cargo test -p amare-store -- --test-threads=1 2>&1 | tee artifacts/linux/storage-tests.log
cargo run -p amare-store --bin storage-ci --release -- --output artifacts/linux/storage-ci.json 2>&1 | tee artifacts/linux/storage-ci.log
cargo run -p amare-store --bin storage-benchmark --release -- --output artifacts/linux/storage-benchmark.json 2>&1 | tee artifacts/linux/storage-benchmark.log
cargo test -p amare-store --test backup -- --test-threads=1 2>&1 | tee artifacts/linux/backup-tests.log
cargo run -p amare-store --bin backup-ci --release -- --output artifacts/linux/backup-ci.json 2>&1 | tee artifacts/linux/backup-ci.log
cargo run -p amare-store --bin backup-benchmark --release -- --output artifacts/linux/backup-benchmark.json 2>&1 | tee artifacts/linux/backup-benchmark.log
cargo test -p amare-platform 2>&1 | tee artifacts/linux/key-protection-tests.log

cargo build -p amare-ffi 2>&1 | tee artifacts/linux/ffi-build.log
gcc -I crates/amare-ffi/include \
  crates/amare-ffi/tests/header_link_smoke.c \
  -L target/debug -lamare_ffi -Wl,-rpath,"$PWD/target/debug" \
  -o artifacts/linux/header_link_smoke 2>&1 | tee artifacts/linux/c-header.log
artifacts/linux/header_link_smoke 2>&1 | tee -a artifacts/linux/c-header.log

export LD_LIBRARY_PATH="${PWD}/target/debug:${LD_LIBRARY_PATH:-}"
AMARE_VERIFY_OUTPUT="${PWD}/artifacts/linux" \
  dotnet run --project apps/windows/AMARE.Interop.Tests/AMARE.Interop.Tests.csproj \
  2>&1 | tee artifacts/linux/csharp-interop.log
