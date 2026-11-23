# PEC Spoolgen Assistant: technical architecture

How the software is built and how it works. For developers and IT who need to understand, review, integrate with or extend it.
Everything below describes what the code does today (version in `Directory.Build.props`); open limits are listed in section 16.

## 1. What it is

A Windows desktop product for Petrocon Engineers & Consultants (PEC). An engineer types a request in a desktop app; **Claude** (through the user's own Claude Code sign-in) does the work by calling **tools** that drive **Intergraph Smart Spoolgen 9.x** (isometric drawing software): check PCFs, import, generate drawings, set up projects, apply client rules, run batches. Every change to a file or project is previewed and needs the engineer's approval.

Design principles:
- **Automate through documented, testable interfaces** (Spoolgen's command line and its text/XML configuration files). No screen automation, no decompiling.
- **Nothing changes without approval**: plan first (`apply=false`), approval in the app, backup, then write.
- **Never invent client data or engineering rules**: unknown means a review item.
- **Token efficiency**: a short, stable system prompt; rules, skills and documents are read on demand.
- **Self-contained**: users need no .NET, Python or admin rights.

## 2. System context

```
                        +-------------------------+
                        |  Claude (Anthropic cloud)|   <- user's own Claude account, no API key
                        +------------^------------+
                                     | HTTPS (handled by Claude Code)
 +--------------------+   starts    +-+----------------------+   stdio (MCP, JSON-RPC)   +--------------------------+
 | PecSpoolgen        | ----------> | Claude Code CLI        | <-----------------------> | Pec.Spoolgen.Mcp.exe     |
 | Assistant.exe      |  env, args  | claude -p stream-json  |      tool calls           | 44 tools, ApprovalGate   |
 | (WPF desktop app)  | <---------- | (installed by Setup)   |                           +------+---------+---------+
 |  chat, projects,   |  JSON lines +------------------------+                                  |         |
 |  approvals, guide  |                                                                         |         |
 +----+----------^----+                                          Spoolgen command line          |         | reads/writes
      |          |  approvals\<id>.pending / .approved             "Intergraph Spoolgen.exe"    |         v
      |          +-------------------------------------------------- /import  /generate <-----+   style files, project files,
      |                                                                                         |   logs (%TEMP%), knowledge
      v                                                                                         v
 Shared office folder (files on a server share): skills, learning, context, documents, prompts, tools, requirements, projects, updates
```

Four deliverables are built from one solution:

| Deliverable | Project | What it is |
|---|---|---|
| `PecSpoolgenAssistant.exe` | `Pec.Spoolgen.Desktop` | WPF app: the user interface and the owner of the chat, approvals and updates |
| `mcp\Pec.Spoolgen.Mcp.exe` | `Pec.Spoolgen.Mcp` | MCP server over stdio, single-file self-contained; holds all tools |
| `PecSpoolgenSetup.exe` | `Pec.Spoolgen.Setup` | WPF installer/updater, single file; carries `payload.zip` next to it |
| (library) | `Pec.Spoolgen.Core` | All logic: Spoolgen integration, files, knowledge, Claude CLI, install |

## 3. Solution structure

Solution `PEC_Spoolgen_MCP.slnx`; one version number for everything in `Directory.Build.props`.

| Project | Target | Packages |
|---|---|---|
| `Pec.Spoolgen.Core` | `net10.0` | PdfPig 0.1.16 (reads PDFs for the knowledge pack) |
| `Pec.Spoolgen.Mcp` | `net10.0`, `win-x64`, self-contained, single file | ModelContextProtocol 2.1.0 (C# SDK), Microsoft.Extensions.Hosting 10.0.15 |
| `Pec.Spoolgen.Desktop` | `net10.0-windows`, WPF | none beyond Core (project reference to Mcp is build-order only) |
| `Pec.Spoolgen.Setup` | `net10.0-windows`, WPF | none beyond Core; reuses the Desktop `Theme.xaml` |
| `Pec.Spoolgen.Tests` | `net10.0` | xUnit 2.9.3, coverlet |

Core namespaces:

| Folder | Responsibility |
|---|---|
| `Install` | Finds Spoolgen, its tools and its manuals (`SpoolgenInstall`) |
| `Projects` | Iso directories (`IsoDirList.xml`), `ProjectList.xml`, project info, `Preflight`, `ProjectBuilder` (clone a template project) |
| `Styles` | Style folders (`StyleFolder`), `OptionSwitchFile` (.OPL), `StyleXmlFile` (`<Style>.xml`, the real definition), `.ALT/.POS` readers |
| `Pcf` | PCF parser, summary, quality checks |
| `Run` | `SpoolgenRunner` (command line, one job at a time, 30-min timeout, job history), `SpoolgenLogs` (log parsers) |
| `Knowledge` | `ManualIndex` + `Bm25` (search installed manuals and documents), `KnowledgeStore` (Markdown entries with a required source), `TrainingPack` (PDF to Markdown + `pack.json`), `SharedHub` |
| `Requirements` | Client requirement files as data; `RequirementChecks` (number formats, deliverables) |
| `Claude` | `ClaudeCli` (sign-in, headless chat, stream parsing, token usage), `PromptBuilder`, `ProjectProfile`/`ProfileStore`, `QuickTasks`, `AssistantSetup` |
| `Approvals` | `ApprovalGate` |
| `Tools` | `CustomTools` (JSON recipes of existing tools, synced with the shared folder) |
| `Setup` | `Installer`, `Updates` |
| `Util` | `SafeWrite` (diff, backup, write) |

## 4. Runtime flow

### 4.1 Application start (`PecSpoolgenAssistant.exe`)
1. Load `Settings` (per-user `settings.json`); show model/effort pickers from it.
2. Seed default skills, prompts and the example requirement file into the shared/local folders **only where missing** (never overwrites).
3. Find Spoolgen (`SpoolgenInstall.Find`), Claude Code (`ClaudeCli.Find`), check Claude sign-in (`claude auth status`), write the MCP config file, check for an update (`latest.json`).
4. Show the status lights (Claude, Spoolgen, shared folder) and the welcome screen.

### 4.2 One chat turn
1. The user sends text (or clicks a quick-task card, which fills a prompt template from the active project).
2. `PromptBuilder.Build` creates the **system prompt** (see 6).
3. The app starts `claude -p --output-format stream-json --verbose --mcp-config <file> --strict-mcp-config --tools "" --allowedTools mcp__pec-spoolgen__* --disable-slash-commands --system-prompt <text> [--resume <session>] --model <id> --effort <level>` with the prompt on standard input. `--tools ""` removes Claude Code's built-in Bash/Edit tools: **Claude can only call the Spoolgen tools**.
4. Claude Code launches `Pec.Spoolgen.Mcp.exe` (from the MCP config) with environment `PEC_SPOOLGEN_DATA` and `PEC_SPOOLGEN_APPROVAL=on`, and the model calls tools over MCP.
5. The app reads the JSON lines: session start, assistant text, each tool call and result (shown as collapsible steps), the final result with token usage (input, cached share, output).
6. The session id of the first turn is reused with `--resume` for follow-ups, so the conversation context stays in Claude Code.

### 4.3 A change that needs approval (e.g. import, style edit)
```
Claude calls import_pcf(apply=false)   -> preflight + plan (command, blockers, warnings)   [nothing changes]
user agrees in chat
Claude calls import_pcf(apply=true)    -> ApprovalGate: writes approvals\<id>.pending, returns "waiting for approval"
App (1 s timer) sees .pending          -> approval window: summary, diff or command, Approve / Reject
user presses Approve                   -> app writes <id>.approved
Claude calls the same tool again       -> gate finds .approved (single use, valid 10 minutes) and deletes it -> the work runs
```
The id is a hash of the tool and its arguments, so an approval covers exactly that action. `PEC_SPOOLGEN_APPROVAL=off` disables the gate on test PCs only.

## 5. MCP server (44 tools)

Hosted with `Microsoft.Extensions.Hosting` and the ModelContextProtocol C# SDK; stdio transport (stdout carries the protocol, all logging goes to stderr); tools are discovered by attribute from the assembly. Tool conventions:
- **Write tools** take `apply=false` by default and return a plan/diff; `apply=true` goes through `ApprovalGate`, then `SafeWrite` (timestamped backup `<file>.bak_YYYYMMDD-HHMMSS`, then write).
- **Results are compact** (counts, first N examples, details on request) to save tokens.
- **Read tools are marked read-only**; destructive ones are marked so clients can show them.

| Group | Tools |
|---|---|
| Environment & projects | `spoolgen_environment`, `list_iso_directories`, `add_iso_directory_setting`, `register_iso_directory`, `list_projects`, `describe_project`, `describe_style`, `active_project`, `create_project`, `preflight_check` |
| PCF | `pcf_check`, `pcf_summary` |
| Spoolgen runs | `import_pcf`, `generate_drawings`, `list_drawings`, `read_messages`, `job_history`, `run_batch` |
| Style configuration | `read_option_switches`, `set_option_switch` (only for styles without an xml), `read_style_properties`, `set_style_property`, `weld_numbering`, `set_weld_prefix` |
| Client requirements | `client_requirements`, `save_requirements`, `check_numbers`, `check_deliverables`, `read_text_file` |
| Knowledge | `build_manual_index`, `search_manuals`, `explain_option_switch`, `convert_training`, `training_overview`, `read_training`, `recall`, `list_knowledge`, `remember`, `forget` |
| Guidance & extension | `guidance`, `save_skill`, `custom_tools`, `run_custom_tool`, `create_custom_tool` |

## 6. Claude Code integration and token efficiency

- **Authentication:** the user's own Claude account via Claude Code (`claude auth login` started from the app). No API key is stored anywhere.
- **Model and effort:** per-user settings (default `claude-sonnet-5-5`, effort `medium`), changeable under the message box; passed as `--model` / `--effort`. A different model has its own prompt cache.
- **System prompt** (`PromptBuilder.Build`): a short fixed core prompt + the active project block + an index of guidance names + a one-line list of custom tools + a short knowledge summary. It contains no dates or counters, so the **prompt cache** stays valid across turns. Measured about 87% fewer tokens per conversation with a warm cache.
- **On demand instead of in the prompt:** full rules and skills (`guidance`), manuals/documents (`search_manuals`, `read_training`), saved knowledge (`recall`).
- **Token display:** the app shows input tokens, share served from cache, and output tokens per turn.

## 7. Safety model

| Mechanism | What it guarantees |
|---|---|
| `ApprovalGate` | No file or project change without the user's approval in the app; single-use, 10-minute expiry, bound to the exact arguments |
| `SafeWrite` | Every write is a diff first, then a timestamped backup, then the write; encoding of the file is preserved |
| Preflight | Before any Spoolgen run: iso directory registered, project and style folders present, required lookup files present; blockers stop the run |
| One job at a time | `SpoolgenRunner` serialises Spoolgen runs (semaphore), 30-minute timeout, history in `jobs\` |
| Read allow-list | `read_text_file` only reads text files inside iso directories, the shared folder and documents; databases and other paths are refused |
| No Claude shell | `--tools ""` removes Bash/Edit: Claude cannot run commands or edit files except through approved tools |
| Never edit originals | Tests and experiments run on copies; confidential folders are git-ignored |

## 8. Spoolgen integration

- **Only documented interfaces:** `"Intergraph Spoolgen.exe" /import <files> /isodirectory <dir> /project <name>` and `<pipeline> /generate <style> /isodirectory <dir> /project <name>`. The exit code is always 0, so success is read from the log (`%TEMP%\SGBatchImportFromCommandLine.Log`, `SGBatchGenerateFromCommandLine.Log`): per file "Isogen return code = 0" and for imports "New pipeline X added to project".
- **Iso directories** must be registered for the Windows user in `IsoDirList.xml`; the preflight checks it (`register_iso_directory` can add it, with approval).
- **Project layout used:** `ProjectList.xml` in the iso directory; per project `Pipelines\POD|Isogen|Work`, the workflow database `WorkFlow.mdb`, one folder per style, `Data\Get External Data\GetExternalData.xml` naming lookup databases. `.mdb` files are never opened or edited.
- **Style definition:** a style's real definition is `<Style>.xml` (what I-Configure edits). Spoolgen **rewrites the style's `.OPL`, `.WDF` and `isogen.FLS` from it on every generate**, so lasting changes are made in the xml (`StyleXmlFile`: only existing attributes, preview, backup, UTF-8 preserved). This was proven by test and drives the design of the style tools.
- **Imports consume the PCF:** Spoolgen moves it into `Pipelines\Isogen`.
- **Drawings** are compressed DWG files; they are checked by file presence, logs/messages, and rebuilt style files, not by reading their content.

## 9. Knowledge and client requirements

- **Manual index:** the installed Spoolgen manuals (PDF) are read locally with PdfPig, split into passages and indexed with BM25 (`manual-index\` in the data folder). Search results carry document and page; manual text is not copied into the repository.
- **Training pack:** `convert_training` converts the training/client PDFs to Markdown (headings from font size/weight) plus `pack.json`, incrementally by SHA-256, into `<documents>\pack\`. Meant to sit on a server and be refreshed when documents change.
- **Knowledge store:** Markdown entries with front matter; **a source is required** for every entry (document/page or session).
- **Client requirements:** one JSON per client in the shared `requirements` folder: number rules (strict and candidate regular expressions, start, source), deliverable rules (file-name patterns with `{iso}` `{isonodash}` `*` `?`), other rules (`manual`/`setting`). Every rule carries its source (document, section, page). `RequirementChecks` evaluates numbers and deliverables; judgement rules are reported as review items.
- **Batch:** `run_batch` = check -> import -> generate per style -> deliverables check -> export -> `batch-report.csv/.md`, one approval for the whole plan.

## 10. Shared office folder and data

**Per-user data** (`%APPDATA%\PEC\SpoolgenAssistant\`, override with `PEC_SPOOLGEN_DATA`): `settings.json`, `profiles.json` (active project), `quick-tasks.json`, `master-prompt.md`, `jobs\`, `approvals\`, `manual-index\`, `tools\`.

**Shared office folder** (`Settings.SharedRoot`, a local path or a server share; plain files, no database):

| Folder | Content |
|---|---|
| `skills\<name>\SKILL.md` | How-to guides Claude reads on demand; `save_skill` updates them (with approval) |
| `learning\` | Lessons and decisions (Markdown, with sources) |
| `context\` | Project state notes |
| `documents\` | Client documents, converted training pack |
| `prompts\` | Master prompt and quick-task definitions |
| `tools\` | Custom tools: JSON recipes of existing tools, synced between a user's PC and the folder |
| `requirements\` | Client requirement JSON files |
| `projects\` | One JSON card per project (client, Spoolgen project, server IP, iso directory, default style, details); the *active* project stays per user |
| `updates\` | `PecSpoolgenSetup.exe`, `payload.zip`, `latest.json` for in-app updates |

Rules: the app and Setup **seed missing files only and never overwrite** shared files (people edit them); `scripts\publish-to-shared.ps1` is the reviewed way to publish new bundled skills/prompts (report first, backup on change). If the shared folder is unreachable, the app works with local copies.

## 11. Settings

`settings.json` (per user): `SharedRoot`, optional separate folders (`KnowledgeDirectory`, `ContextDirectory`, `SkillsDirectory`, `DocumentsDirectory`, `ToolsDirectory`), `MasterPromptFile`, `ExtraIsoDirectories`, `UpdateSource`, `Model`, `Effort`, Spoolgen home. Every folder has an `Effective*` property: explicit path, else `SharedRoot\<sub>`, else a local default. Environment: `PEC_SPOOLGEN_DATA` (data folder), `PEC_SPOOLGEN_HOME` (Spoolgen install), `PEC_SPOOLGEN_APPROVAL` (`on`/`off`).

## 12. Desktop app

- WPF on .NET 10, one design system in `Theme.xaml` (brand blue, navy navigation rail, card/pill/button styles, Segoe MDL2 icons) shared with Setup.
- Views: Assistant (welcome with project card and quick tasks, then conversation), Projects (cards), Quick tasks, Knowledge (dashboard), Guide & FAQ (the guide Markdown rendered in-app). Dialogs: approval, project profile, settings, input.
- Chat is rendered by `MarkdownView` (headings, lists, tables, code) into a `FlowDocument`; each tool call is a collapsible step.
- Threading: the chat runs on a background task; UI updates go through the dispatcher; values read from controls are copied before `Task.Run` (a cross-thread bug found and covered by a test). A 1-second `DispatcherTimer` polls `approvals\`.
- The in-app guide is the single file `help\help.md`.

## 13. Build, packaging, installation and update

**Build:** `dotnet build PEC_Spoolgen_MCP.slnx`; tests: `dotnet test tests\Pec.Spoolgen.Tests`.

**Release:** `scripts\build-installer.ps1` (version from `Directory.Build.props`, notes from `CHANGELOG.md`):
1. `dotnet publish` the app (self-contained, win-x64);
2. publish the MCP server single-file into `app\mcp\`;
3. zip the app folder to `dist\payload.zip` (app + `mcp\` + `skills\` + `prompts\` + `requirements\` + `help\`);
4. publish Setup single-file; write `dist\latest.json` (`Version`, `Notes`, `Setup`, `Released`).

**Install** (`PecSpoolgenSetup.exe`, per user, no admin): readiness checks; asks for the install folder and the shared office folder; stops a running app; extracts `payload.zip` to `%LOCALAPPDATA%\Programs\PEC Spoolgen Assistant`; writes settings; optionally installs Claude Code with the official installer (needs internet), registers the MCP server with Claude Code (`claude mcp add pec-spoolgen --scope user`) and Claude Desktop (config backed up first), copies skills for Claude Code, adds a Start-menu shortcut and a per-user uninstall entry; writes `install-manifest.json`. Uninstall removes registrations, skills and the shortcut (user data only if asked).

**Update:** the app reads `latest.json` from `UpdateSource` (default `<SharedRoot>\updates`, a folder or web address). If newer, the **Update now** bar copies the setup and payload to a temp folder, closes the app and runs Setup from there. A release is published by copying the three `dist` files to the updates folder.

## 14. Testing

105 xUnit tests (synthetic data only): Spoolgen log parsing and runner, PCF checks, option-switch and style-xml editing (layout-preserving), approval gate, installer and update logic, knowledge store/index/training pack, shared folder, project cards, custom tools, requirement checks, model settings, plus guards (WPF thread rule). Real-system checks were run on a test PC with the real Spoolgen (import, generate, batch, style change then regenerate), the real Setup window and in-app update driven through UI Automation, and a real Claude conversation measured for tokens.

## 15. Security and privacy

- Conversations go to Anthropic through **Claude Code under the user's own account**; the app holds no API key and no password.
- No telemetry from the app. Client and vendor data (PCF, drawings, MDB, PDFs, Spoolgen manuals, the shared folder) are never committed to the repository; tests use synthetic files.
- Secrets live only in git-ignored files. The app never reads decompiled vendor material.
- Writes are limited to project/style/shared files through the approved tools; Claude has no shell.
- Executables are not code-signed yet (Windows shows an "unknown publisher" warning).

## 16. Known limits

- Spoolgen is driven through its command line: one job at a time; the GUI may need a refresh or reopened project to show results.
- Generated DWG content cannot be read here; style changes are verified by regeneration and rebuilt style files.
- The client-specific lookup databases must be present for a project to import (Reliance example pending).
- No login/licence layer yet (design brief: `docs\licensing-api-brief.md`).
- Setup needs internet to install Claude Code; the assistant needs access to Claude's servers to work.
- Executables are not signed; no test on a clean PC yet.

## 17. Extension points

| To add | Do this |
|---|---|
| A tool | New static method with `[McpServerTool]` in `src\Pec.Spoolgen.Mcp\Tools`; logic in Core; plan/apply split + `Resolve.NeedApproval` if it writes; tests; update `help\help.md` and `CHANGELOG.md` |
| A how-to for Claude | `skills\<name>\SKILL.md` (short, imperative); publish to the shared folder with `publish-to-shared.ps1` |
| A client's rules | `requirements\<client>.json` (see `docs\client-requirements.md`), each rule with a source |
| A repeatable multi-step task | A custom tool (JSON recipe of existing tools) via `create_custom_tool`; it syncs to the shared folder |
| A style property | Nothing: `read_style_properties` / `set_style_property` work on any existing attribute of the style xml |
| A quick-task card | `prompts\quick-tasks.json` |

## 18. Repository map

```
CLAUDE.md, context.md, PLAN.md, CHANGELOG.md, README.md   working rules, current state, phases, versions
Directory.Build.props                                      single version number
src\Pec.Spoolgen.{Core,Mcp,Desktop,Setup}                  the code
tests\Pec.Spoolgen.Tests                                   unit tests
skills\  prompts\  requirements\example.json  help\help.md bundled content (seeded, never overwrite)
docs\                                                      technical docs (this file, architecture, spoolgen-automation, knowledge-pack, token-efficiency, shared-folder, client-requirements, licensing-api-brief)
learning\lessons.md                                        what was learned (causes, fixes, pitfalls)
scripts\build-installer.ps1  publish-to-shared.ps1  make-icon.ps1
assets\                                                    logo and icon
Spoolgen_MCP\  Training\  dist\                            git-ignored: shared folder, client training material, build output
```
