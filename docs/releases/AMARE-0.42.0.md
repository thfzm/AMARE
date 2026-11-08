# AMARE 0.42.0 — ETL Deep Analysis & Handle Ownership Investigation

0.42.0 analyzes the 0.41.0 ETW ETL and lifecycle snapshots. It is an investigation release, not a speculative resource-management change.

## Findings

- ETL generated: 606,780,794 bytes, SHA-256 recorded in the development report.
- tracerpt processed 25,734,983 events and produced CSV/summary.
- The collected providers include Process/Thread/Image/StackWalk/Win32k/COMRuntime; no handle create/close or object provider events were found in this ETL.
- Lifecycle shows Process/Composition handles remaining after tab close and after 5 minutes, while idle is stable.
- Exact creation/cleanup call stacks remain UNVERIFIED.

## Scope

- No UI/feature rewrite.
- No forced `CloseHandle`, GC, WebView2 process termination, or speculative Dispose.
- Existing certificate, tear-off, cross-window, session, update, storage, and packaging regressions remain active.
