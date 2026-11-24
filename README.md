# PEC QC Assistant

A Windows desktop assistant for the QC team of Petrocon Engineers & Consultants. A QC engineer asks; Claude (the user's own Claude Code sign-in)
runs the checks by calling tools in a local MCP server. QC is **read-only**: the tools find differences between drawings, the Navisworks model and the project
inputs and write reports; people decide.

| QC type | Checked | Against |
|---|---|---|
| Piping isometric | isometric PDF | Navisworks model, PMS, P&ID, line list, vendor data |
| P&ID | redrawn P&ID PDF | client's original P&ID PDF |
| GA drawing | GA PDF | Navisworks model, P&ID, vendor data |

Built like the other PEC assistants: WPF app + C# MCP server + Setup with auto-update + one shared office folder (skills, learning, requirements,
projects, models, reports, users, activity, updates).

**Status:** in development, one phase at a time (Phases 0-5 done: plan, foundation, shared folder with identity and roles, desktop app, Setup with auto-update, project inputs / requirements / findings / skills; next: the isometric check). Plan, decisions and checkboxes: [`DEVELOPMENT.md`](DEVELOPMENT.md). Release notes: [`CHANGELOG.md`](CHANGELOG.md).

## Working on it
- Needs the .NET 10 SDK (per-user install: `dotnet-install.ps1 -Channel 10.0`). `dotnet build PEC_Qc.slnx`, `dotnet test`.
- The MCP server is `src\Pec.Qc.Mcp` (`pec-qc`, stdio); register it with Claude Code: `claude mcp add pec-qc -- <path to Pec.Qc.Mcp.exe>`.
- Private site settings (share path, server names) go in `config.local.json` (git-ignored, template `config.example.json`).
- Before every push: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\leak-scan.ps1`.
- Client drawings, models, snapshots and reports are never committed (see `.gitignore`).

## Release (for the release manager)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-installer.ps1             # dist\PecQcSetup.exe, payload.zip, latest.json
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish-release.ps1             # report: what would be copied to the update folder
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish-release.ps1 -Apply      # copy (an older version never replaces a newer one)
```

- Set the version in `Directory.Build.props` and add a `## <version>` section to `CHANGELOG.md` first: its text becomes the release notes in `latest.json`.
- The update folder and the shared folder come from the private `config.local.json` (`update_source`, `shared_root`) and are baked into the package as `office-defaults.json`; they are never committed.
- Users run `PecQcSetup.exe` once; after that the app updates itself from the update folder (Settings can turn that off).
