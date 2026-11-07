# AMARE 0.37.0 — Composition / Process Handle Ownership & Leak Root Cause

0.37.0 continues the handle forensics from 0.36.0. It identifies the target processes referenced by the growing Process handles and preserves the full regression suite without speculative resource-management changes.

## Findings

- type31 is dynamically resolved as **Composition** and type8 as **Process**.
- Process handles are attributed to target PIDs/process names via safe handle duplication; Composition target identity remains limited by available Windows APIs.
- No root-cause fix is applied until the creation/cleanup path is confirmed.

## Scope

- No UI or feature rewrite.
- No forced `CloseHandle`, GC, or WebView2 process termination.
- Tear-off, certificate UX, session, update, storage, and packaging regressions remain active.
