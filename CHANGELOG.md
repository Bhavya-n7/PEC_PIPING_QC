# Changelog

## Unreleased
- Phase 2 (code): central user registry `users\<code>.json` and roles (qc-engineer, qc-lead, admin) enforced by the tools; new tools `users_list` and `set_user_role`; seed master prompt, quick tasks and platform skills; `publish-to-shared.ps1` and `make-office-defaults.ps1`; 130 tests. Real-share checks still open.
- Phase 1: foundation. Solution `PEC_Qc.slnx` (Core, Mcp, Tests); the shared stack of the PEC assistants ported and renamed; QC project cards; QC system prompt and quick tasks; MCP server `pec-qc` with 34 tools; share probe that works on shares without delete; 118 tests.
- Phase 0: development plan (`DEVELOPMENT.md`), repository hygiene (`.gitignore`, leak scan, config template).
