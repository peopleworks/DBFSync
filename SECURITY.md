# Security policy · Política de seguridad

## Reporting a vulnerability

Open a [private security advisory](https://github.com/peopleworks/DBFSync/security/advisories/new),
or email **peopleworks@gmail.com** with `DBFSYNC SECURITY` in the subject.
Please do not open a public issue for a vulnerability.

Include the version (`dbfsync --version`), the destination engine, what you ran, and
what happened. **Never attach a profile file, a connection string, a log containing
customer data, or a real ERP DBF.** You will get a first response within a week.

## Supported versions

| Version | Supported |
|---|---|
| 1.0.x | ✅ |

## How credentials are handled

DBFSync never accepts a password as a command-line argument. Other processes on the
machine can read a full command line, shells record it in history, and CI runners
usually echo the command, so the option simply does not exist. A password reaches
DBFSync two ways:

```powershell
# interactive: the prompt does not echo
dbfsync profile set erp-pg --engine postgresql --server pg01.example.com --database erp --user dbfsync

# unattended: one line on stdin, never in the process arguments
"s3cr3t" | dbfsync profile set erp-pg --engine postgresql --server pg01.example.com --database erp --user dbfsync --password-stdin
```

The password is then stored with **Windows DPAPI**, never in plain text, under
`%ProgramData%\PeopleWorks\DBFSync\profiles.json` (or `%DBFSYNC_HOME%`).

`--scope` decides who can decrypt it, and the choice has consequences:

| Scope | Decrypted by | Use when |
|---|---|---|
| `user` (default) | Only the Windows account that created the profile | A scheduled task runs as that same account |
| `machine` | Any account on that machine | A service account differs from the one that set it up — **restrict NTFS access to the configuration folder** |

On SQL Server, `--integrated` avoids storing a password at all.

## Transport

Encryption is on by default: PostgreSQL requires SSL and SQL Server requires an
encrypted connection.

- `--trust-server-certificate` keeps the traffic encrypted but stops validating the
  certificate. It defeats man-in-the-middle protection; use it only with an internal CA
  you cannot install.
- `--no-encrypt` turns transport protection **off entirely**. It exists for a trusted
  local server during development. Do not use it across a shared network.

## What DBFSync writes

- **Run logs** under `%ProgramData%\PeopleWorks\DBFSync\logs\` record progress, row
  counts, schema decisions, and errors. They record the profile name, server, and
  database — **never a password**. They can name tables and columns from your ERP, so
  treat them as internal.
- **Profiles** hold connection metadata and a DPAPI blob. `dbfsync profile show` prints
  a profile without its secret.
- DBFSync **reads** the DBF source and never writes back to it. The Visual FoxPro
  connection is opened with `Deleted=Yes`, so records flagged as deleted are skipped.

## Destructive operations

Three flags can delete data in the **destination** and each requires explicit opt-in:

| Flag | What it deletes |
|---|---|
| `sync` (no flag) | Destination rows whose `RECNO()` is gone from the DBF |
| `--allow-drop-columns` | Destination columns no longer present in the DBF; requires `--sync-schema` |
| `--recreate` | The destination table itself, rebuilt from scratch; `migrate` only |

Without `--sync-schema`, a structural difference stops that table instead of guessing.
Each table is reconciled inside its own transaction, so a failure rolls that table back
rather than leaving it half-written. Run `dbfsync inspect` first — it is read-only and
touches no destination.

---

## Reportar una vulnerabilidad

Abra un [aviso de seguridad privado](https://github.com/peopleworks/DBFSync/security/advisories/new)
o escriba a **peopleworks@gmail.com** con `DBFSYNC SECURITY` en el asunto.
Por favor no abra un issue público para una vulnerabilidad.

Incluya la versión (`dbfsync --version`), el motor destino, qué ejecutó y qué pasó.
**Nunca adjunte un archivo de perfiles, una cadena de conexión, un log con datos de
clientes ni un DBF real del ERP.** Recibirá una primera respuesta en el plazo de una
semana.

## Manejo de credenciales

DBFSync no acepta contraseñas como argumento de línea de comandos: otros procesos
pueden leer la línea completa, el shell la guarda en el historial y los runners de CI
suelen imprimirla. La contraseña se pide de forma interactiva sin eco, o se lee de
stdin con `--password-stdin`, y se guarda protegida con **DPAPI de Windows** en
`%ProgramData%\PeopleWorks\DBFSync\profiles.json`.

`--scope user` (predeterminado) permite descifrarla solo a la cuenta que creó el perfil:
la tarea programada debe ejecutarse con esa misma cuenta. `--scope machine` la habilita
para cualquier cuenta del equipo, así que **limite el acceso NTFS a la carpeta de
configuración**. En SQL Server, `--integrated` evita almacenar contraseña.

## Transporte

El cifrado viene activado. `--trust-server-certificate` mantiene el cifrado pero deja de
validar el certificado, lo que anula la protección contra intermediarios.
`--no-encrypt` desactiva la protección por completo y solo tiene sentido contra un
servidor local de confianza durante el desarrollo.

## Qué escribe DBFSync

Los logs registran progreso, conteos, decisiones de esquema y errores, junto al nombre
del perfil, el servidor y la base — **nunca la contraseña**. Pueden nombrar tablas y
columnas de su ERP, así que trátelos como internos. DBFSync **lee** el origen DBF y
nunca escribe sobre él.

## Operaciones destructivas

`sync` elimina del destino las filas cuyo `RECNO()` ya no está en el DBF.
`--allow-drop-columns` elimina columnas y exige `--sync-schema`. `--recreate` reconstruye
la tabla desde cero y solo existe en `migrate`. Sin `--sync-schema`, una diferencia
estructural detiene esa tabla en vez de adivinar. Cada tabla se reconcilia en su propia
transacción. Use `dbfsync inspect` primero: es de solo lectura y no toca el destino.
