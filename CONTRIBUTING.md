# Contributing to DBFSync · Cómo contribuir

Thanks for taking a look. Issues and pull requests are both welcome, in English or in
Spanish — answer in whichever you are comfortable with.

## Getting set up

```powershell
git clone https://github.com/peopleworks/DBFSync.git
cd DBFSync
dotnet build .\DBFSync.slnx --configuration Release
dotnet test  .\DBFSync.slnx --configuration Release
```

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). The test suite needs
**no database and no DBF**: it covers hashing, profiles, selection, localization, type
mapping, structural compatibility, and a full SQLite cycle against a temporary file.

Running the CLI end to end additionally needs **Windows** and the **Microsoft Visual
FoxPro ODBC Driver (x86)**. The driver is 32-bit only, which is why the project targets
`win-x86` and why GitHub-hosted runners cannot execute the DBF path — that needs a
self-hosted Windows runner with the driver installed.

## Reporting a bug

The useful bug report for a migration tool is a **reproduction in schema**: the DBF
field definitions, the destination engine, the command you ran, and what you got versus
what you expected.

```text
-- source DBF
CLIENTES.DBF   CODIGO C(6), NOMBRE C(40), SALDO N(12,2), ALTA D

-- command
dbfsync sync --profile erp-pg --source C:\ERP\Datos --tables clientes --sync-schema

-- expected: SALDO widened to numeric(14,2)
-- actual:   table stopped with an incompatible-type error
```

That turns straight into a test. **Never paste a real DBF, a connection string, a
password, or customer data** — invent field names if the real ones are sensitive.
Please include `dbfsync --version` and the destination engine version.

## Pull requests

- **Add a test.** Behavioral changes need one. If you fix a type-mapping or
  schema-compatibility bug, pin it the way the existing tests do.
- **Keep the three engines consistent.** PostgreSQL, SQL Server, and SQLite implement
  `ITableDestination`. A capability added to one usually belongs in all three, or needs
  an explicit reason in the code why it does not.
- **Keep the build clean.** `TreatWarningsAsErrors` is on.
- **Never log or commit secrets or ERP data.** No credentials, customer names, real
  paths, or `.dbf` files.
- **Match the surrounding style.** `.editorconfig` covers the mechanical part. The house
  style writes `if (condition)` **with** a space, uses file-scoped namespaces, and writes
  comments that explain *why* rather than restating the code.
- **One concern per pull request.** A type-mapping fix and a new command are two pull
  requests.

## Documentation is tested

The CLI option surface is declared once, in the `CliArguments.EnsureOnly(...)` call of
each command, and repeated in three places: `README.md`, `README.en.md`, and the pocket
guide at `docs/index.html`. `DocumentationSyncTests` reads all four back and compares
them **in both directions**, so:

- Adding an option without documenting it in all three fails the build.
- Leaving behind an option the CLI no longer accepts fails the build.
- A new command that starts accepting options fails `EveryCommandSurfaceIsCoveredByThisTest`
  until it is added to the covered set.

The failure names the file, the section, and the option, so the fix is mechanical.

## Both READMEs move together

`README.md` (Spanish) and `README.en.md` (English) are kept at **section parity** — the
same headings in the same order, and the same public claims. Wording should be
equivalent rather than mechanically identical; write each one so it reads naturally.
If you change one, change the other in the same pull request. The pocket guide carries
both languages in a single page through `.es` / `.en` spans.

## Adding a language

Copy `src\DBFSync.Core\Resources\Strings.es.resx` to `Strings.<culture>.resx` — for
example `Strings.pt.resx` — and translate only the values. English is the neutral
resource, so `Strings.resx` stays English. Nothing else needs to change: `--lang`,
`--language`, and `DBFSYNC_LANG` resolve the culture at startup.

## Releasing

Releases are automated. Bump `<Version>` in `src/DBFSync/DBFSync.csproj`, then:

```powershell
git commit -am "release: 1.1.0"
git tag v1.1.0
git push origin main --tags
```

The `Release` workflow refuses a tag that disagrees with the assembly version, runs the
full suite and the dependency audit, asserts the published `dbfsync.exe` is a 32-bit PE,
then publishes the release with a SHA-256 checksum.

## Code of conduct

Be decent to each other. Harassment or personal attacks are not welcome, and threads
that go that way will be closed. The full terms, and how to report an incident, are in
[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) — Contributor Covenant 2.1, whose official
Spanish translation is at
<https://www.contributor-covenant.org/es/version/2/1/code_of_conduct/>.

---

## Preparar el entorno

Necesita el [SDK de .NET 10](https://dotnet.microsoft.com/download). Las pruebas **no
requieren base de datos ni DBF**. Ejecutar la CLI de punta a punta sí requiere Windows y
el **Microsoft Visual FoxPro ODBC Driver (x86)**: el driver es de 32 bits, y por eso el
proyecto apunta a `win-x86`.

```powershell
git clone https://github.com/peopleworks/DBFSync.git
cd DBFSync
dotnet build .\DBFSync.slnx --configuration Release
dotnet test  .\DBFSync.slnx --configuration Release
```

## Reportar un error

Lo útil es una **reproducción del esquema**: las definiciones de campo del DBF, el motor
destino, el comando que ejecutó, y qué obtuvo frente a qué esperaba. Eso se convierte
directamente en una prueba. **Nunca pegue un DBF real, una cadena de conexión, una
contraseña ni datos de clientes**: invente los nombres de campo si los reales son
sensibles.

## Pull requests

- **Agregue una prueba** para cambios de comportamiento.
- **Mantenga los tres motores consistentes**: PostgreSQL, SQL Server y SQLite
  implementan `ITableDestination`.
- **Mantenga el build limpio**: `TreatWarningsAsErrors` está activado.
- **Nunca registre ni suba secretos ni datos del ERP.**
- **Respete el estilo del entorno.** La casa escribe `if (condición)` **con** espacio,
  usa namespaces con ámbito de archivo, y comenta el *porqué*, no lo que el código ya dice.
- **Un tema por pull request.**

## La documentación está probada

Las opciones de la CLI se declaran una sola vez, en el `EnsureOnly(...)` de cada comando,
y se repiten en `README.md`, `README.en.md` y `docs/index.html`.
`DocumentationSyncTests` compara las cuatro superficies **en ambas direcciones**: agregar
una opción sin documentarla rompe el build, y dejar documentada una que ya no existe
también. El mensaje de error nombra el archivo, la sección y la opción.

## Los dos README se mueven juntos

`README.md` (español) y `README.en.md` (inglés) mantienen **paridad de secciones**: los
mismos encabezados en el mismo orden y las mismas afirmaciones públicas. La redacción
debe ser equivalente, no calcada. Si cambia uno, cambie el otro en el mismo pull request.

## Agregar un idioma

Copie `src\DBFSync.Core\Resources\Strings.es.resx` como `Strings.<cultura>.resx`, por
ejemplo `Strings.pt.resx`, y traduzca solo los valores. Inglés es el recurso neutral.

## Publicar una versión

Actualice `<Version>` en `src/DBFSync/DBFSync.csproj`, haga commit, cree el tag `vX.Y.Z`
y empújelo. El workflow `Release` rechaza un tag que no coincida con la versión del
ensamblado, corre la suite completa y la auditoría de dependencias, verifica que el
`dbfsync.exe` publicado sea un PE de 32 bits, y publica el release con su checksum
SHA-256.
