# AMARE 0.38.0 — Handle Creation Path Tracing & Lifecycle Leak Verification

0.38.0 adds an independent handle-forensics tool and continues the Process/Composition ownership investigation from 0.37.0.

## Findings

- Process handles accumulate against the same WebView2 browser PID; Composition handles correlate with tab lifecycle.
- The independent tool records per-PID type counts, Process target PIDs/process start times, JSON, and CSV snapshots.
- ETW/stack attribution, idle baseline, and full shutdown cleanup remain UNVERIFIED unless actually collected.
- No root-cause fix is applied until the creation/cleanup path is confirmed.

## Scope

- No feature or UI rewrite.
- No forced `CloseHandle`, GC, WebView2 process termination, or speculative Dispose.
- Existing certificate, tear-off, cross-window, session, update, storage, and packaging regressions remain active.
