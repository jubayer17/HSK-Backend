# Backend change log

## 28 October 2026 — Fix false "Task Sheet Report is missing" rejection

### What failed and why

The rejected run `53a43e93-1b6a-48a2-9e3e-34055cd841bc` contained genuine Opera titles.
Opera places the print time about 5.3 PDF points above the title. The new .NET reader
correctly returned separate lines, but header validation still expected an adjacent
hotel line followed by `Task Sheet Report HH:mm` on one line. It therefore rejected
both files even though their 103 and 74 room rows had been read.
The earlier real-PDF regression tests checked room fields and totals but missed
header recognition; this gap is now covered explicitly.

### Changed files and reasons

| File | Change and reason |
|---|---|
| `src/HousekeepingParser/Parsing/TaskSheetParser.cs` | Recognise the exact title within the first page's header area independently of line order. Read a single nearby right-hand print time when it occupies a separate line. Preserve combined-line support, reject ambiguous/misplaced times and handle invalid clock values without throwing. Keep room-row grouping unchanged. |
| `src/HousekeepingParser.Tests/RealReaderTests.cs` | Assert recognition and the complete hotel/date/time header for both Stream and forced PdfPig fallback on all four available private PDFs. |
| `src/HousekeepingParser.Tests/HeaderParsingTests.cs` | Add eight cases for split and combined headers, missing/invalid/unrelated/ambiguous times, and a title mentioned only in another document's body. |
| `src/HousekeepingParser.Tests/RealPdfPipelineTests.cs` | Exercise actual Opera bytes through read, validation, SQL persistence, colouring and marked-PDF creation in an isolated test database. Explicitly skip when private files or Docker are unavailable. |

### Verification and runtime

- Before the fix, the strengthened real-reader checks reproduced **8 failures**.
- After the fix, focused header/reader checks: **16 passed, 0 failed, 1 skipped**.
- Full backend suite: **173 passed, 0 failed, 1 skipped** (174 total).
  The skip is still the missing mandatory October PDF and independent expected fields.
- The real-PDF pipeline test passed for the 30 August Novotel/ibis pair, with 112/116
  rooms, all steps done, and both marked downloads available. Test data used an
  isolated database and memory storage; no user's run was created or changed.
- Read the two exact rejected blobs again without modifying storage. Both now have
  recognised titles, complete Novotel headers, and no invalid data pages. Their
  contents matched the existing private samples by SHA-256.
- Restarted the local backend from the newly built Debug output, preserving its
  existing local settings. The previous failed run remains historical evidence.
- No frontend source was changed for this fix. Existing duplicate/same-hotel checks
  remain active: the user's two rejected files are both Novotel, dated 10 July and
  28 August. A new upload should select one task sheet per hotel.

## 27 October 2026 — Final Phase 1 decision

Requested by the user: implement the reviewed changes, add comments explaining why, and track backend/frontend separately.
No commit, push, Azure deployment or real email send is implied by this log.

### Changed files and reasons

| File | Change and reason |
|---|---|
| `src/HousekeepingParser/HousekeepingParser.csproj` | Add Camelot, compatible PdfPig and ACS Email packages. Pin Camelot alpha002 + PdfPig 0.1.5 because newer PdfPig removed the DlaOptions API. |
| `src/HousekeepingParser/Parsing/CompliantOpenDataLoaderExtractor.cs` | Removed the JVM subprocess reader to follow the .NET-only decision. |
| `src/HousekeepingParser/Parsing/PdfExtractor.cs` | New vendor-independent extractor contract, settings, exceptions and audit types. |
| `src/HousekeepingParser/Parsing/DotNetPdfExtractor.cs` | New in-memory Stream parser with page-level PdfPig fallback, processing budget, serialized extraction and metadata-only quality logs. |
| `src/HousekeepingParser/Parsing/PdfPigLayoutMapper.cs` | New mapping from real word coordinates and Stream row bands; preserve header/footer text and PDF marker positions. |
| `src/HousekeepingParser/Parsing/OdlLayoutMapper.cs` | Removed from production; moved to a test-only legacy fixture mapper. |
| `src/HousekeepingParser/Parsing/PdfModels.cs` | Add per-page quality diagnostics and vertical word centres. Scores describe extraction, not verified field correctness. |
| `src/HousekeepingParser/Parsing/TaskSheetModels.cs` | Represent invalid data-page layouts separately from non-blocking warnings. |
| `src/HousekeepingParser/Parsing/TaskSheetParser.cs` | Detect room-number covers without dropping valid first pages; default omitted children; normalize GLCL; validate wrapped data headings. |
| `src/HousekeepingParser/Program.cs` | Register .NET reader and its settings in place of Java. |
| `src/HousekeepingParser/Runs/MorningRunPipeline.cs` | Reject invalid layouts through existing failed-pdf handling; warn on departure/credit totals; use generic reader errors; remove Liegezimmer-to-Waiting link. |
| `src/HousekeepingParser/Coloring/ColorRuleService.cs` | Room moves now run before special requests and remain Red, including when no cleaner is available. |
| `src/HousekeepingParser/Operations/RoomMoveService.cs` | Explain why move day and cleaning day stay separate and how daily close uses the pending move. |
| `migrations/0005_break_minutes.sql` | Add authoritative decimal break minutes and backfill existing cleaner hours without rounding loss. Retain old column for compatibility. Do not rewrite an applied migration. |
| `src/HousekeepingParser/Operations/DailyCloseService.cs` | Convert hours-based API to minute storage; include uncleaned move-outs once in Liegezimmer for draft/new closes and save them. Preserve confirmed snapshots. |
| `src/HousekeepingParser/Operations/WeeklyBillingService.cs` | New shared signed daily arithmetic and Monday–Sunday grouping. Filter monthly dates before grouping to avoid double billing cross-month weeks. |
| `src/HousekeepingParser/Operations/DailyCloseMath.cs` | Use the shared billing arithmetic rather than a separate formula. |
| `src/HousekeepingParser/Operations/DashboardService.cs` | Use shared arithmetic and weekly aggregation for period billable-check totals. |
| `src/HousekeepingParser/Reports/ReportService.cs` | Build monthly billable checks from weekly totals; expose weekly ledger; recognize Azure as live email. |
| `src/HousekeepingParser/Functions/Contracts.cs` | Add weekly totals to the monthly report API response. |
| `src/HousekeepingParser/Functions/OperationsFunctions.cs` | Map weekly totals into the API while preserving existing report endpoints and send triggers. |
| `src/HousekeepingParser/Reports/EmailSender.cs` | Add ACS SDK sending, PDF attachments and provider-operation status checking. Explicit Azure mode never silently falls back to outbox. |
| `src/HousekeepingParser/local.settings.example.json` | Replace Java settings with .NET reader settings and local-safe email placeholders. |
| `Dockerfile` | Remove Java installation, JAR copying and JVM settings. |
| `.dockerignore` | Exclude local secrets, real hotel PDFs and stale build output from Docker build layers. |
| `tools/get-opendataloader.sh` | Retired; the runtime reader now restores through NuGet. |
| `tools/setup-local.sh` | Remove Java download/configuration from existing setup. |
| `tools/Setup-Local.ps1` | Add Windows-native setup that preserves existing configuration and does not print generated credentials. |
| `infra/main.bicep` | Add Linux Premium EP1 deployment configuration, managed-identity Storage permissions, Key Vault references and optional ACR pull access. |
| `infra/README.md` | Document required existing Azure resources, deployment preview and live-verification limits. |
| `tools/Test-AzureHosting.ps1` | Add read-only verification of live Linux EP1 and HTTPS settings. |
| `samples/opera/README.md` | Create requested sample folder and explain the mandatory missing October fixture and independent expected fields. |
| `.gitignore` | Keep private expected-result manifests out of Git, alongside already ignored PDFs. |
| `src/HousekeepingParser.Tests/RealReaderTests.cs` | Repair outdated server/JAR paths; test Stream and forced fallback on the four available private PDFs; add explicit October acceptance gate. |
| `src/HousekeepingParser.Tests/Fixtures.cs` | Use the test-only mapper for historical anonymized JSON. |
| `src/HousekeepingParser.Tests/LegacyFixtureMapper.cs` | Preserve old regression snapshots without shipping an OpenDataLoader dependency in production. |
| `src/HousekeepingParser.Tests/LayoutMapperTests.cs` | Point legacy snapshot checks to the test-only mapper. |
| `src/HousekeepingParser.Tests/TestSupport.cs` | Replace Java-specific test exceptions with generic extraction errors. |
| `src/HousekeepingParser.Tests/Phase1DecisionTests.cs` | Add edge-case, layout, total-warning, room-priority and weekly/month-boundary regression tests. |
| `src/HousekeepingParser.Tests/Phase1DatabaseTests.cs` | Add SQL integration checks for break minutes, automatic Liegezimmer and separation from Waiting. |
| `src/HousekeepingParser.Tests/AzureEmailTests.cs` | Exercise SDK HTTP serialization, PDF attachments and operation polling with a fake HTTP service; test missing-config failure. No real email is sent. |
| `src/HousekeepingParser.Tests/DatabaseTests.cs` | Fix two pre-existing stale tests: migration count and operational-name allowlist. Keep imported-PDF privacy protections. Expect the newly required departure warning. |
| `tools/Test-FinalSample.ps1` | Strict acceptance fails when the mandatory October PDF/expected fields are unavailable. |
| `README.md` | Update setup, parser limitations, weekly billing, ACS, hosting and acceptance instructions. |

### Local-only changes (ignored by Git)

- Copied the four supplied July/August Opera PDFs into `samples/opera` for local regression checks.
- Removed old Java options from `src/HousekeepingParser/local.settings.json`; preserved SQL/storage settings, users and local Outbox mode.
- No credentials or real task-sheet PDFs are included in this change log or committed source.

### Billing agreement

The user confirmed signed daily balances, grouped Monday–Sunday and added into monthly totals.
Example: 23 checks minus 30 allowance remains -7. Daily flat-rate flags determine allowance days.
Only confirmed days are billed. Cross-month weeks contribute only the selected month's dates.
No holiday or deficit-carry-forward policy was invented.

### Verification

- Baseline: 138 passed, 2 failed (outdated tests), 2 skipped.
- New reader regression: 8 passed across four real PDFs, covering Stream and forced PdfPig fallback.
- EP1 Bicep template compiled locally; live Azure deployment is not verified.
- Full backend suite: **164 passed, 0 failed, 1 explicitly skipped** (165 total). The skip is the missing October acceptance input.
- The strict final-sample command was run separately and correctly failed for the missing required PDF.
- Release `dotnet publish`: passed.
- `git diff --check`, PowerShell script syntax checks and Bicep compilation: passed.
- Local runtime restarted from the updated release publish. Health, login, current user, weekly dashboard, monthly API and settings passed through the frontend proxy.
- Local email transport remains `outbox`; no real message was sent.
- Docker image build: **not verified**. Microsoft SDK base-layer download stalled, then failed with `lookup mcr.microsoft.com: no such host` in Docker Desktop. The source Dockerfile/context changes remain reviewable; retry when registry DNS/network access works.
- Every changed/new source and configuration file was checked against this log; all were listed.

### Outstanding inputs and limits

- Exact `task_sheet_report_1914628800.PDF` and independently reviewed expected fields are absent. October acceptance is pending, not passed.
- The PDF repeats 56 rooms but says 84 once; strict acceptance currently follows 28 + 28 = 56 and summary 109.
- Camelot is an archived prerelease. Compatibility is pinned and fallback tested; the October PoC remains a production gate.
- No Azure account/CLI was available: EP1 is configured in code, not provisioned or proven live.
- ACS SDK tests use fake HTTP responses. Verified domain, credentials, intended recipients and real mailbox delivery remain to be configured/tested.
- Existing per-guest frozen cleaning rhythms were not changed: the user requested the ten-item change list; the separate existing-guest policy remains unresolved.
