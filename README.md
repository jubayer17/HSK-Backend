<div align="center">

# 🏨 HSK Automation · Backend

**Reads hotel task sheets, colours every room, and marks the PDF**

Built for Novotel & ibis budget Münster City.

![.NET](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&logoColor=white)
![Azure Functions](https://img.shields.io/badge/Azure_Functions-v4-0062AD?logo=azurefunctions&logoColor=white)
![SQL Server](https://img.shields.io/badge/Azure_SQL-SQL_Server_2022-CC2927?logo=microsoftsqlserver&logoColor=white)
![Java](https://img.shields.io/badge/Java-17-ED8B00?logo=openjdk&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-ready-2496ED?logo=docker&logoColor=white)

[Quick start](#-quick-start) · [How it works](#-how-it-works) · [API](#-api-endpoints) · [Tests](#-tests) · [Frontend repo](https://github.com/jubayer17/HSK-Frontend)

</div>

---

## ✨ What it does

```
 Novotel PDF ─┐                                            ┌─► Marked Novotel PDF
              ├─► Check ─► Read ─► Save ─► Colour ─► Mark ─┤
 ibis PDF ────┘                                            └─► Marked ibis PDF
```

1. Staff upload the two daily **Opera task sheets** through the [frontend](https://github.com/jubayer17/HSK-Frontend).
2. The backend **checks** they are valid, **reads** every room, and **assigns a cleaning colour** using the Director's rhythm rules.
3. It returns one **PDF per hotel**: a designed report (summary, colour legend, room list per task sheet with colour, service and reason), followed by the **original Opera sheet** with a coloured dot next to each room number.

---

## 🧰 Tech stack

| Part | Technology |
|---|---|
| ⚡ App | .NET 8 isolated **Azure Functions** + **Durable Functions** |
| 🗄️ Database | **Azure SQL** (SQL Server in Docker locally) with Dapper |
| 📦 File storage | **Azure Blob Storage** (Azurite locally) |
| 📖 PDF reading | **OpenDataLoader** (Java 17) |
| 🖍️ PDF report & marking | **PDFsharp** with the bundled Inter font (swappable for Syncfusion later) |
| 🧪 Tests | xUnit + Testcontainers |

---

## 🚀 Quick start

### 1. Install the requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Java 17](https://adoptium.net)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- [Docker](https://www.docker.com/get-started)
- Python 3

### 2. Set up and run

```bash
git clone https://github.com/jubayer17/HSK-Backend.git
cd HSK-Backend

tools/setup-local.sh          # one time only
docker compose up -d          # starts the database and storage
cd src/HousekeepingParser
func start                    # API runs at http://localhost:7071/api
```

On the first start, the database, tables, storage and demo accounts are created automatically. ✅

### 3. Sign in

| Username | Password | Role |
|---|---|---|
| `director` | `demo` | Director – can change the cleaning rhythm |
| `operations` | `demo` | Operations – uploads and results |

> 💡 To use a different password, run `HSK_DEMO_PASSWORD=yourpass tools/setup-local.sh` before the first start.

### 4. Start the frontend

Follow the [HSK-Frontend quick start](https://github.com/jubayer17/HSK-Frontend#-quick-start) with `NEXT_PUBLIC_USE_MOCK=false`.

---

## 📁 Project structure

```
HSK-Backend/
├── src/
│   ├── HousekeepingParser/          The app
│   │   ├── Functions/               API endpoints, background jobs, timers
│   │   ├── Parsing/                 Reads the task sheet PDFs
│   │   ├── Coloring/                Decides each room's colour
│   │   ├── Rhythm/                  Director's cleaning rhythm rules
│   │   ├── Runs/                    The upload-to-PDF process
│   │   ├── PdfMarking/              Draws the coloured dots
│   │   ├── Accounts/                Sign-in and sessions
│   │   ├── Audit/                   Logs of who did what
│   │   ├── Storage/                 File storage
│   │   ├── Data/                    Database connection and setup
│   │   └── Core/                    Hotels, rooms, colours, Berlin time
│   └── HousekeepingParser.Tests/    Automated tests
├── migrations/                      Database scripts (run automatically)
├── tools/                           Setup scripts
├── docker-compose.yml               Local database and storage
└── Dockerfile                       Production image
```

---

## ⚙️ Configuration

`tools/setup-local.sh` creates `src/HousekeepingParser/local.settings.json` for you. The most important settings:

| Setting | Description |
|---|---|
| `Database__ConnectionString` | Database connection |
| `Storage__ConnectionString` | File storage connection |
| `OpenDataLoader__JarPath` | Location of the PDF reader |
| `Auth__SeedUsers__*` | Demo accounts created on start-up |
| `Auth__SecureCookie` | Set to `true` outside local development |
| `Validation__EnforceSheetAge` | Only accept sheets printed in the last 24 h. `false` locally (the samples are old), **`true` in production** |

> 🔐 `local.settings.json` and `.env` contain secrets and are ignored by git.

---

## 🔄 How it works

Each upload starts one **run**. Both files are processed side by side:

| # | Step | What happens |
|:---:|---|---|
| 1 | 📅 **Rhythm** | Makes sure the Director has set a cleaning rhythm |
| 2 | 📖 **Read** | Extracts the text and positions from the PDF |
| 3 | ✅ **Check** | Right report type, known hotel, recent, complete, one sheet per hotel, no duplicates |
| 4 | 💾 **Save** | Stores rooms and totals; warns if the totals don't add up |
| 5 | 🎨 **Colour** | Gives every room a colour based on the rhythm and guest stay |
| 6 | 🖍️ **PDF** | Builds the report pages, then adds the original sheet with a coloured dot next to each room number |

### ⏰ Scheduled jobs (Berlin time)

| When | Job |
|---|---|
| 00:05 on the 1st of each month | Activates planned rhythm changes |
| 03:30 every day | Deletes old files, logs and expired sessions |

---

## 🌐 API endpoints

Base URL: `http://localhost:7071/api` · Sign-in uses a secure session cookie.

#### 🔑 Authentication
| Method | Endpoint | Description |
|---|---|---|
| `POST` | `/auth/login` | Sign in (locked for 2 min after 5 wrong tries) |
| `POST` | `/auth/logout` | Sign out |
| `GET` | `/auth/me` | Current user |

#### 📅 Cleaning rhythm
| Method | Endpoint | Who | Description |
|---|---|---|---|
| `GET` | `/rhythm` | All | Current and planned rhythm per hotel |
| `POST` | `/rhythm` | Director | Set or plan a rhythm (starts on the 1st of a month) |
| `DELETE` | `/rhythm/{hotelId}/scheduled` | Director | Cancel a planned change |
| `GET` | `/rhythm/history` | All | Rhythm change history |

#### 📄 Runs
| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/runs` | Last 100 runs |
| `POST` | `/runs` | Start a run (upload 2 PDFs + guest requests) |
| `GET` | `/runs/{id}` | Run progress, warnings, totals and room colours |
| `GET` | `/runs/{id}/pdf/{hotelId}` | Report + marked sheet PDF (add `?download=1` to download) |

#### ❤️ Health
| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/health` | Is the service up? |

> The data types are shared with the frontend in [`src/lib/types.ts`](https://github.com/jubayer17/HSK-Frontend/blob/main/src/lib/types.ts).

---

## 🧪 Tests

```bash
dotnet test
```

| Test group | Needs | Checks |
|---|---|---|
| Parser | – | Room counts and totals for all 4 sample sheets |
| Privacy | – | No staff names survive reading |
| Colour rules | – | Every rhythm and priority rule |
| PDF | – | Dots are drawn in the right place |
| Real reader | Sample PDFs + Java | Reading the real files end to end |
| Database | Docker | Full runs, rhythm rules, duplicate checks, data safety |

> ⏭️ Tests that need Docker or the sample PDFs are **skipped automatically** when those aren't available.
> The real-reader tests look for the sample PDFs in the folder **above** this repo.

---

## 🐳 Deployment

```bash
tools/get-opendataloader.sh       # download the PDF reader first
docker build -t hsk-backend .
```

The image contains .NET 8, Java 17 and the PDF reader. In Azure, **Azure SQL** and **Azure Storage** replace the local Docker services, and secrets come from **Key Vault**.

---

<details>
<summary><b>📝 Notes for developers</b> (click to expand)</summary>

<br>

- **PDF reader output.** OpenDataLoader returns text blocks with positions, not lines and words.
  `OdlLayoutMapper` converts them, and table rows are rebuilt from the positions.
- **Damaged sample files.** The four sample PDFs had their line endings changed during transfer, which breaks them.
  `PdfBytes.RepairLineEndings` fixes exactly that, and only when needed. Files straight from Opera should be fine.
- **PDF library.** The client prefers Syncfusion, but its licence isn't settled yet. Drawing goes through
  `IPdfMarker`, so PDFsharp can be swapped out later.
- **Sign-in.** Simple username and password for now (hashed with PBKDF2). Stage 2 moves to Microsoft Entra ID.
- **Not in this stage yet:** emails, follow-up bookings, automatic 06:55 start, day console, reports.

</details>

---

## 🔒 Privacy

Real task sheets contain **staff names**. Never commit them.
PDF files, `.env`, `local.settings.json` and the reader JAR are ignored by git. Test data uses fake names only.
