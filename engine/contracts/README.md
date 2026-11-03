# Engine contracts

Language-neutral command/event/capability schemas shared by Rust, C#, and Kotlin.

These files are **contracts**, not an implementation. The Rust `EnginePort` and
the C#/Kotlin engine adapters must conform to the same versioned schema. Add a
contract version bump for any breaking change.

Phase 1 scaffold: schemas only. The WebView2 adapter is not implemented yet.
