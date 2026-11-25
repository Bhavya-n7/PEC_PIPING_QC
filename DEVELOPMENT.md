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

### Phase 6: Piping isometric QC - DONE (checked on the one real isometric and its model; no second project yet)
- [x] **Navisworks export** (`Pec.Qc.NavisExport` plugin + `Pec.Qc.NavisRunner`, both .NET Framework 4.8, built against the user's own Navisworks, never redistributed): a hidden Navisworks opens a COPY of the model, the plugin writes every PIPE / BRAN / component with its AVEVA attributes and tree path, nozzles included; nothing is saved or changed. Built into the package as `navis\` by `build-installer.ps1` when the build PC has Navisworks. Real model (13 MB, 76,095 items): 30 s, 303 lines, 284 nozzles, 67 duplicated pipes (same names in appended files)
- [x] **Model snapshots shared**: `models\<project>\<model>.model.json` on the shared folder, an earlier snapshot of the same model is kept (stamped), the export goes to a local temp file first and is validated (no pipes = nothing saved). Tools `model_export` (approval), `model_summary` (stale warning when the model file is newer), `model_line`
- [x] **Isometric reader in C#** (`IsoReader`, `PdfTextLines`): lines rebuilt from letter positions (the sample draws one character at a time), positions normalised to the A3 frame; title block, BOM (fabrication / erection, category, item, size, quantity), cut pipe lengths, E/N/U coordinate labels with CONT. connections, sheet n of N, revision, CONT. DRG links; a page without text is reported as unreadable. **Compared field by field with the Python prototype on the real 3-sheet isometric: 0 differences**. Tool `iso_read`
- [x] **Checks** (`IsoChecker`): identity (line in model, duplicates, sheets 1..n, one revision, links, class and size in the line number), title block vs model (units converted, tolerances), BOM vs model components by type and size, coordinates vs model points and connected lines, cut table vs BOM vs model pipe length; **line list** (with units; an unknown unit is a REVIEW), **PMS class** (looked up, quoted with page), **P&ID** (line on the P&ID named in the title block). Every FAIL carries the drawing value, the source value and the source (a test asserts this for every FAIL)
- [x] Tools `iso_check` (report saved with approval) and `iso_check_folder`; tolerances and the support map in `iso-rules.json` in the shared requirements folder; skill `qc-iso` rewritten around the real tools
- Verified: 284 tests pass, 4 skipped (new: units, snapshot, store, reader, 28 checker cases including each kind of wrong drawing, line list / PMS / P&ID evidence, rules file); **mutation check** (a quantity FAIL downgraded to REVIEW failed 2 tests); two real-file tests behind `PEC_QC_SAMPLES`. **On the real files**: the genuine isometric gives 0 FAIL and 2 REVIEW (the model's test pressure is 300 on the branch but 10 on the pipe; operating pressure blank in the model: both real model inconsistencies); the 5 deliberately corrupted copies (coordinate, class, pipe quantity, tee quantity, test pressure) each give the specific FAIL. `model_export` and `iso_check` driven through MCP stdio
- **Found and fixed while testing**: supports compared against a model with no support map gave a false FAIL (now REVIEW + total compared); "CALC NO :" read as ":" when the value sits close
- **Not verified:** a second project or client (the reader is tuned to the layout of the sample isometrics; a different template may give warnings or REVIEWs); exported model from a different AVEVA / Navisworks version; the support map (the real one must come from the client's support collection, nothing is guessed); false-alarm and miss rates on a real batch (only 1 genuine drawing + 5 corrupted copies); the Navisworks export from the installed app on another PC (built and run from the source tree here); Navisworks needs a full user environment to start (a stripped environment failed to start it: the app passes its own)
- **Not built (moved on):** structured PMS class sheets (rating / material / schedule against the BOM): needs a real client PMS sample, its layout is not guessed. P&ID from / to end points and vendor face-to-face: reported as not verified
- Exit: on the real project the corrupted drawings are found and the genuine one is clean; the measured agreement with the QC team's hand check still needs a real batch from the team

### Phase 7: P&ID QC (redrawn vs original) - DONE on synthetic drawings only (no real P&ID was available)
- [x] **P&ID reader** (`PidReader`): the text layer into line numbers, tags (equipment, instruments, valves; an instrument bubble with the letters over the number is joined: PT / 1001 = PT-1001), short labels and notes, each with its zone (A1..H6) on a page normalised to the A3 frame. Reads horizontal AND rotated text (line numbers run along pipes). The same extraction runs on both drawings, so what is compared is consistent; everything that is text lands in one of the four kinds
- [x] **Compare** (`PidComparer`): sheet count; every line number, tag, label and note of the original must be on the redrawn drawing. FAIL: missing, changed (one changed item, not a missing plus an extra: both texts, look-alike characters such as 0/O named, works even when the changed text no longer looks like a tag), a lower count, a note reworded or incomplete (the missing words named). REVIEW: only on the redrawn drawing, moved beyond the tolerance (default 30 pt of A3, so a different page size does not matter), a sheet with no text. INFO: a note with different line breaks; **the graphics (symbols, routing, flow, line types) are always listed as not verified**
- [x] Tools `pid_read`, `pid_compare` (finds the original in the project's pid inputs by file name, or `original`; report saved with approval, type pid); skill `qc-pid` rewritten around them
- Verified: 15 new tests (reading incl. rotated text and bubbles, an identical copy is clean, each kind of copying error, a different page size, tolerance, unreadable sheet, every FAIL has both texts and the source); **mutation check** (missing-item FAIL downgraded: 3 tests fail); both tools driven over MCP stdio on synthetic drawings (original auto-found, a retyped 0/O tag and a forgotten valve both found, report saved)
- **Not verified:** any real redrawn P&ID and its original. The extraction is tuned on drawings I generated, not on a CAD-exported P&ID: real ones may split tags or line numbers differently, hold text as graphics, or number tags in patterns the reader does not expect. Hand-QC agreement (the exit measure) is not measured. Symbols and routing are never compared; a P&ID drawn as vector outlines or scanned is not readable
- Exit: pending a real redrawn / original pair from the QC team to measure against their hand check

### Phase 8: GA drawing QC - DONE against the real model's equipment; no real GA drawing was available
- [x] **Equipment in the model snapshot**: the exporter now writes every EQUI with its Navisworks bounding box (mm, same world coordinates as the pipes) and a count of every item type. Real model: 307 equipment records (30 process items such as `/60D302_CONDENSATE_POT`, the rest electrical / instrument items), all with a box. **Finding from the real data**: the AVEVA `POS` attribute of equipment is unreliable (values like `E 4826316 mm` for items that sit at E 1,574,000), so position and size come from the bounding box and POS is used as the origin only when it lies inside the box. A snapshot exported before this version has no equipment: `model_summary` warns and `ga_check` asks for a new export
- [x] **GA reader** (`GaReader`): tags (plant-code form 60D302 / 60-D-302 and letter-hyphen form V-201), E/N/U coordinate labels (stacked feet-inches, or one line in mm), elevations (`EL +37378`, metres, feet-inches), scale, revision, north arrow as text, number of notes; text-like tokens one character from a model tag are kept for the typo check
- [x] **Checks** (`GaChecker`): tag against model equipment (any spelling; a tag or text one character away from a model tag is a FAIL with both), coordinate against the equipment's extent (outside plus 100 mm: FAIL with the distance, both values and the extent), elevation against the height range (outside: REVIEW, what it refers to is unknown), tags against the P&IDs and the vendor data of the project inputs (found with file and page, or REVIEW), completeness (scale, north arrow, revision). **Not checked and always listed**: dimensions, equipment sizes, nozzle positions and orientations against the vendor data, clearances
- [x] Tools `ga_read`, `ga_check` (report with approval, type ga); `model_summary` and `model_export` report the equipment count; skill `qc-ga` rewritten
- Verified: 16 new tests (equipment parse and box rules, reading, each kind of wrong drawing, tolerances, P&ID / vendor presence, an old snapshot) + 1 test on the real model behind `PEC_QC_SAMPLES`; **mutation check** (coordinate FAIL downgraded: 2 tests fail). **On the real model**: GA drawings generated from 3 real equipment items with their real tags and coordinates (feet-inches, rounded to 1/16 in): the correct one has 0 FAIL / 0 REVIEW; a retyped tag (60D3O2) fails; a 1 m shifted coordinate fails with the real distances (198 mm and 399 mm outside); wrong elevations are REVIEWs. Driven through MCP stdio
- **Found and fixed while testing**: a retyped tag that no longer looks like a tag was silently ignored (the drawing came out OK); `EL +37378` was not read because of the plus sign
- **Not verified:** any real client GA drawing (the reader is tuned on drawings I generated from real model data; a real template may print tags, coordinates and elevations differently, or draw them as graphics); agreement with hand QC (the exit measure); what a real GA's elevations refer to; model equipment that is missing from the drawing (the area a drawing covers is not known, so it is not reported); equipment whose box is large (a wide box makes a small coordinate error invisible, e.g. 60F301A is 7.5 m deep)
- [x] **Real client GA sample received** (`1-GH5100-28-GAS-4297-00_00.pdf`: 8 sheets, A0, a piping plan per tier, 474 line numbers). It is a different kind of GA from the one I had assumed, so the reader was extended for it: line numbers with size and elevation (all 474 paired with their `EL .. m`; the count per sheet equals an independent extraction by another PDF library: 85, 48, 61, 43, 34, 64, 111, 28), drawing references, matchlines, tier elevation, plan range; consolidated completeness findings. New checks on line numbers: against the model's pipes (size vs bore), the line list (size in inches) and the P&ID text, and the continuation drawings against the inputs. 11 new tests + 1 on the real drawing behind `PEC_QC_SAMPLES`
- **What the real drawing showed that synthetic data could not**: the same area-seq-fluid number appears with several sizes in 44 places (this client's convention for branches) and 46 of 474 elevations lie outside their sheet's plan range, so a "size inconsistent" or "elevation out of range" check would have produced false alarms and was NOT built. The source-generated regex (a .NET quirk) did not match the real text that the same pattern matches at run time: replaced by runtime regexes. The Navisworks runner copied every model to `model.nwd`, so an `.nwc` could not be opened: it now keeps the extension
- **Still missing for this sample**: the NWC next to it was exported from the DXF (10,123 geometry items, no AVEVA data), so it is not a model the drawing can be checked against; no line list, P&ID or AVEVA snapshot of GH5100 was provided. On the real drawing `ga_check` therefore ran with the Dehydration-package model: 3 REVIEWs (north arrow and revision not text, none of the 264 distinct line numbers is in that model: correct, wrong project), 0 FAIL, 4.5 s. Whether the checks find real drafting errors on this drawing is NOT measured
- Exit: pending a real GA drawing and the QC team's hand-check results

### Phase 9: Self-learning and the shared kit - DONE (the reference gave much of it; gaps closed and verified here)
- [x] **Came across from the reference and now checked in this product** (Phase 1): lesson record / search / pull with secret scrubbing, the reflection turn after an error or a correction (settings default on; corrections detected by wording), kit publish / pending / approve / pull with the privacy scan (admin approves, nothing overwrites a user's own edit), shared custom tools synced both ways, activity log
- [x] **Offline queue** (`OfflineQueue`): lessons and activity kept on the PC while the share is away are appended to the shared files (only the lines not already there; the share has no delete, so nothing there is overwritten or removed), checked line by line, and only then removed locally. Runs at start of the app and the MCP server; tool `sync_offline`. Twice or from two PCs adds nothing twice
- [x] **Curator** (`LessonCurator`): `learn_pending` (strongest first: several different people recorded the same lesson; corrections first) and `learn_review` approve / reject (qc-lead or admin, approval, audit) -> `learning\approved\lessons.jsonl`, generated `skill\LEARNED.md` (old one kept in `learning\history`), `rejected.jsonl` (hidden from everyone), `decisions.jsonl` (who decided). An approved lesson is not repeated in the live list. App: Knowledge page cards "Lessons to review" and "Team activity" (lead / admin only) with the review window (approve / reject selected) and the activity window
- [x] **Activity report**: `activity_summary` (one row per day and person: prompts, corrections, tool calls, tool errors, errors, chats, most used tools; totals per type) and the app window. **`activity_report` had no role gate in the port** (any engineer could read everyone's prompts): now qc-lead / admin only
- Verified: 10 new tests incl. the exit case on two simulated PCs (a correction recorded on PC A is in PC B's `learn_pull` at once and, after the lead approves with the tools, in the curated `LEARNED.md` PC B reads; an engineer is refused); **4 mutation checks** (keeping the local copy, appending every line again, rejected lessons staying visible, removing the role gate: each made tests fail); **real share**: an offline lesson reached it by appending, was approved into LEARNED.md by overwriting, nothing deleted (test folder `_selftest\phase9`, permanent files); **real app driven by UI Automation as the lead**: both cards present, review window listed both lessons with their context, approve and reject worked and wrote LEARNED.md and rejected.jsonl, activity window showed the person and tool counts
- **Not verified:** two real PCs at the same time (simulated with two data folders and one shared folder); the reflection turn and the shared kit were not re-run end to end in this phase; the review and activity windows were not seen by a person (screenshots taken by the script showed other windows); a real Claude session on PC B applying the curated rule (what it gets is verified, how well it follows it is not); the share's behaviour while it drops in the middle of a sync (the verify-then-delete order is tested, a cut cable is not)
- Exit met in the simulated case: a correction on PC A is in front of Claude on PC B after curation

### Phase 10: Hardening and licence - PARTLY DONE (needs a person for the rest)
- [ ] **Licence client** (seat, expiry, feature flags = roles, offline grace) against the company licence service: **NOT built.** The reference plan points to a licensing API brief of the PEC Spoolgen product that is not on this PC, and no service address or protocol was given; inventing a protocol would be worse than none. Roles (qc-engineer / qc-lead / admin) already work as feature levels without a service. Needs: the licensing brief (or a decision that roles are enough)
- [x] **Authenticated updates** (found by the security review: the update ran `PecQcSetup.exe` from a share that everyone can write to, unchecked): `latest.json` is signed with a release key kept off the share (RSA-PSS SHA-256, `latest.json.sig`), every PC holds only the public key from the shipped defaults (a settings file cannot override it), and the SHA-256 of Setup and `payload.zip` in the signed file are checked before Setup starts. Scripts: `new-release-key.ps1` (private key readable by the release manager only), `build-installer.ps1` (hashes), `publish-release.ps1` (signs; refuses to publish unsigned when PCs require a signature), `make-office-defaults.ps1` (public key). 25 tests incl. 3 mutation checks and a **round trip: key made and release signed by the PowerShell scripts, verified by the C# code, a changed copy refused** (found and fixed on the way: Windows PowerShell's default RSA provider cannot do PSS; the key script needed `icacls`, not `Set-Acl`). **Not active until a key pair is made and a build with the public key is installed on the PCs** (without a key the old behaviour remains and `qc_status` says the release was not checked)
- [~] **Code signing**: `scripts\sign-binaries.ps1` signs Setup and the executables inside the payload, repacks it and refreshes the hashes in `latest.json`; tested with a throw-away self-signed certificate (all five files signed, hashes match). **No real certificate exists**: SmartScreen reputation is not obtained. Windows Defender (current signatures) scanned `PecQcSetup.exe` and `payload.zip`: no threat. Other antivirus products and SmartScreen not tested
- [x] **Security review** (`docs/security-review.md`): all 60 tools inventoried by what they write and which are gated; network, process, registry and credential use read in the code. Fixed with tests: S2 overwriting an existing file with the Office writers now asks the user, S3 `activity_report` had no role gate (any engineer could read everyone's prompts), S4 shared knowledge pack conversion is qc-lead only, S6 more credential shapes scrubbed (bearer tokens, `sk-` keys, AWS keys, private key blocks, `user:password@host`, connection strings), S5 unreviewed lessons shown to Claude as labelled hints, S7 option `ActivityDetail = "metadata"` (no typed or answered text in the log). 16 security tests, 4 mutation checks. Open, needing the share administrator: S8 roles are enforced by the tools, not by the file system (a person with write access to `users\` can make themselves admin): share permissions recommended in the document. No known vulnerable NuGet package on the day of the build (and the advisory warnings are no longer suppressed)
- [x] **Runbook** (`docs/runbook.md`: roles, release procedure, release key, install, day to day, troubleshooting, backups) and **quick start** (`docs/quick-start.md`). **Training video: not made**
- [x] **Package checked as shipped**: the real `payload.zip` (102 MB) contains the Navisworks tools; the packaged MCP server unzipped into a fresh folder (no source tree) lists 60 tools, finds the packaged Navisworks tools and exports the real model (303 lines, 252 equipment, 20 s)
- [x] **Whole-product demo run on the real app with real Claude** (real model, real isometric, UI driven by UI Automation): turn 1 "export the model" -> approval window -> approved by the person at the keyboard -> 303 lines, 284 nozzles, 252 equipment, 18 s; turn 2 "check the isometric" -> REVIEW, 0 FAIL, 3 REVIEW, 29 OK, with both values and what could not be checked; turn 3 a copy with a changed class -> FAIL (class differs between sheets, class differs from the model). **The demo found two real defects, now fixed and tested**: (1) the approval timer started only after the Claude sign-in check, so an approval request could stay invisible for a long time; (2) the tool answered "waiting" at once, Claude called it three times in three seconds and gave up while the window was still coming up: the tool now waits up to 60 s for the click (`PEC_QC_APPROVAL_WAIT`), a denial is its own result (`denied`), 4 tests. A probe confirmed that nothing approves a request by itself. `docs/demo-script.md` is the walkthrough for presenting it
- [x] **Release key made** on the release manager's PC (private key in the user profile, readable by that user only, **not backed up yet: it must be**; the public key is in the private `config.local.json`). The build now carries the public key (`office-defaults.json`); a signed release was published to a TEST folder and verified from there by the C# code with the real key (a changed copy and another key refused); publishing without the signing key is refused by the script
- [x] **Install test, as clean as this PC allows**: the real `PecQcSetup.exe` + `payload.zip` copied alone into a new folder, run with no .NET on the path, installed into a new location: 437 files, app, MCP server and Navisworks tools present, Start-menu shortcut, uninstall entry and the Claude Code / Claude Desktop registrations made; the installed app started; **uninstall removed the files, the registry entry, both registrations and the shortcut**. Not a clean PC: Windows, .NET runtimes, Claude Code and Navisworks were all there
- **Incident, found by that test and repaired**: Setup/the app took the shared folder from the shipped defaults (the real share) instead of my test folder, so the installed test app registered the test user "CLN-1" on the real share **as its administrator** (the first registered person becomes admin). The record was deleted; nothing else on the share changed (checked: only two user records were new). The real owner's own record (registered as qc-engineer by the same run) was left as it is: **nobody is administrator on the real share now**; setting a role is a decision for the owner (edit `users\<code>.json`, `"Role": "admin"`). Whether Setup's shared-folder box really overrides the default was not established (the box was filled by the UI driver and the install still used the default): to check before the pilot. Lesson: never run an installed build against the real share in a test
- [ ] **NOT done (needs the owner's go-ahead): publishing 0.1.0 to the real `updates` folder** (`scripts\publish-release.ps1 -Apply`; the report shows 4 new files, nothing replaced). The package is built and signed-ready in `dist\`
- [x] **MCP servers of other products installed by Setup** (requested after the release: AutoCAD, Civil 3D, Plant 3D, Navisworks, Revit): the team's five one-click installers are bundled unmodified (`third-party\mcp-installers`, packed as `mcp-addons\` in the app folder) with a manifest that pins each script's SHA-256 and says how to detect the Autodesk program. Setup shows them in a new card, pre-ticks those whose program is on the PC, warns before running ones that may close AutoCAD / Revit / Claude, runs each ticked script in its own window after the QC tools are installed (the scripts are interactive), refuses a script whose hash differs, skips them in automatic updates, and does not remove them on uninstall (it says so). 9 tests incl. 3 mutation checks and running a fake installer; **driven in the real Setup** (dry run, no internet install): on this PC Navisworks 2027 and AutoCAD 2027 were found and ticked, Civil 3D / Plant 3D / Revit not, the two ticked ones were "run", the scripts landed in the app folder, a test install and its uninstall left nothing behind. **Not verified: a real run of any of the five scripts from Setup** (they download from GitHub, install plug-ins into Autodesk programs and change Claude's config: that was not repeated on this PC), Civil 3D / Plant 3D / Revit detection on a PC that has them, the licence of the Revit server (not stated in its installer), and the published 0.1.0 does not contain this yet
- **Not verified:** a truly clean PC (no Windows Sandbox or VM on this PC; the package was run from an unzipped folder, which still had .NET, Claude Code and Navisworks around it); SmartScreen; a person clicking through Setup and the app on another PC; the security sign-off itself
- Exit (open): the security sign-off by PEC's IT / security owner, who needs `docs/security-review.md` ("To do before the pilot")

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
