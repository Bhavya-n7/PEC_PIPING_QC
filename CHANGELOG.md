# Changelog

## 0.1.0 - first release candidate (Phases 0-4)
- Phase 4: Setup (PecQcSetup.exe + payload.zip): readiness checks, per-user install without administrator rights, registration with Claude Code and Claude Desktop (config backed up first), skills copied without replacing a skill of the same name, Start-menu shortcut, uninstall that leaves nothing behind; update check from the shared updates folder with an Update now bar and a silent automatic update that keeps the choices of the first install; build-installer.ps1 and publish-release.ps1 (older version never replaces a newer one, latest.json written last).
- Phase 3: desktop app `PecQcAssistant.exe` (WPF): chat, first-run identity, QC project cards, Navisworks and project-inputs status lights, approval window, Knowledge and Guide pages, QC quick tasks with hints, settings with role.
- Phase 2 (real share): QC layout created on the company share (`Pec.Qc.Mcp.exe init-shared`), seed prompts and skills published, registry/lessons/activity/audit verified there with a gated integration test (`PEC_QC_SHARE_TEST`).
- Phase 2 (code): central user registry `users\<code>.json` and roles (qc-engineer, qc-lead, admin) enforced by the tools; new tools `users_list` and `set_user_role`; seed master prompt, quick tasks and platform skills; `publish-to-shared.ps1` and `make-office-defaults.ps1`; 130 tests.
- Phase 1: foundation. Solution `PEC_Qc.slnx` (Core, Mcp, Tests); the shared stack of the PEC assistants ported and renamed; QC project cards; QC system prompt and quick tasks; MCP server `pec-qc` with 34 tools; share probe that works on shares without delete; 118 tests.
- Phase 0: development plan (`DEVELOPMENT.md`), repository hygiene (`.gitignore`, leak scan, config template).
