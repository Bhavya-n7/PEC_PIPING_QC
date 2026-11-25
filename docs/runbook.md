# Runbook: running the PEC QC Assistant

For the release manager and the QC lead. Server names and paths are not in this file: they are in the private `config.local.json` of the release manager.

## Roles
| Role | Can | How it is set |
|---|---|---|
| qc-engineer | run checks, read reports, record lessons, use the kit | the default; the first person to register on the share is its **administrator** |
| qc-lead | + approve lessons and requirement packs, save shared skills, convert documents into the knowledge pack, read the team's activity | an administrator: `set_user_role` in a chat, or edit the person's file in `users\` |
| admin | + approve shared kit items, change roles | the first person; later by an administrator |

## Releasing a new version
1. Update `CHANGELOG.md` (a `## <version>` section) and the version in `Directory.Build.props`.
2. `dotnet build` and `dotnet test` green; `scripts\leak-scan.ps1` clean.
3. `scripts\build-installer.ps1` (builds the app, the MCP server, the Navisworks tools if this PC has Navisworks, Setup, `payload.zip`, `latest.json` with the SHA-256 of both files). **Build on a PC that has Navisworks Manage, or the package has no model export.**
4. `scripts\sign-binaries.ps1 -Thumbprint <cert>` (once a certificate exists). It refreshes the hashes in `latest.json`.
5. `scripts\publish-release.ps1` (report), then `-Apply`. It signs `latest.json` with the release key (`update_signing_key` in the private config) and copies the payload and Setup first and `latest.json` and its signature last. An older version never replaces a newer one. It refuses to publish an unsigned release when the build tells PCs to require a signature.
6. `scripts\publish-to-shared.ps1` (report), then `-Apply` publishes changed skills, prompts and the example requirement pack to the shared folder (old versions kept as `.bak_`).
7. PCs check at start; **Update now** or the automatic update installs it. A release that is not signed with the company key is ignored by every PC (the status shows why).

## The release key (once)
`scripts\new-release-key.ps1` makes the key pair. The private key stays on the release manager's PC, readable by that user only: **back it up** (if it is lost, every PC must be reinstalled with a new public key). Put the public key in `config.local.json` as `update_public_key`, the path of the private key as `update_signing_key`, rebuild, and install that build once by hand on each PC. Never put the private key on the share or in the repository.

> **Testing warning:** an installed build uses the shared folder from its shipped defaults (the company share). Do not start an installed test build on a PC that can reach the share unless you mean to: the first person who registers there becomes its administrator. Give a test its own settings with an empty shared folder, or unplug it.

## First install on a PC
Run `PecQcSetup.exe` (next to `payload.zip`). It checks Claude Code (required), Navisworks (optional: needed for model export, isometric and GA checks) and the shared folder, installs per user (no administrator rights), registers the tools with Claude Code and Claude Desktop with approvals on, and adds a Start-menu shortcut. The first start asks for name, employee code and company. Setup can uninstall and leaves nothing behind.

## Day to day
- **Model**: someone with Navisworks runs the model export once per model revision (`model_export`: about half a minute to a few minutes); the snapshot is shared and everyone checks against it. `model_summary` warns when the model file is newer than the snapshot.
- **Project**: add the project (client, number, model file, inputs folder) in Projects; put the PMS, line list, P&IDs and vendor data in the inputs folder.
- **Client rules** and the **support map** (`iso-rules.json`) are the QC lead's: every rule needs a source document.
- **Lessons**: the QC lead reviews on the Knowledge page ("Lessons to review") weekly: approve true rules, reject wrong ones. **Team activity** shows who uses it and where it is corrected.
- **Reports** are in `reports\<project>\` (Excel and Markdown), never overwritten.

## When something goes wrong
| Symptom | Look at |
|---|---|
| A tool says "needs the role ..." | the person's role in `users\<code>.json` |
| "Shared folder not reachable" | the share; the app works from local copies and sends lessons and activity back by itself (`sync_offline`) |
| Update is not offered | `qc_status` shows `update.error`: unsigned or wrongly signed release, wrong key on the PC, or no `latest.json` |
| Model export fails | Navisworks not found (Settings), the model file is open exclusively, or a PC with a stripped environment; the message says which. The export runs hidden on a copy |
| A check gives many REVIEWs | read `notVerified` and the REVIEW reasons: they say what could not be read or compared (a scanned drawing, a missing line list, no support map) |
| A finding is wrong | the QC lead records the correction (a lesson) with the source; if a tool is wrong, report it with the drawing and the finding |

## Backups
Back up the shared folder (it holds everything the team has built) and the release private key. The per-user data folder holds only settings and local copies.
