# Retained layout-loop source fixtures

These three source inputs reproduce the original eight-iteration diagnostic
entry. `build-layout-loop.ps1` checks their fixed SHA-256 values before compiling.
It intentionally excludes the corresponding current fixtures from that special
build. The full 87-suite runner continues to use the current `tests/` sources.

Only source inputs were promoted from the local investigation. Dumps, original
executables, timing records and machine-specific manifests remain in ignored
`artifacts/`; they are not part of this directory or a public validation claim.
