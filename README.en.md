<div align="center">

# PeopleWorks DBFSync

[![CI](https://github.com/peopleworks/DBFSync/actions/workflows/ci.yml/badge.svg)](https://github.com/peopleworks/DBFSync/actions/workflows/ci.yml)
[![License](https://img.shields.io/github/license/peopleworks/DBFSync?color=3fb950)](LICENSE)
[![Stars](https://img.shields.io/github/stars/peopleworks/DBFSync?color=00bcd4)](https://github.com/peopleworks/DBFSync/stargazers)
![Version](https://img.shields.io/badge/version-1.0.0-00bcd4)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![Platform](https://img.shields.io/badge/platform-Windows%20x86-0078D4)

[Español](README.md) · **English**

**[📖 Pocket guide — every command on one page](https://peopleworks.github.io/DBFSync/)**

**.NET 10 CLI for migrating and synchronizing Visual FoxPro DBF files to
PostgreSQL, SQL Server, or SQLite without shutting the ERP down.** The Xbase++
application keeps operating on its DBFs while other services consume an
up-to-date relational database.

<img src="assets/hero.svg" width="900"
     alt="DBFSync flow diagram: Visual FoxPro DBF files are read through the x86 ODBC driver, which preserves RECNO(), normalizes dates and text, and computes SHA-256; rows pass through a temporary staging table and a per-table transaction that inserts, updates, or deletes in PostgreSQL, SQL Server, or SQLite.">

<sub><code>RECNO()</code> preserves the physical record identity and <code>SHA-256</code>
decides whether it changed; each table is reconciled in its own transaction.</sub>

</div>

PeopleWorks DBFSync provides secure named profiles, wildcard selection,
content-based change detection, controlled schema evolution, unattended
execution, and a bilingual Spectre.Console interface.

> **Platform:** Windows `win-x86`. The process must be 32-bit because the
> Microsoft Visual FoxPro ODBC Driver is x86.

## Contents

- [Features](#features)
- [How it works](#how-it-works)
- [Requirements and installation](#requirements-and-installation)
- [SQLite quick start](#sqlite-quick-start)
- [Profiles and security](#profiles-and-security)
- [Command reference](#command-reference)
- [DBF selection](#dbf-selection)
- [Migration and synchronization](#migration-and-synchronization)
- [Schema evolution](#schema-evolution)
- [Type mapping](#type-mapping)
- [Task Scheduler](#task-scheduler)
- [Logs and exit codes](#logs-and-exit-codes)
- [Troubleshooting](#troubleshooting)
- [Development and contributing](#development-and-contributing)
- [Related PeopleWorks projects](#related-peopleworks-projects)

## Features

| Area | Capabilities |
|---|---|
| Destinations | PostgreSQL, SQL Server, and SQLite |
| Selection | Exact names, lists, `*`, `?`, `--all`, exclusions, and repeated options |
| Identity | `RECNO()` preserves each DBF record's physical identity |
| Changes | SHA-256 detects modified values even when `RECNO()` remains unchanged |
| Reconciliation | Inserts new rows, updates changed rows, and deletes missing or marked-deleted rows |
| Schema | Adds fields, safely widens types, blocks destructive changes, and supports explicit rebuilds |
| Security | Named profiles, DPAPI-protected passwords, and encrypted transport by default |
| Automation | No credentials in arguments, per-run logs, and concurrent-job locking |
| Languages | Extensible English and Spanish resources |
| CLI experience | PeopleWorks banner, colors, tables, and connection context without exposed secrets |

## How it works

```text
Visual FoxPro DBF
        │
        ▼
x86 ODBC ── RECNO() + normalization + SHA-256
        │
        ▼
Temporary staging table
        │
        ▼
Transaction per table
        │
        ├── INSERT new records
        ├── UPDATE changed hashes
        └── DELETE missing records
        │
        ▼
PostgreSQL / SQL Server / SQLite
```

- The driver opens with `Deleted=Yes`; marked-deleted records are excluded and
  removed from the destination during `sync`.
- Empty FoxPro dates (`1899-12-30`) become `NULL`.
- Text is normalized before hashing.
- Each DBF has its own transaction. When multiple tables are selected and a
  later one fails, previously committed tables remain synchronized.
- Destination names are normalized to lowercase, and DBFSync adds
  `_dbfsync_recno`, `_dbfsync_hash`, and `_dbfsync_at`.

## Requirements and installation

### Requirements

- Windows with the **.NET 10 x86 runtime** or .NET 10 SDK.
- **x86** Microsoft Visual FoxPro ODBC Driver.
- Read access to the DBF directory.
- For PostgreSQL or SQL Server: permission to connect, create the schema, and
  create or alter tables.
- For SQLite: write access to the `.db` file directory.
- For `--create-database`: database creation permission through PostgreSQL's
  `postgres` database or SQL Server's `master`.

### Build from source

```powershell
dotnet restore .\DBFSync.slnx
dotnet build .\DBFSync.slnx --configuration Release
dotnet test .\DBFSync.slnx --configuration Release
dotnet publish .\src\DBFSync\DBFSync.csproj `
  --configuration Release `
  --runtime win-x86 `
  --self-contained false `
  --output .\dist\win-x86
```

Verify the executable:

```powershell
.\dist\win-x86\dbfsync.exe --version
.\dist\win-x86\dbfsync.exe --help --lang en
```

Copy the complete `dist\win-x86` directory to a stable location such as
`C:\Tools\PeopleWorks\DBFSync`. Do not copy only `dbfsync.exe`; its published
DLLs and resources are also required.

## SQLite quick start

SQLite has no server or credentials and is the recommended destination for a
first unattended test.

```powershell
# 1. Create the profile and SQLite file
dbfsync profile set local `
  --engine sqlite `
  --file C:\DBFSync\erp.db

# 2. Review what will be read
dbfsync inspect `
  --source C:\ERP\Data `
  --tables "customers,items,fa*.dbf" `
  --exclude "*history*,*bak*"

# 3. Initial load
dbfsync migrate `
  --profile local `
  --source C:\ERP\Data `
  --tables "customers,items,fa*.dbf"

# 4. Subsequent synchronization
dbfsync sync `
  --profile local `
  --source C:\ERP\Data `
  --tables "customers,items,fa*.dbf" `
  --sync-schema
```

The database file and its parent directories are created automatically.

## Profiles and security

Named profiles are stored in:

```text
C:\ProgramData\PeopleWorks\DBFSync\profiles.json
```

Logs are written to:

```text
C:\ProgramData\PeopleWorks\DBFSync\logs
```

`DBFSYNC_HOME` changes both locations:

```powershell
$env:DBFSYNC_HOME = "D:\PeopleWorks\DBFSync"
dbfsync profile list
```

Passwords are never stored as plaintext. DPAPI protects them under one of these
scopes:

| Scope | Recommended use | Consideration |
|---|---|---|
| `--scope user` | Interactive use or a task running under the same Windows account | Only that account can decrypt the password |
| `--scope machine` | A service or task using another account on the same computer | Protect `profiles.json` with restrictive NTFS permissions |

SQLite and SQL Server integrated authentication store no password. To update a
profile, run `profile set` again with the same name and all options.

### PostgreSQL with verified SSL

Encrypted transport is enabled by default. PostgreSQL validates both the
certificate and server name.

```powershell
dbfsync profile set erp-pg `
  --engine postgresql `
  --server pg01.example.com `
  --port 5432 `
  --database erp `
  --schema public `
  --user dbfsync `
  --scope user `
  --create-database
```

The password is requested once, and the connection is tested before the profile
is saved.

### Local PostgreSQL without SSL

Some local installations do not enable SSL. On a trusted development computer,
explicitly use `--no-encrypt`:

```powershell
dbfsync profile set erp-pg `
  --engine postgresql `
  --server 127.0.0.1 `
  --port 5432 `
  --database pwerp `
  --schema public `
  --user postgres `
  --scope user `
  --no-encrypt `
  --create-database
```

Then verify the profile:

```powershell
dbfsync profile test erp-pg
```

> `--trust-server-certificate` still requires SSL but skips certificate
> validation. It does not fix a server that offers no SSL; trusted local
> development requires `--no-encrypt`. Never disable encryption on untrusted
> networks or in production.

### SQL Server with a database user

```powershell
dbfsync profile set erp-sql `
  --engine sqlserver `
  --server sql01 `
  --port 1433 `
  --database ERP `
  --schema dbo `
  --user dbfsync `
  --scope machine `
  --trust-server-certificate `
  --create-database
```

SQL Server also encrypts by default. Use `--trust-server-certificate` only when
the server certificate is accepted by the organization but cannot be validated
by the client computer.

### SQL Server Windows authentication

```powershell
dbfsync profile set erp-windows `
  --engine sqlserver `
  --server sql01 `
  --database ERP `
  --schema dbo `
  --integrated
```

### SQLite

```powershell
dbfsync profile set local `
  --engine sqlite `
  --file C:\DBFSync\erp.db
```

SQLite uses WAL, a busy timeout, and a `.dbfsync.lock` sidecar to prevent two
DBFSync writers from using the same database simultaneously. Decimals are
stored as invariant canonical text for exact precision; dates and timestamps
are stored as ISO text.

### Profile management

```powershell
dbfsync profile list
dbfsync profile show erp-pg
dbfsync profile test erp-pg
dbfsync profile remove erp-pg
```

### Database creation

`--create-database` creates a missing database before testing and saving the
profile. An idempotent operation is also available for saved profiles:

```powershell
dbfsync database create --profile erp-pg
```

- PostgreSQL connects through the `postgres` maintenance database.
- SQL Server connects through `master`.
- SQLite creates the file and parent directories.
- An existing database is reported successfully and never recreated.

## Command reference

| Command | Description |
|---|---|
| `dbfsync --help` | Show concise help |
| `dbfsync --version` | Show the version |
| `dbfsync sample` | Show complete scenarios |
| `dbfsync profile set NAME` | Create or replace a profile |
| `dbfsync profile list` | List profiles |
| `dbfsync profile show NAME` | Show a profile without secrets |
| `dbfsync profile test NAME` | Test a connection |
| `dbfsync profile remove NAME` | Remove a profile |
| `dbfsync database create --profile NAME` | Create or verify a database |
| `dbfsync inspect ...` | Inspect schema, active rows, and a sample |
| `dbfsync migrate ...` | Replace destination contents |
| `dbfsync sync ...` | Reconcile inserts, updates, and deletes |

Spanish aliases such as `perfil`, `migrar`, `sincronizar`, `inspeccionar`,
`ejemplo`, and `ayuda` are also accepted.

### `profile set` options

| Option | Engines | Description |
|---|---|---|
| `--engine postgresql\|sqlserver\|sqlite` | All | Required engine |
| `--server HOST` | PostgreSQL, SQL Server | Server or IP address |
| `--port N` | PostgreSQL, SQL Server | Port; PostgreSQL defaults to 5432 |
| `--database NAME` | PostgreSQL, SQL Server | Database name |
| `--file PATH` | SQLite | SQLite database file |
| `--schema NAME` | PostgreSQL, SQL Server | Defaults to `public` or `dbo` |
| `--user NAME` | PostgreSQL, SQL Server | User when integrated authentication is not used |
| `--integrated` | SQL Server | Use the process Windows identity |
| `--scope user\|machine` | PostgreSQL, SQL Server | DPAPI scope; defaults to `user` |
| `--no-encrypt` | PostgreSQL, SQL Server | Disable PostgreSQL SSL or stop requiring SQL Server encryption; trusted local development only |
| `--trust-server-certificate` | PostgreSQL, SQL Server | Keep encryption without certificate validation |
| `--password-stdin` | PostgreSQL, SQL Server | Read one line from stdin instead of prompting |
| `--create-database` | All | Create the database or file when missing |

### Transfer options

| Option | Description |
|---|---|
| `--profile NAME` | Required destination profile |
| `--source PATH` | Required DBF directory |
| `--tables PATTERNS` | Comma-separated names or patterns |
| `--table PATTERN` | Single repeatable pattern |
| `--all` | Select every DBF in the directory |
| `--exclude PATTERNS` | Exclude names or patterns |
| `--batch-size N` | Rows per batch; default 5000, range 1-100000 |
| `--sync-schema` | Apply safe schema evolution |
| `--allow-drop-columns` | Allow column removal; requires `--sync-schema` |
| `--recreate` | Rebuild tables; `migrate` only |

`--all` and inclusion patterns are mutually exclusive.

### Language

`--lang` may appear anywhere:

```powershell
dbfsync --help --lang en
dbfsync sample --lang es
dbfsync inspect --lang en --source C:\ERP\Data --tables "fa*.dbf"
```

Scheduled jobs may also use `DBFSYNC_LANG`:

```powershell
$env:DBFSYNC_LANG = "en"
```

## DBF selection

Searches include `.dbf` files directly inside the source directory; subfolders
are not traversed.

| Need | Example |
|---|---|
| One DBF | `--table customers` |
| List | `--tables customers,items` |
| Repeated options | `--table customers --table items` |
| Prefix | `--tables "fa*.dbf"` |
| One character | `--tables "cbmovf??"` |
| Exclusion | `--exclude "*history*,*bak*"` |
| Inline negation | `--tables "fa*,!fahistory*"` |
| All except some | `--all --exclude "temp*,*bak*,test*"` |

Every inclusion pattern must match at least one DBF, and exclusions cannot leave
an empty selection. This prevents a misspelled scheduled job from succeeding
silently.

Inspect before writing:

```powershell
dbfsync inspect `
  --source C:\ERP\Data `
  --tables "fa*.dbf,cbmovf??" `
  --exclude "*history*,*bak*"
```

## Migration and synchronization

### Initial load

`migrate` replaces all destination contents for the selected tables:

```powershell
dbfsync migrate `
  --profile erp-pg `
  --source C:\ERP\Data `
  --tables "customers,items,fa*.dbf"
```

### Recurring synchronization

```powershell
dbfsync sync `
  --profile erp-pg `
  --source C:\ERP\Data `
  --tables "customers,items,fa*.dbf" `
  --exclude "*history*"
```

For each table:

1. `RECNO()` identifies the physical record.
2. SHA-256 represents all normalized values.
3. A new `RECNO()` produces an `INSERT`.
4. The same `RECNO()` with a different hash produces an `UPDATE`.
5. A missing `RECNO()` produces a `DELETE`.

Any changed field is therefore synchronized even when `RECNO()` stays the same.

### Engine implementation

| Engine | Staging and reconciliation | Lock |
|---|---|---|
| PostgreSQL | Binary `COPY` and transaction | Per-table advisory lock |
| SQL Server | `SqlBulkCopy` and transaction | Per-table `sp_getapplock` |
| SQLite | Temporary table and transactional UPSERT | Exclusive file lock per database |

## Schema evolution

Without schema flags, an existing destination table must exactly match the DBF.
DBFSync stops before data reconciliation when it differs.

### Add fields or widen types

```powershell
dbfsync sync `
  --profile erp-pg `
  --source C:\ERP\Data `
  --tables customers,items `
  --sync-schema
```

`--sync-schema`:

- adds new fields as nullable columns;
- safely widens text lengths;
- widens decimal precision and scale while preserving capacity;
- allows date-to-date/time widening when applicable;
- applies schema and data in the same per-table transaction.

### Remove fields

Fields removed from the DBF are rejected by default:

```powershell
dbfsync sync `
  --profile erp-pg `
  --source C:\ERP\Data `
  --tables customers `
  --sync-schema `
  --allow-drop-columns
```

`--allow-drop-columns` is destructive and requires `--sync-schema`.

### Incompatible changes

Reduced text length or decimal precision and other unsafe conversions require a
rebuild:

```powershell
dbfsync migrate `
  --profile erp-pg `
  --source C:\ERP\Data `
  --tables customers `
  --recreate
```

`--recreate` drops and recreates the table inside a transaction. It is available
only for `migrate` and cannot be combined with `--sync-schema`.

## Type mapping

| DBF type | SQL Server | PostgreSQL | SQLite |
|---|---|---|---|
| Text | `nvarchar(n)` / `nvarchar(max)` | `varchar(n)` / `text` | `TEXT` |
| Decimal | `decimal(p,s)` | `numeric(p,s)` | Exact canonical `TEXT` |
| Integer | `bigint` | `bigint` | `INTEGER` |
| Floating point | `float(53)` | `double precision` | `REAL` |
| Logical | `bit` | `boolean` | `INTEGER` |
| Date | `date` | `date` | ISO `TEXT` |
| Date/time | `datetime2(3)` | `timestamp without time zone` | ISO `TEXT` |
| Binary | `varbinary(max)` | `bytea` | `BLOB` |

SQLite does not encode length or precision in physical column types, so schema
synchronization adds or drops columns there without widening those attributes.

## Task Scheduler

The task receives only the profile name and never the password:

```text
C:\Tools\PeopleWorks\DBFSync\dbfsync.exe sync --profile erp-pg --source C:\ERP\Data --tables "fa*.dbf,cbmovf??" --exclude "*history*" --lang en
```

PowerShell example:

```powershell
$action = New-ScheduledTaskAction `
  -Execute "C:\Tools\PeopleWorks\DBFSync\dbfsync.exe" `
  -Argument 'sync --profile erp-pg --source C:\ERP\Data --tables "fa*.dbf,cbmovf??" --exclude "*history*" --lang en'

$trigger = New-ScheduledTaskTrigger `
  -Once `
  -At (Get-Date).AddMinutes(1) `
  -RepetitionInterval (New-TimeSpan -Minutes 5)

$settings = New-ScheduledTaskSettingsSet `
  -MultipleInstances IgnoreNew `
  -StartWhenAvailable

Register-ScheduledTask `
  -TaskName "PeopleWorks DBFSync ERP" `
  -Action $action `
  -Trigger $trigger `
  -Settings $settings `
  -Description "Synchronizes ERP DBFs with PostgreSQL"
```

- With `--scope user`, run the task under the account that created the profile.
- With `--scope machine`, restrict NTFS access to the configuration directory.
- Configure **do not start a new instance** while the previous run is active.
- Treat only exit code `0` as success.

## Logs and exit codes

Every `migrate` and `sync` creates a UTF-8 log with UTC timestamps:

```text
C:\ProgramData\PeopleWorks\DBFSync\logs\
yyyyMMdd-HHmmssfff-operation-profile-pid.log
```

| Code | Meaning |
|---|---|
| `0` | Successful execution |
| `1` | Invalid arguments, connection error, or transfer failure |
| `130` | Ctrl+C cancellation |

Logs contain progress, counts, and errors, but never passwords.

## Troubleshooting

### `SSL connection requested. No SSL enabled connection from this host is configured`

The profile requires SSL, but the local PostgreSQL server does not offer it.
Save the complete profile again with `--no-encrypt`:

```powershell
dbfsync profile set erp-pg `
  --engine postgresql `
  --server 127.0.0.1 `
  --port 5432 `
  --database pwerp `
  --schema public `
  --user postgres `
  --scope user `
  --no-encrypt
```

Use this only for trusted local development. If SSL exists but its certificate
cannot be validated, use `--trust-server-certificate` instead.

### `Unknown options: --no-encryp`

The option includes a final `t`: `--no-encrypt`.

### The DBF cannot be opened

Confirm that:

1. the **x86 Microsoft Visual FoxPro ODBC Driver** is installed;
2. the `win-x86` artifact is running;
3. the account has read access to the directory;
4. no process opened the DBF exclusively.

### DPAPI cannot decrypt the password

- `--scope user`: run under the Windows account that created the profile.
- `--scope machine`: the profile must have been created on the same computer.
- Save the profile again after changing computers or accounts.

### The database cannot be created

The user needs `CREATEDB` in PostgreSQL or `CREATE ANY DATABASE` in SQL Server.
Alternatively, create the database manually and save the profile without
`--create-database`.

### The schema changed

- Compatible change: run with `--sync-schema`.
- Removed field: consciously add `--allow-drop-columns`.
- Incompatible change: use `migrate --recreate` after confirming the table may
  be rebuilt.

### A wildcard matches no files

Every inclusion pattern must match. Use `inspect`, verify the directory, and
remember that searches do not traverse subdirectories.

### Another execution is synchronizing

Wait for the existing run. Configure Task Scheduler to prevent parallel
instances, and never delete the SQLite lock file while a process is active.

## Development and contributing

### Structure

```text
DBFSync.slnx
├── src
│   ├── DBFSync.Core   DBF reading, profiles, and destination engines
│   └── DBFSync        CLI application and presentation
├── tests
│   └── DBFSync.Core.Tests
├── .github
│   └── workflows
│       └── ci.yml     Build, tests, audit, and win-x86 artifact
├── assets
│   └── hero.svg       Synchronization flow diagram
├── README.md
├── README.en.md
└── LICENSE
```

Main dependencies:

- `System.Data.Odbc` for Visual FoxPro;
- `Npgsql` for PostgreSQL;
- `Microsoft.Data.SqlClient` for SQL Server;
- `Microsoft.Data.Sqlite` and SQLitePCLRaw for SQLite;
- `Spectre.Console` for the terminal.

### Tests and dependency audit

```powershell
dotnet test .\DBFSync.slnx --configuration Release
dotnet list .\DBFSync.slnx package --vulnerable --include-transitive
```

Tests cover hashing, profiles, selection, localization, type mapping, schema
compatibility, and SQLite integration.

### GitHub Actions CI

The repository ships [`.github/workflows/ci.yml`](.github/workflows/ci.yml),
which runs on `windows-latest` for every push and pull request targeting `main`,
and on demand through `workflow_dispatch`:

| Step | What it does |
|---|---|
| `dotnet restore` and `dotnet build` | Builds the whole solution in `Release` |
| `dotnet test` | Runs the test suite |
| `dotnet list package --vulnerable` | Fails the run if a vulnerable dependency appears |
| `dotnet publish` | Produces the framework-dependent `win-x86` binary |
| `actions/upload-artifact` | Publishes `dist/win-x86` as a downloadable artifact |

Standard runners do not include the Visual FoxPro driver. End-to-end DBF tests
require a self-hosted Windows runner with the x86 driver installed. On
GitHub-hosted runners the portable validation surface is the unit test suite
plus the end-to-end SQLite cycle.

### Add a language

Copy:

```text
src\DBFSync.Core\Resources\Strings.es.resx
```

to `Strings.<culture>.resx`, such as `Strings.pt.resx`, and translate only its
values. English is the neutral resource.

Contributions should keep all three engines consistent, include tests for
behavioral changes, and never commit secrets or ERP data.

## Related PeopleWorks projects

DBFSync is one of the three PeopleWorks data tools. Each one covers a different
stage of a modernization, and all three are MIT-licensed .NET command-line
tools.

| | [DBFSync](https://github.com/peopleworks/DBFSync) | [SQLDiff](https://github.com/peopleworks/SqlSchemaDiff) | [SyncJob](https://github.com/peopleworks/syncjob) |
|---|---|---|---|
| **Purpose** | Migrate and synchronize legacy data | Compare and align schemas | Continuously synchronize relational data |
| **Source** | Visual FoxPro DBF over x86 ODBC | SQL Server schema | SQL Server |
| **Destination** | PostgreSQL, SQL Server, or SQLite | Data-preserving `ALTER` script | SQL Server |
| **Safety model** | Per-table transaction, SHA-256 change detection, destructive schema changes blocked | Transactional apply and drift detection with exit code `2` for CI | Unattended execution as a CLI or as a Windows service |
| **Execution** | CLI, Windows `win-x86`, .NET 10 | Single-binary CLI, .NET 9 | CLI and Windows service, .NET |

The three compose into a gradual modernization pipeline:

1. **SQLDiff** brings the relational schema to its expected state and detects
   drift between environments before any data is touched.
2. **DBFSync** loads the Visual FoxPro DBFs onto that schema and keeps them in
   sync while the legacy ERP stays in production.
3. **SyncJob** moves the now-relational data to the other SQL Server systems
   that consume it.

---

## Credits

Created by **Pedro Hernández — PeopleWorks**
· [GitHub](https://github.com/pedrohernandez-xari)
· [Microsoft MVP for .NET](https://mvp.microsoft.com/en-US/mvp/profile/24060a02-dbc6-44ec-bca5-c213ff9835c5)

Built for the .NET, Visual FoxPro, and data-modernization communities:
*by and for the developer community.*

DBFSync is part of the data practice at **PeopleWorks Services, LLC & Xari
Technologies**, alongside
[DPO — Data Performance Optimizer](https://dpo.peopleworksservices.com/), the
multi-database assessment, optimization, and governance platform.

### Get involved

| | |
|---|---|
| Repository | <https://github.com/peopleworks/DBFSync> |
| Bugs and proposals | [Issues](https://github.com/peopleworks/DBFSync/issues) |
| Contributions | [Pull requests](https://github.com/peopleworks/DBFSync/pulls) |

If DBFSync helped you through a real migration, open an issue describing the
scenario: concrete ERP cases are what move all three engines forward. A pull
request with tests, a new translation, or a star helps too.

Distributed under the [MIT License](LICENSE).

<p align="center">
  <sub><b>PeopleWorks DBFSync</b> • Visual FoxPro data synchronization for real-world systems</sub><br>
  <sub><b>PeopleWorks data tools</b> —
  <b>DBFSync</b> bridges DBFs to modern databases ·
  <a href="https://github.com/peopleworks/SqlSchemaDiff">SQLDiff</a> aligns schemas ·
  <a href="https://github.com/peopleworks/syncjob">SyncJob</a> moves relational data</sub>
</p>
