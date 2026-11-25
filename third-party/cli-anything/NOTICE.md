# CLI-Anything (third-party, vendored)

The files in this folder are copied, unmodified, from the CLI-Anything project:

- Project: https://github.com/HKUDS/CLI-Anything (HKUDS)
- Folder copied: `cli-anything-plugin/` (methodology HARNESS.md, command specifications, guides, templates, repl_skin.py, skill_generator.py) and `registry.json` (the CLI-Hub list)
- Commit: 34f519533bc175d2fe287ab8316b0dd99bb9cc43 (copied on 2026-11-25)
- Licence: Apache License 2.0 (see LICENSE in this folder)

The PEC QC Assistant uses the methodology as the guide Claude follows when it builds a command-line harness for other software (tool `harness_guide`), and registry.json as the offline copy of the CLI-Hub list. Nothing here is executed by the assistant: harnesses are built by Claude, tested, and shared through the office folder.
