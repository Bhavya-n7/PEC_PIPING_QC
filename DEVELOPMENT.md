# PEC QC Assistant: development plan

Product: a Windows desktop assistant for the **QC team** of Petrocon Engineers & Consultants (PEC). A QC engineer types a request; **Claude** (through the user's own Claude Code sign-in) does the QC by calling **tools** in a local MCP server. QC is **read-only on models and drawings**: the tools find differences and write reports; people decide and sign off.
It is built the same way as the released PEC products (`PEC_Spoolgen_MCP`, `Smart3D-MCP` product track): WPF app + C# MCP server + Setup/auto-update + one shared office folder for skills, knowledge, tools, learning and users.

Reference documents in this repo:
- `QCTYPES.MD`: the three QC types (the requirement)
- `docs/reference/spoolgen-technical-architecture.md`: the architecture this product mirrors
- Reference product (separate repository on this PC): `Smart3D-MCP` (`docs\technical-architecture.md`, `docs\product-plan.md`, `src\Pec.Smart3D.*`)

How to read this file: `[x]` done and checked, `[~]` started, `[ ]` to do. **One phase at a time**: finish, test, update this file + `README.md` + `CHANGELOG.md`, run `scripts\leak-scan.ps1`, commit, push. Then the next phase.

---

## 1. The three QC types (from `QCTYPES.MD`)

| # | QC type | Checked | Against |
|---|---|---|---|
| 1 | **Piping isometric check** | isometric drawings (PDF) | Navisworks 3D model, Piping Material Specification (PMS), P&IDs, line list, vendor data, other project documents |
| 2 | **P&ID check** | redrawn P&ID (PDF) | the client's original P&ID (PDF): all lines, equipment, instruments, tags and notes copied accurately and completely |
| 3 | **GA drawing check** | general arrangement drawings (PDF) | Navisworks 3D model, P&IDs, vendor data, other project inputs |

What each QC type needs from the platform:

| Need | Iso | P&ID | GA |
|---|---|---|---|
| Read drawing PDFs (text + position) | x | x | x |
| Navisworks model data (headless export of AVEVA attributes and positions) | x | | x |
| Project inputs library: PMS, line list, P&IDs, vendor data (searchable, some structured) | x | | x |
| Compare drawing to drawing (tags, lines, notes) | | x | |
| Findings with iso/drawing value next to the source value and where it was found | x | x | x |
| Report (Excel + readable summary) saved with the project | x | x | x |

## 2. Decisions

| # | Decision | Status |
|---|---|---|
| D1 | **Same architecture as the other PEC products**: C# on .NET 10, WPF app, MCP server (ModelContextProtocol C# SDK), Setup + payload + `latest.json`, shared office folder, Claude Code under the user's own account (no API key). Code is **copied and renamed** (`Pec.Qc.*`), not shared as a package | decided (owner) |
| D2 | **Shared office folder** for everything shared: the QC share on the company server (`...\PIPEING_QC`), also holding `updates\`. The server path is **never committed**: it lives in the private `config.local.json` and is written into `office-defaults.json` by the release build | decided (owner) |
| D3 | **"Login" = identity**, as in the reference: name, employee code, company asked once at first start, stamped on every lesson, audit line and activity line. **Added for QC:** each user is also registered centrally in `<shared>\users\<code>.json` (role, first/last seen, version, machine). Claude sign-in stays the user's own. No passwords stored by the app. A licence layer is Phase 10 | assumption, confirm |
| D4 | **Roles** (from the registry): `qc-engineer` (run checks), `qc-lead` (+ approve lessons, requirements, reports), `admin` (+ users, updates, shared folder). Enforced by the tools, not only by the UI | assumption, confirm |
| D5 | **QC tools never modify** models, drawings or client documents. The approval gate guards what QC *does* write: shared skills, lessons, requirement packs, saved tools, published reports | decided (design) |
| D6 | **Navisworks**: read by a headless Navisworks Manage run (net48 helper built against the user's own installation, never redistributed). Education or commercial licence is the user's/company's responsibility; the product never checks or changes it | decided (as in reference) |
| D7 | **PDFs are read with the text layer and positions** (no OCR in v1). Scanned/raster drawings are reported as "cannot read", never guessed | decided |
| D8 | **Never invent client data or engineering rules**: unknown = REVIEW item. Every client rule carries its source (document, section, page) | decided (principle) |
| D9 | Earlier exploratory Python work is kept **locally** in `prototype/` (git-ignored, not pushed) as a behaviour reference for the iso reader and comparison; the product is written in C# | decided |
| D10 | Marked-up PDF output (red clouds on the drawing) needs a PDF writer with a usable licence; v1 delivers Excel + Markdown/PDF summary with page and position of each finding | open |

## 3. Architecture (one screen)

```
 PecQcAssistant.exe (WPF)  --starts-->  Claude Code CLI  <--stdio MCP-->  Pec.Qc.Mcp.exe (tools, ApprovalGate)
  chat, projects, findings,                (user's account)                   |            |           |
  approvals, settings, guide                                                  | PDF reader | Navisworks | shared office folder
         ^   approvals\<id>.pending/.approved                                 | (PdfPig)   | snapshot   | skills learning context
         |                                                                    |            | (net48     | documents requirements
         +--------------------------------------------------------------------+            |  helper)   | projects models reports
                                                                                                          | users activity audit updates
```

Deliverables (one solution `PEC_Qc.slnx`, one version in `Directory.Build.props`):

| Deliverable | Project | Target |
|---|---|---|
| `PecQcAssistant.exe` | `Pec.Qc.Desktop` | net10.0-windows, WPF |
| `mcp\Pec.Qc.Mcp.exe` | `Pec.Qc.Mcp` | net10.0 win-x64, single file, self-contained |
| `PecQcSetup.exe` + `payload.zip` | `Pec.Qc.Setup` | net10.0-windows, WPF |
| (library) | `Pec.Qc.Core` | net10.0: Claude CLI, approvals, knowledge, shared folder, identity, activity, setup/update, QC engines |
| `navis\Pec.Qc.NavisExport.dll` + runner | `Pec.Qc.NavisExport` | net48, references the user's Navisworks Manage |
| tests | `Pec.Qc.Tests` | xUnit, synthetic data only |

### Shared office folder (`<shared>` = the PIPEING_QC share)

| Folder | Content | Written by |
|---|---|---|
| `skills\<name>\SKILL.md` | how-to guides Claude reads on demand (one per QC type + platform skills) | admins, Claude after approval |
| `learning\` | lessons, corrections, decisions (Markdown, each with a source) | Claude (user approves) |
| `context\` | per-project state notes | Claude (user approves) |
| `documents\` | client procedures, standards, training | admins |
| `requirements\` | client rule packs and QC-type checklists (JSON, every rule with its source) | qc-lead |
| `projects\<project>.json` | project card: client, project no., model file, inputs folder, active rule packs | qc-lead, users via the app |
| `projects\<project>\inputs\` | PMS, line list, P&IDs, vendor data of that project | qc-lead |
| `models\<project>\` | Navisworks snapshots (JSON) so the team exports a model once | any user |
| `reports\<project>\` | QC reports (Excel + Markdown) | the assistant |
| `users\<code>.json` | central user registry: identity, role, first/last seen, version | the app (automatic) |
| `activity\yyyy-MM\` | prompts, answers, tool calls per person and day | the app (automatic) |
| `audit\yyyy-MM.jsonl` | every write tool: who, what, arguments hash, result | the tools (automatic) |
| `tools\` | custom tools (recipes of existing tools), C# tools | Claude (user approves) |
| `prompts\` | `master-prompt.md`, `quick-tasks.json` | admins |
| `commands\`, `agents\`, `contributions\` | shared kit (skills/commands/agents users propose, admin approves) | users, admins |
| `updates\` | `latest.json`, `PecQcSetup.exe`, `payload.zip` | release manager |

Rules: seed missing files only, never overwrite shared files (people edit them); `scripts\publish-to-shared.ps1` is the reviewed way to publish new bundled content; if the share is unreachable the app works from local copies.

---

## 4. Phases

### Phase 0: Plan and repository hygiene
- [x] Read the requirement (`QCTYPES.MD`) and the reference products
- [x] This plan, decisions D1-D10, shared-folder layout
- [x] Earlier prototype parked locally (`prototype/`, git-ignored); sample PDF/NWD stay git-ignored
- [x] `.gitignore` (client data, build output, `config.local.json`, `prototype/`)
- [x] `scripts\leak-scan.ps1`: blocks server names, share paths, IPs, user names before every push
- [x] Pushed to the remote
- Exit: plan reviewed by the owner

### Phase 1: Foundation (solution, Core, MCP skeleton, tests) - DONE
- [x] .NET 10 SDK available (per user, no admin; installed with the official install script into the user profile, user PATH updated)
- [x] `PEC_Qc.slnx`, `Directory.Build.props` (version 0.1.0), `Pec.Qc.Core`, `Pec.Qc.Mcp`, `Pec.Qc.Tests`
- [x] Core: the shared stack copied from the reference and renamed: Claude CLI + prompt builder + chat history/attachments, ApprovalGate, SafeWrite, Settings, UserIdentity, SharedHub, ActivityLog/AuditLog, LessonStore, KnowledgeStore, ManualIndex/Bm25/TrainingPack, KitSync, CustomTools, Updates, Office kit (Excel/Word/PDF + text reader), path diagnostics, Navisworks finder. Everything Smart 3D-specific left out (bridge, conversion, SQL, sites, model, APS, docs index)
- [x] QC replacements written: `ProjectCard` + `ProjectStore` (shared `projects\` cards, active project per user; replaces the Smart 3D site profile), QC system prompt, quick tasks for the three QC types, QC settings folders (`projects`, `models`, `reports`, `users`)
- [x] MCP server `pec-qc`, 34 tools: `qc_status`, `who_am_i`, `activity_report`, `guidance`, `recall`/`remember`/`forget`/`list_knowledge`, `learn_record`/`learn_search`/`learn_pull`, `read_training`/`training_overview`/`convert_training`, `custom_tools`/`run_custom_tool`/`create_custom_tool`/`save_skill`, `kit_*` (5), `excel_*` (4), `word_*` (2), `pdf_*` (4), `file_read`
- [x] Shared-folder probe tolerant of shares that allow create/overwrite/append but **not delete** (measured on the real share): one fixed probe file, project-card delete never throws
- [x] Tests green: 118 (approvals, activity, shared hub, kit, updates, training pack, chat history, attachments, office kit, plus new tests for project cards/store, settings folders, folder defaults, share without delete)
- [x] MCP server run over stdio by an MCP client (34 tools listed, status/identity/lessons/Excel round trip) **and** by a fresh Claude Code session calling `qc_status`
- Not in this phase (moved): the Smart 3D-specific `error_explain` and the C# tool store (Phase 9), `graphify` (dropped), installer tests and the in-app guide / WPF thread-guard tests (added with the projects they guard: Phase 3 and 4)
- Exit met: `dotnet build` 0 errors, `dotnet test` 118 passed, `qc_status` answers through Claude Code

### Phase 2: Shared office folder, identity and central users - DONE
- [x] QC folder layout created on the **real share** (README, `hub.json`, 17 folders) with `Pec.Qc.Mcp.exe init-shared [folder]` (new command for IT and the release manager; creates, never overwrites, never deletes). Layout checked by listing the share
- [x] `office-defaults.json` written from the private `config.local.json` by `scripts\make-office-defaults.ps1` (git-ignored output); `Settings.Load` applies it to empty settings only
- [x] `UserIdentity` + **central registry** `users\<code>.json` (`Pec.Qc.Core.Users`): role, first/last seen, version, machine, sessions; refreshed by `qc_status` and `who_am_i`; falls back to a local record when the share is away
- [x] Roles `qc-engineer` < `qc-lead` < `admin` **enforced by the tools** (`Resolve.Require`): `save_skill` needs lead; `kit_pending`, `kit_approve`, `set_user_role` need admin; `users_list` needs lead. Tools `users_list`, `set_user_role` (36 tools in all). An admin cannot demote themselves
- [x] Activity, audit, lessons and the user registry **verified on the real share** by an integration test (`RealShareTests`, runs only when `PEC_QC_SHARE_TEST` names a folder): two identities see each other's registry entries and lessons, both write activity to the shared log and read each other's lines, an audit line lands in `audit\`, a role change survives the person's next start, and an unreachable share falls back to this PC without an error. Run inside `<share>\_selftest` so the real registry and logs stay empty (files cannot be deleted there)
- [x] Seed content published to the real share with `scripts\publish-to-shared.ps1 -Apply` (report first; re-run is idempotent, 0 changes): `prompts\master-prompt.md`, `prompts\quick-tasks.json`, skills `qc-platform` and `qc-learning`. Seeding never overwrites; changed files are backed up as `.bak_<date-time>`
- [x] `scripts\publish-to-shared.ps1` (report first, `-Apply` copies, never deletes; folder from `config.local.json`)
- Verified: `dotnet build` 0 errors, `dotnet test` 130 passed + 2 real-share tests passed on the company share
- Left on the share by development (cannot be deleted by users, an admin should remove them): `.probe-6ab40e`, `.write-test` at the root, and the whole `_selftest\` folder
- **Decision needed (D4):** the first person to register when the registry has no admin becomes admin. On the real share nobody has registered yet, so **whoever starts the app first becomes the administrator**. The owner should be that person, or name the admins up front. Roles live in a plain file: give `users\` modify rights to administrators only on the share
- Moved to Phase 9: a later **sync** of activity and lessons written while offline (today they stay on that PC); the fallback itself is verified
- Exit met: two identities see each other's lessons and appear in the user registry on the real share

### Phase 3: Desktop app (WPF) - DONE (checked by UI automation, not yet by a person)
- [x] Shell ported from the reference and adapted (`Pec.Qc.Desktop`, `PecQcAssistant.exe`): theme, chat with markdown, collapsible tool steps, attachments, chat history, token display; the MCP server, skills, prompts and help travel with the app
- [x] First-run identity dialog (name, employee code, company); the user is listed in the registry at start; the first person on a shared folder is told they are its administrator (not shown when there is no shared folder or an admin already exists)
- [x] Settings rewritten for QC: shared folder and update source, "About you" with the role from the registry, autonomy and confirmations, Navisworks folder (found/not found), advanced folder overrides. The Smart 3D bridge and cloud-conversion cards are gone
- [x] Status lights: Claude, **Navisworks**, **Project inputs** (folder present, file count), shared folder
- [x] Approval window for shared saves (wording for QC), with the full protocol checked against the real MCP server
- [x] Projects view: QC project card dialog (client, project no., Navisworks model, inputs folder defaulting to `projects/<name>/inputs` on the share, rule packs, details); cards are shared; active project per user
- [x] Quick-task cards for the three QC types (each with a hint); Knowledge page (shared folder, skills, custom tools, lessons, documents, learning); Guide & FAQ page written for QC (help.md)
- [x] `StoredChat`, activity and audit rows say Project instead of Site; audit write no longer fails on a share that denies a write; the registry counts a session per app start, not per tool call
- Checked in the live app by Windows UI Automation (screenshots reviewed): first-run dialog, main window, all four lights, Add project (local and on the shared `_selftest` folder: card and default inputs folder created, user registered), a **real chat turn** through Claude Code and the MCP server (answer, token readout, activity log prompt/tool/result/answer/done), the approval window (pending, approve, save succeeded, later calls in the task pass), Knowledge page (6 cards), Guide (14 contents entries), Settings (role admin, Navisworks found)
- Tests: 136 passed (+2 real-share tests skipped); guide and WPF thread-guard tests restored for the app; no text in the app mentions the product it was built from
- **Not yet checked** (by automation or by a person): Edit/Delete/Share buttons on project cards, the quick-task question dialog, reopening a chat from history, attachments (drag, paste), the update bar, Sign in/out with Claude, high-DPI and small windows, a clean PC
- Exit (open): a person clicks through first run, chat, approval and a project card on a clean PC (done together with the Setup test in Phase 4)

### Phase 4: Setup, packaging and auto-update - DONE (verified on the development PC, not on a clean PC)
- [x] `PecQcSetup.exe` + `payload.zip` (self-contained, no .NET or Python needed): `Pec.Qc.Setup` window with readiness checks (Claude Code required, Navisworks and the shared folder reported), install location, shared folder pre-filled from `office-defaults.json`, options, progress log
- [x] `Installer` in Core: per-user install without administrator rights, registration with **Claude Code** (user scope, replaces an older entry) and **Claude Desktop** (config merged, backed up first, other servers kept, invalid JSON left alone), Start-menu shortcut, uninstall entry, manifest, uninstall that undoes exactly what was done
- [x] Safety fixes found while porting: a skill of the same name in the user's `.claude/skills` is never replaced or removed (checked against two real skills that were already there: byte-identical afterwards); path-safe folder copy; a Claude Desktop config that only existed because of us is deleted with it; **uninstall now closes a running assistant first** (found live: it had left 66 locked files; test added that fails without the fix)
- [x] Update: `latest.json` read from the update source (`<shared>/updates` by default), **Update now** bar and **automatic update** once per version (Setup `/auto`); an update keeps the choices of the first install (nothing new registered or copied); the uninstaller copies itself to a temp folder so nothing is left behind
- [x] `scripts/build-installer.ps1` (finds a per-user SDK, `-Version` override, release notes from the matching `## <version>` section of CHANGELOG) and `scripts/publish-release.ps1` (report first, `-Apply`, an older version never replaces a newer one, `latest.json` written last, nothing deleted); `PEC_QC_APPDIR` moves only the install folder (used for the tests)
- [ ] Channels stable/beta: not built (one channel; a second update folder can be set as the update source)
- Verified live on this PC (UI automation, real Claude Code and registry, install folder and data folder redirected): install from the 0.1.0 package (office defaults pre-filled, both registrations made, `claude mcp list` shows pec-qc connected, shortcut, uninstall entry), **automatic update 0.1.0 -> 0.1.1** from a test updates folder on the share (app closed itself, Setup ran silently, 0.1.1 started from the same folder, project and identity kept, manifest and registry say 0.1.1, registrations not duplicated), the version guard refusing 0.1.0 over 0.1.1, then **uninstall while the app was running**: install folder fully removed and the machine state identical to before the install (Claude Code, Claude Desktop, shortcut, registry, skills)
- Tests: 155 passed (+2 real-share tests skipped): installer (install, upgrade, both registrations, failures, skill guard, uninstall, running-app close, readiness, Claude Code installer), update JSON round trip, release notes present for the current version, Setup thread guard
- **Not verified:** a truly clean PC (no .NET, no Claude Code, no Navisworks); Setup installing Claude Code with the official installer (tested with a fake only); the manual **Update now** button and the **What's new** box (the automatic path was run); the Setup window when `payload.zip` is on a slow share; code signing (Phase 10)
- **Not done on purpose:** publishing 0.1.0 to the real `updates` folder of the company share: it makes the installer available to the whole office, so it waits for the owner. Test builds are in `_selftest/updates` (about 250 MB; an admin should delete `_selftest`)
- Exit (partly open): install, first run, update and uninstall work end to end on the development PC; the clean-PC run and a person clicking through (also the Phase 3 exit) remain

### Phase 5: QC knowledge, project inputs and skills - DONE (on synthetic data; no client sample yet)
- [x] **Project inputs library**: the project's inputs folder (standard sub-folders pms, line-list, pid, vendor, other; names with "PMS", "line list", "P&ID", "vendor" are recognised outside them) with an inventory by kind, coverage per QC type (what each check still needs), and `EnsureLayout` for a folder the assistant makes itself. Backup, temp and hidden files are ignored; a path that leaves the folder is never read
- [x] **Search with a source**: incremental BM25 index per project (PDF pages with table rows kept on one line, Excel rows with their column names, Word / PowerPoint / CSV / text), refreshed only for new or changed files; every hit says file and where (page, or sheet and rows). Line numbers and tags are searchable whole or by a part (`B400A`, `60A07900`): fixed in `Bm25`. Scans and DWG are reported (`cannotBeRead`), never skipped silently. 300 PDF pages indexed in about 2.6 s, later searches 0.2 s
- [x] **Line list as data**: header found below title rows, columns mapped from many spellings with units from the header (psig, degF...), unknown columns kept as extras, duplicates and unreadable sheets reported; lookup by any spelling of the line number, or a part of it, with file / sheet / row
- [x] **Requirement packs**: JSON per client in `requirements/`; every rule must carry a source (document and section or page) or the save is refused; pattern rules are tested by `requirements_check`, manual rules go to the person; a candidate rule can only ever produce REVIEW; save needs the qc-lead role, an approval, a diff and a backup
- [x] **Findings model** shared by the three QC types (`Finding`, `FindingJson`, `QcReport`): a FAIL needs both values and the source document or it is refused, no findings is "NOT CHECKED", never OK; Excel + Markdown report written to `reports/<project>/`, never over an older report
- [x] **Checklists** per QC type (13 isometric, 7 P&ID, 7 GA items, from QCTYPES.MD); the QC lead can replace them with `qc-checklists.json` in the shared requirements folder
- [x] **Skills** `qc-project-inputs`, `qc-findings-and-reports`, `qc-iso`, `qc-pid`, `qc-ga`, `qc-troubleshoot` (plus the two platform skills); master prompt, core prompt and quick tasks (now 11: line lookup, checklist) updated; a test fails if any skill or prompt names a tool that does not exist
- [x] **12 new MCP tools** (46 in all): `inputs_list`, `inputs_search`, `inputs_read`, `line_list_summary`, `line_list_lookup`, `client_requirements`, `save_requirements`, `requirements_check`, `qc_checklist`, `qc_report_write`; `qc_status` now reports the inputs and what each QC type lacks. App: Knowledge page "Project inputs" card; a new project's inputs folder gets the standard sub-folders
- [x] New skills, master prompt and quick tasks published to the real share (report first; the two changed files were byte-identical to the Phase 2 seed, old versions kept as `.bak_`)
- Verified: 234 tests pass (+2 skipped), 79 of them new (classification, inventory and path guard, search with page / sheet / rows, incremental refresh, a scan reported, line list spellings, requirement validation and checks, findings validation, report files, checklists, skills and prompts); **mutation checks**: breaking tag splitting, the path guard, the FAIL-needs-a-source rule, and a skill naming a fake tool each made tests fail. All 10 tools exercised over stdio on a synthetic project built with the product's own tools, including both refusals. **Exit checked in the real app with a real Claude turn**: "what does the PMS say for class B400A at 2 inch?" -> searched, read the full page, answered with the pipe and gate valve rows and the source (file, page 2 of 3, class section), and said that a scanned file could not be searched
- **Not verified:** a real client's PMS, line list or P&ID (everything is synthetic, so unusual layouts, merged cells or multi-line headers are untested); line list columns the reader does not know; PMS tables as structured data (see Phase 6); very large PDFs beyond about 400 pages; the Knowledge card clicked by a person
- Exit met: Claude answers a PMS question with the document and page

### Phase 6: Piping isometric QC
- [ ] `Pec.Qc.NavisExport` (net48): headless Navisworks Manage export of PIPE/BRAN/component attributes to a snapshot; tree path, duplicate and federated-model handling
- [ ] C# iso PDF reader (title block, BOM, cut list, coordinates, supports, continuations)
- [ ] Checks: identity, title block vs model, BOM vs model, coordinates, lengths, plus line list, PMS (class, rating, material), P&ID (line existence, from/to) and vendor data
- [ ] Structured PMS: the class sheets as data (size ranges, rating, material, schedule) for the rating / material checks. Needs a real client PMS sample: its layout is not guessed (moved here from Phase 5)
- [ ] Tools: `model_export`, `model_summary`, `iso_read`, `iso_check`, `iso_check_folder`; Excel + Markdown report to `reports\<project>\`
- Exit: on a real project: errors found match what the QC team found by hand (measured), false alarms listed

### Phase 7: P&ID QC (redrawn vs original)
- [ ] P&ID reader: line numbers, equipment tags, instrument tags, notes, valves with positions
- [ ] Drawing-to-drawing compare: missing, extra, changed (text and tag), moved beyond tolerance; sheet and zone reference
- [ ] Tools: `pid_read`, `pid_compare`, report
- Exit: measured against hand QC on real redrawn P&IDs

### Phase 8: GA drawing QC
- [ ] GA reader: equipment tags, dimensions, elevations, coordinates, north arrow, notes
- [ ] Compare to the Navisworks model (equipment positions and sizes), P&IDs (tags) and vendor data (dimensions, nozzles)
- [ ] Tools: `ga_read`, `ga_check`, report
- Exit: measured against hand QC on real GA drawings

### Phase 9: Self-learning and the shared kit
- [ ] Offline queue: activity and lessons written while the share is away are synced back later
- [ ] `learn_record` / `learn_search` / `learn_pull` in the app; curator screen (approve/reject, strong-correction flag) -> `LEARNED.md`
- [ ] Automatic reflection turn after errors or corrections; saved C# tools synced across PCs
- [ ] Shared kit (skills, commands, agents) publish/approve/pull with privacy scan
- [ ] Activity report screen (per person/day/type)
- Exit: a correction on PC A is applied by Claude on PC B after curation

### Phase 10: Hardening and licence
- [ ] Licence client (seat, expiry, feature flags = roles, offline grace) against the company licence service
- [ ] Code signing; antivirus/SmartScreen check
- [ ] Security review (secrets, share permissions, redaction option); runbook, quick start, training video
- Exit: security sign-off

### Phase 11: Pilot and office rollout
- [ ] Pilot with a few QC engineers on a live project; metrics: install success, findings accepted vs rejected, time saved per iso
- [ ] Waves through the shared installer; support hours
- Exit: QC team on the stable channel

---

## 5. Working agreement (every phase)
1. Update this file (tick boxes honestly: built **and** checked), `README.md` and `CHANGELOG.md`.
2. `dotnet build` 0 errors, `dotnet test` green.
3. `powershell -File scripts\leak-scan.ps1` clean (no server names, share paths, IPs, Windows user names, client data).
4. Commit with a clear message and push. Never commit client drawings, models, snapshots, reports or `config.local.json`.
5. Be honest about what is verified and what is inferred.

## 6. Risks
| Risk | Mitigation |
|---|---|
| Iso/P&ID/GA templates differ per client | readers report what they could not parse (REVIEW), not guesses; templates as data where possible; add samples per client |
| The model lacks data the drawing shows (part numbers, welds) | listed as "not verifiable", never failed |
| Navisworks licence (education vs commercial) | D6; the guide says it is the company's responsibility |
| Share permissions (create but no delete seen on the QC share) | code never relies on deleting; append-only logs; tested on the real share |
| A wrong lesson spreads to everyone | human approval, revocable lessons, strong-correction flag |
| Scanned (image-only) drawings | detected and reported as not readable (D7) |
| Single developer | architecture mirrors existing products; tests; this plan |
