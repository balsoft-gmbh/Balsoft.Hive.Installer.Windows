# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [2.0.0] - 2026-10-11

### Removed
- Taking over an installation made by another installer. Setup now only updates installations it
  made itself; `ProductDefinition` loses the property that switched this on.

## [1.1.0] - 2026-10-10

### Added
- Version check: Setup compares its version with the installed one. A newer installed version is
  never replaced by accident: Setup stops with exit code 7 and changes nothing, in the wizard and
  in silent runs. `/ALLOWDOWNGRADE` overrides it for support cases.
- The same version again is offered as a repair (wizard title and button say "Repair").
- Both versions are written to the Setup log.

## [1.0.0] - 2026-10-10

First release: a WPF installer that Balsoft's Windows products share.

### Added
- `InstallerApp.Run(ProductDefinition, args)`: complete Setup in one call; wizard, `/SILENT` and
  `/VERYSILENT`, install, upgrade and uninstall, documented exit codes.
- `ProductDefinition`, `ServiceDefinition`, `ShortcutDefinition`, `InstallerBranding`.
- Upgrades with backup and rollback (files restored, service restarted) when a step fails.
- Windows service handling: account check against Windows before install, stop and wait for
  the process before files are replaced, registration through the product's own executable.
- Reboot renames an earlier, interrupted update left for the install folder are dropped
  (`PendingFileRenameOperations` read and written as raw REG_MULTI_SZ).
- Build targets that pack `InstallerPayloadDirectory` (tar + Brotli) and embed it in Setup.
