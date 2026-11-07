# AMARE 0.40.0 — Handle Lifecycle Attribution & Leak Isolation

0.40.0 extends the independent handle-forensics tool with snapshot diff summaries, PID/start-time target attribution, and ETW provider availability probing. It preserves the full regression suite and avoids speculative resource-management changes.

## Findings

- Process handles continue to accumulate on the same WebView2 browser PID during tab lifecycle stress.
- Composition handles continue to correlate with tab lifecycle.
- ETW/WPR availability is probed, but creation stack/cleanup path remains UNVERIFIED unless actually collected.
- No forced `CloseHandle`, GC, WebView2 kill, or speculative Dispose is applied.

## Scope

- No feature or UI rewrite.
- Existing certificate, tear-off, cross-window, session, update, storage, and packaging regressions remain active.
- CurrentUser/WebView2 trusted CA and signed MSIX remain BLOCKED/UNVERIFIED.
