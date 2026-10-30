# Changelog

## 0.8.0-beta.1 - First public beta

- Six-tab Windows and Linux hardware/system diagnostics interface.
- Real device IDs, driver diagnostics, process memory and selected-process controls.
- Live performance graphs, supported disk health readings and system specifications.
- JSON inventory snapshots and complete specification-report copying.
- Compact gray classic-style Windows interface with stationary wrapping tabs.
- Fixed Windows tab keyboard navigation and snapshot overwrites that could erase
  the previous export after an I/O failure.
- Fixed Linux comparison loads restoring cleared state or ignoring a newer file.
- Fixed Linux empty-inventory export and silently ignored concurrent export requests.
- Rejected malformed snapshot Unicode before it can fail during Linux UI rendering.
- MIT application license; upstream runtime/dependency notices retained.

Validation and remaining platform limitations are documented in the root README
and each edition's `VERIFICATION.md`.
