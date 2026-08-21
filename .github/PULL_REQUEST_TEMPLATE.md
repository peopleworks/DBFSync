<!--
  English or Spanish is fine — write in whichever you are comfortable with.
  Inglés o español, como prefiera.
-->

## What this changes · Qué cambia

<!-- One paragraph. What was wrong or missing, and what this does about it. -->

Closes #

## Why · Por qué

<!--
  The reasoning that is not obvious from the diff: the failure it prevents, the
  behavior it preserves, the alternative you rejected.
-->

## Checklist

- [ ] **Tests.** Behavioral changes are covered. A type-mapping or schema-compatibility
      fix is pinned the way the existing tests pin theirs.
      *Cambios de comportamiento cubiertos con pruebas.*
- [ ] **The three engines stay consistent.** PostgreSQL, SQL Server and SQLite implement
      `ITableDestination`; a capability added to one belongs in all three, or the code
      says why it does not.
      *Los tres motores quedan consistentes.*
- [ ] **Both READMEs move together.** `README.md` and `README.en.md` keep section parity
      and the same public claims, worded naturally in each language rather than calqued.
      *Los dos README mantienen paridad de secciones.*
- [ ] **New or removed CLI options are documented in all three surfaces** — both READMEs
      and `docs/index.html`. `DocumentationSyncTests` fails the build otherwise, naming
      the file and the option.
      *Opciones nuevas o eliminadas documentadas en las tres superficies.*
- [ ] **`dotnet test .\DBFSync.slnx --configuration Release` passes**, and the build is
      warning-free — `TreatWarningsAsErrors` is on.
      *Las pruebas pasan y el build no emite advertencias.*
- [ ] **No secrets or ERP data.** No credentials, customer names, real machine paths, or
      `.dbf` files, in the code, the tests, the docs or the commit message.
      *Sin secretos ni datos del ERP.*
- [ ] **One concern.** A type-mapping fix and a new command are two pull requests.
      *Un solo tema por pull request.*

<!--
  If this changes destructive behavior — sync deletions, --allow-drop-columns,
  --recreate — say so explicitly here. Those paths delete data in the destination.

  Si esto cambia comportamiento destructivo, dígalo explícitamente: esas rutas
  eliminan datos en el destino.
-->
