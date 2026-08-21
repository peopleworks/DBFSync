# Changelog

All notable changes to PeopleWorks DBFSync are recorded here.
This project follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **The documented CLI option surface is now tested.** Options are declared once, in the
  `CliArguments.EnsureOnly(...)` call of each command, and repeated in `README.md`,
  `README.en.md` and the pocket guide. `DocumentationSyncTests` reads all four back and
  compares them in **both** directions, so an option added without documentation, or a
  documented option the CLI no longer accepts, breaks the build instead of shipping.
  `EveryCommandSurfaceIsCoveredByThisTest` resolves each `EnsureOnly` call back to its
  enclosing method, so a new command that starts accepting options cannot slip past.
- **Releases are automated from a version tag.** Pushing `vMAJOR.MINOR.PATCH` builds,
  verifies and publishes the release. Two checks the manual process relied on a human
  for are now enforced: the tag must agree with `<Version>` in `DBFSync.csproj`, and the
  published `dbfsync.exe` is asserted to be a 32-bit PE — an x64 apphost builds and
  publishes cleanly and only fails on a user machine, where the x86 Visual FoxPro ODBC
  driver cannot load. A manual run replaces an existing release for its tag; a tag push
  refuses to.
- `CONTRIBUTING.md`, `SECURITY.md`, `CHANGELOG.md`, `.editorconfig` and `.gitattributes`,
  aligning DBFSync with the conventions already used by
  [SQLDiff](https://github.com/peopleworks/SqlSchemaDiff).

### Fixed

- `DocumentationSyncTests` used a namespace that did not match the rest of the suite.

## [1.0.0] - 2026-08-21

First public release.

### Added

- **Visual FoxPro DBF migration and synchronization** to PostgreSQL, SQL Server and
  SQLite over the 32-bit Microsoft Visual FoxPro ODBC driver.
- **Identity and change detection.** `RECNO()` preserves the physical identity of each
  DBF record, and a per-row `SHA-256` decides whether it changed. Rows land in a
  temporary staging table; each table is then reconciled inside its own transaction —
  `INSERT` for a new `RECNO`, `UPDATE` when the hash differs, `DELETE` when the record is
  gone or flagged as deleted, `ROLLBACK` on error.
- **Normalization before hashing**, so a value that did not change does not look changed:
  the driver opens with `Deleted=Yes`, empty FoxPro dates (`1899-12-30`) become `NULL`,
  and text is trimmed.
- **Flexible selection** by exact name, comma-separated list, `*` and `?` wildcards,
  `--all`, exclusions, and repeatable `--table`. `--all` and inclusion patterns are
  mutually exclusive.
- **Controlled schema evolution.** `--sync-schema` adds fields and widens safe types;
  destructive changes are blocked unless authorized with `--allow-drop-columns`, and
  `migrate --recreate` rebuilds a table explicitly. Without `--sync-schema`, a structural
  difference stops that table instead of guessing.
- **Secure profiles.** Named connection profiles with DPAPI-protected passwords scoped to
  the user or the machine, encrypted transport by default, and no password ever accepted
  as a command-line argument — the prompt does not echo, and `--password-stdin` covers
  unattended use.
- **Unattended execution.** Per-run UTF-8 logs under
  `%ProgramData%\PeopleWorks\DBFSync\logs\`, concurrent-run locking, and exit codes `0`
  (success), `1` (invalid arguments, connection or transfer error) and `130` (Ctrl+C).
- **Bilingual CLI** in English and Spanish through extensible `.resx` resources, selected
  with `--lang`, `--language` or `DBFSYNC_LANG`, plus Spanish command aliases such as
  `perfil`, `migrar`, `sincronizar` and `inspeccionar`.
- **Spectre.Console interface** with the PeopleWorks banner, colored tables and
  connection context that never reveals a secret.
- **Windows CI** that builds, tests, audits dependencies and publishes the
  framework-dependent `win-x86` artifact on every push.
- **Bilingual documentation** with section parity between `README.md` and `README.en.md`,
  a custom architecture diagram, and a
  [pocket guide](https://peopleworks.github.io/DBFSync/) carrying every command on one
  page in both languages.

### Known limitations

- **Windows `win-x86` only.** The process must be 32-bit because the Microsoft Visual
  FoxPro ODBC Driver is x86. There is no x64 or Linux build.
- **End-to-end DBF tests need a self-hosted runner.** The Visual FoxPro driver is not
  installed on GitHub-hosted runners, so the portable validation surface is the unit
  suite plus the end-to-end SQLite cycle.

[Unreleased]: https://github.com/peopleworks/DBFSync/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/peopleworks/DBFSync/releases/tag/v1.0.0
