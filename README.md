# HSK Automation – Backend

Backend for the housekeeping task sheet automation at **Novotel Münster City** and **ibis budget Münster City**.
It receives the two Opera task sheets, checks and reads them, assigns a cleaning colour to every room,
adds coloured markers to the PDF and serves the result to the web app.

> Frontend: [jubayer17/HSK-Frontend](https://github.com/jubayer17/HSK-Frontend) (Next.js 15)

Scope: the core demo workflow (Stage 1 of the implementation plan).

## Contents

- [Tech stack](#tech-stack)
- [Project structure](#project-structure)
- [Getting started](#getting-started)
- [Configuration](#configuration)
- [Tests](#tests)
- [API endpoints](#api-endpoints)
- [Timers](#timers)
- [The morning run](#the-morning-run)
- [Deployment](#deployment)
- [Notes for the team](#notes-for-the-team)
- [Privacy](#privacy)

## Tech stack

| Area | Choice |
|---|---|
| Runtime | .NET 8 isolated Azure Functions + Durable Functions |
| Database | Azure SQL (SQL Server 2022 in Docker locally), Dapper |
| Files | Azure Blob Storage (Azurite locally), Managed Identity in Azure |
| PDF reading | OpenDataLoader (Java 17), wrapped by the client's `CompliantOpenDataLoaderExtractor` |
| PDF marking | PDFsharp behind `IPdfMarker` (Syncfusion once licensed) |
| Tests | xUnit, Testcontainers (SQL Server) |

## Project structure

```
HousekeepingParser.sln
Directory.Build.targets      removes the Durable Task analyzer (needs a newer compiler than SDK 8.0.1xx)
docker-compose.yml           local SQL Server + Azurite (development only)
Dockerfile                   Function App image: .NET 8 + Java 17 + reader JAR
migrations/                  numbered SQL scripts, each run once at start-up
tools/
  setup-local.sh             one-time local set-up (JAR, .env, local.settings.json)
  get-opendataloader.sh      downloads the reader JAR (Apache-2.0)
src/HousekeepingParser/      the Function App
  Core/                      hotels, room inventory, colours, Berlin clock
  Data/                      connection and migrator
  Parsing/                   reader wrapper, layout mapper, task sheet parser, name filter
  Coloring/                  IColorRuleService + rhythm cycles (pure, no database)
  Rhythm/                    Director rules, planned changes, guest rhythm frozen at arrival
  Runs/                      morning-run pipeline steps, run storage, run view for the screens
  PdfMarking/                dots on the PDF (IPdfMarker)
  Accounts/                  Stage 1 sign-in, sessions (httpOnly cookie, hashed token), PBKDF2 passwords
  Audit/                     UserConfirmationLog, DirectorAuditLog, PdfProcessingLog
  Storage/                   blob containers
  Functions/                 HTTP endpoints, Durable orchestration, timers
src/HousekeepingParser.Tests/
  Fixtures/                  reader JSON for the 4 sample sheets, staff names replaced by fake values
```

## Getting started

### Prerequisites

- .NET 8 SDK
- Java 17
- Azure Functions Core Tools v4
- Docker
- Python 3 (used by `setup-local.sh`)

### Run locally

```bash
git clone https://github.com/jubayer17/HSK-Backend.git server
cd server
tools/setup-local.sh                       # once: reader JAR, random local SQL password, local.settings.json
docker compose up -d                       # SQL Server + Azurite
cd src/HousekeepingParser && func start    # http://localhost:7071/api
```

On start-up the app creates the database, runs the migrations, creates the blob containers and seeds the two
accounts: `director` and `operations`. The password is `demo` unless `HSK_DEMO_PASSWORD` was set when
running `setup-local.sh`.

Then start the [frontend](https://github.com/jubayer17/HSK-Frontend#option-b--real-backend) with
`NEXT_PUBLIC_USE_MOCK=false`, sign in, upload two task sheets and open the results.

Recommended local layout (the real-reader tests look for the sample PDFs in the parent folder):

```
HSK-Automation/
  client/                       HSK-Frontend
  server/                       ← this repo (HSK-Backend)
  task_sheet_report_*.PDF       real sample sheets, never committed
```

## Configuration

Local settings live in `src/HousekeepingParser/local.settings.json`, generated from
`local.settings.example.json` by `setup-local.sh`. In Azure the same keys are App Settings / Key Vault references.

| Key | Meaning |
|---|---|
| `Database__ConnectionString` | SQL connection string |
| `Database__CreateIfMissing` / `Database__MigrateOnStartup` | create the database and run `migrations/` on start-up |
| `Storage__ConnectionString` | Blob Storage (`UseDevelopmentStorage=true` for Azurite) |
| `OpenDataLoader__JavaExecutable` / `__JarPath` / `__Timeout` | the PDF reader |
| `Auth__SecureCookie` | `true` everywhere except local HTTP |
| `Auth__SeedUsers__N__*` | seeded accounts (username, display name, role, password) |
| `Validation__EnforceSheetAge` | "printed within 24 hours" check; `false` locally because the samples are from July/August 2026, keep `true` everywhere else |
| `WEBSITE_TIME_ZONE` / `TZ` | `Europe/Berlin` |

`server/.env` holds only the local SQL `sa` password for `docker-compose.yml`. Both files are git-ignored.

## Tests

```bash
dotnet test
```

| Group | Needs | What it proves |
|---|---|---|
| Parser acceptance | nothing | All numbers from plan section 2: 103 / 74 / 112 / 116 rooms, the six summary values, rooms, adults and children per sheet, FLOAT sheet over two pages, published 30.08.2026 17:34 |
| Privacy | nothing | No staff name or MA number survives parsing; July "name // text //" instructions keep only the text |
| Colour rules | nothing | Both rhythm tables nights 0–8, card / no card, every priority rule |
| PDF | nothing | Dot geometry (y-axis flip, left of the room number, clear of the "!" marker), marking keeps the pages, line-ending repair |
| Real reader | the 4 real PDFs in the parent folder + the JAR | The wrapper runs Java on the real files and gets the totals; temp folder shredded; audit row written |
| Database | Docker | Migrations, schema guard (no name columns), rhythm rules in API **and** database, guest keeps the rhythm of the arrival day, full run on both hotels, duplicate and same-hotel refusal, non-PDF refusal, leak test over every text column |

Tests that need Docker or the real PDFs skip themselves when those are missing.

## API endpoints

All under `/api`, JSON, session cookie `hsk_session` (httpOnly, SameSite=Strict).
The contract is shared with the frontend: `src/lib/types.ts` in HSK-Frontend.

| Method | Path | Who | Notes |
|---|---|---|---|
| POST | `/auth/login` | all | 5 wrong passwords lock the username for 2 minutes |
| POST | `/auth/logout` | all | revokes the session |
| GET | `/auth/me` | signed in | |
| GET | `/rhythm` | signed in | active and planned rhythm per hotel; due planned changes are activated first |
| POST | `/rhythm` | Director | `confirmed: true` required; 1st of a month; first set-up may be this month, a change only a future month; one planned change per hotel |
| DELETE | `/rhythm/{hotelId}/scheduled` | Director | until the day before it starts |
| GET | `/rhythm/history` | signed in | set, scheduled, cancelled, activated |
| GET | `/runs` | signed in | last 100 runs (without rooms) |
| POST | `/runs` | signed in | multipart: 2 × `files`, `requests` (JSON), `confirmed=true`; 409 without an active rhythm |
| GET | `/runs/{id}` | signed in | steps, warnings, totals, rooms with colour and reason |
| GET | `/runs/{id}/pdf/{hotelId}` | signed in | marked PDF, `?download=1` for attachment, `no-store` |
| GET | `/health` | anyone | no data |

## Timers

Berlin time (`WEBSITE_TIME_ZONE`):

| When | What |
|---|---|
| 00:05 on the 1st | activates planned rhythm changes |
| 03:30 daily | retention clean-up: refused PDFs 30 days, stale uploads 90 days, processing log 90 days, expired sessions |

## The morning run

One Durable orchestration per run; both files go through the steps in parallel:

1. **Rhythm** – checked before the run is accepted (409 otherwise).
2. **Read** – line-ending repair if needed, then `CompliantOpenDataLoaderExtractor` (own temp folder,
   no shell, `-Xmx1g`, 30 s limit, one retry on time-out, folder shredded, `PdfProcessingLog` row).
3. **Check** – "Task Sheet Report" title, known hotel name from the PDF header (never the file name),
   printed within 24 h, Report Summary complete; then across files: one sheet per hotel, never the same
   sheet twice (also a filtered unique index).
4. **Save** – task sheets, room rows (with the room number's position), summary. Rows ≠ "Total Rooms",
   sheet counts and per-sheet totals are compared; a mismatch is a warning and the run continues
   (conflict C5 is still open with the client).
5. **Colour** – priority list from plan 9.5, guest rhythm frozen at arrival, "daily cleaning for the whole
   stay" carried over within the same stay.
6. **Mark** – one dot left of each coloured room number; the original moves to `processed-archive`.

## Deployment

The `Dockerfile` builds one image with the .NET 8 isolated worker, Java 17 and the reader JAR.
Run `tools/get-opendataloader.sh` before `docker build` (the JAR is not committed):

```bash
tools/get-opendataloader.sh
docker build -t hsk-backend .
```

In Azure, Azure SQL and Azure Storage replace the Docker services, and secrets come from Key Vault.

## Notes for the team

- **Reader output (C15).** OpenDataLoader 2.5 returns a tree of text blocks with bounding boxes, not
  pages → lines → words, and its CLI takes the input as a positional argument with `-o`. The wrapper is
  otherwise the client's class; `OdlLayoutMapper` turns the blocks into the wrapper's line/word contract.
  Blocks are column-wise, so rows are rebuilt from positions.
- **Damaged sample files.** The four sample PDFs had every LF turned into CR LF (e.g. a text-mode
  transfer), so their cross-reference offsets are wrong and the reader refuses them. `PdfBytes.RepairLineEndings`
  undoes exactly that damage, only when the file has no bare LF and a broken xref. Files straight from Opera
  are probably fine.
- **PDF library (D1).** The handoff names Syncfusion; its licence is still open, so drawing is behind
  `IPdfMarker` and uses PDFsharp (MIT) until it is settled.
- **Sign-in.** Stage 1 simple login as in the plan; Stage 2 replaces it with Microsoft Entra ID.
  Accounts are seeded once from settings (Key Vault in Azure), passwords are PBKDF2-hashed.
- **Not in this stage:** emails, follow-up bookings, automatic 06:55 start, day console, reports,
  Evidence Register.

## Privacy

Real task sheets contain staff names. Never commit them: `*.pdf` / `*.PDF`, `.env`, `local.settings.json`
and the reader JAR are git-ignored. Test fixtures use fake names only.
