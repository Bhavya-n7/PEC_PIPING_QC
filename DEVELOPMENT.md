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

### Phase 2: Shared office folder, identity and central users
- [ ] QC folder layout created on the real share (README, `hub.json`, all folders) by `SharedHub.Ensure`
- [ ] `office-defaults.json` from private `config.local.json` (share + updates), applied to empty settings only
- [ ] `UserIdentity` + **central registry** `users\<code>.json` (role, first/last seen, version, machine), roles enforced in tools
- [ ] Activity and audit logs verified on the real share (several users, offline fallback and later sync)
- [ ] Seed content: shared `README.md`, `master-prompt.md`, platform skills, `quick-tasks.json`
- [ ] `scripts\publish-to-shared.ps1` (report first, `-Apply` copies, backs up changed files)
- Exit: two PCs/identities see each other's lessons and appear in the user registry

### Phase 3: Desktop app (WPF)
- [ ] Shell copied from the reference and adapted: theme, chat with markdown, tool steps, attachments, chat history, token display
- [ ] First-run identity dialog (name, employee code, company), settings ("About you", shared folder, model/effort, approval mode)
- [ ] Status lights: Claude, shared folder, Navisworks, project inputs
- [ ] Approval window (what will be saved/published, where)
- [ ] Projects view (cards) and Guide & FAQ
- [ ] Quick-task cards for the three QC types
- Exit: clicked through by a person: first run, chat, approval, project card

### Phase 4: Setup, packaging and auto-update
- [ ] `PecQcSetup.exe` + `payload.zip` (self-contained, no .NET/Python needed), readiness checks, per-user install, Claude Code and Claude Desktop registration, skills copy, shortcut, uninstall, manifest
- [ ] `latest.json` on `<shared>\updates`, **Update now** bar, auto-update (once per version, `/auto`), channels
- [ ] `scripts\build-installer.ps1`, `publish-release.ps1`, `make-icon.ps1`
- Exit: clean Windows PC: install, first run, update to a newer build, uninstall

### Phase 5: QC knowledge, project inputs and skills
- [ ] Project card + inputs library: PMS, line list, P&IDs, vendor data (indexed for search; Excel line list and PMS table read into structured data)
- [ ] Requirement packs and checklists per QC type (JSON, each rule with source); `client_requirements`, `save_requirements`
- [ ] Skills: `qc-iso`, `qc-pid`, `qc-ga`, `qc-findings-and-reports`, `qc-project-inputs`, `qc-troubleshoot`; master prompt; quick tasks
- [ ] Findings model (check, status OK/REVIEW/FAIL, drawing value, source value, source reference, location) used by all three QC types
- Exit: Claude can answer "what does the PMS say for class B400A at 2 inch" with the document and page

### Phase 6: Piping isometric QC
- [ ] `Pec.Qc.NavisExport` (net48): headless Navisworks Manage export of PIPE/BRAN/component attributes to a snapshot; tree path, duplicate and federated-model handling
- [ ] C# iso PDF reader (title block, BOM, cut list, coordinates, supports, continuations)
- [ ] Checks: identity, title block vs model, BOM vs model, coordinates, lengths, plus line list, PMS (class, rating, material), P&ID (line existence, from/to) and vendor data
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
