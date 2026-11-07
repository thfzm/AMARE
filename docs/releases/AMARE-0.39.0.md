# AMARE 0.39.0 — Process / Composition Handle Acquisition Tracing

0.39.0 continues the handle forensics program from 0.38.0. It preserves the independent handle-forensics tool, dynamic object-type resolution, PID/target attribution, and full regression suite while documenting the remaining acquisition/cleanup limitations.

## Findings

- Process handles continue to accumulate against the same WebView2 browser PID during tab lifecycle stress.
- Composition handles continue to correlate with tab lifecycle.
- The exact creation API call stack, ETW attribution, idle baseline, 60-minute stability, and full-shutdown cleanup remain UNVERIFIED.
- No speculative resource-management change is applied.

## Scope

- No UI/feature rewrite.
- No forced `CloseHandle`, GC, WebView2 process termination, or speculative Dispose.
- Existing certificate, tear-off, cross-window, session, update, storage, and packaging regressions remain active.
