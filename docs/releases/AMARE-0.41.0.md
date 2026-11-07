# AMARE 0.41.0 — ETW Trace Capture & Automated Handle Lifecycle

0.41.0 adds an ETW capture workflow and an optional A–J handle lifecycle experiment. It preserves the existing handle-forensics tool and regression suite.

## Findings

- ETW providers and WPR are available on the Windows runner.
- Lifecycle snapshots identify Process/Composition changes by tab stage and idle waits.
- ETL/stack analysis remains UNVERIFIED unless the trace and analysis actually complete.
- No speculative resource-management change is applied.

## Scope

- ETW/lifecycle diagnostics are opt-in and do not affect normal browser execution.
- Existing certificate, tear-off, cross-window, session, update, storage, and packaging regressions remain active.
