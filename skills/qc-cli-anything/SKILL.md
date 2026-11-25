---
name: qc-cli-anything
description: What to do when the user needs OTHER software driven (an application, a service, a file format with its own program) and no tool does it: look for a ready harness, otherwise build a command-line harness with the CLI-Anything method, test it, and share it on the office folder so every colleague gets it. Read it before building or installing a harness.
---

# Driving other software: harnesses (CLI-Anything)

A **harness** is a small command-line program (`cli-anything-<software>`) that lets you operate another piece of software: JSON output, one command per action, a SKILL.md that says how. The method is the open-source CLI-Anything project (Apache-2.0); its guide is shipped with the assistant (`harness_guide`). Use it only for *other software*. Computations and file processing are script tools (`qc-tool-building`).

## Order, always
1. **Look first**: `harness_find` (the office folder, then CLI-Hub with 70+ ready CLIs), `script_tool_find`, `custom_tools`, the built-in tools. Use what exists. A ready one: `harness_install` (apply=false first, show the user where it comes from and what it needs, then apply=true), `harness_skill` / `--help`, `harness_run`.
2. **Say so**: tell the user in one line that nothing does it and what you will build, and whether the software or its API is available on this PC.
3. **Check it can be done**: the method works from the software's **source code, API or an existing command line**. For closed-source programs (the Autodesk products, most commercial software) do not invent a screen-driving trick: wrap what the vendor officially gives (its command-line engine such as `accoreconsole`, its COM / .NET API, its file formats, a local server). If there is none, say so and ask for an export (PDF, DXF, Excel, CSV) instead.
4. **Read the method**: `harness_guide` topic `harness` (all of it, in parts), then `build`, and `template-skill`, `repl-skin` when you write the files. Follow it; do not improvise.
5. **Build** in the workspace: `harness-work\<software>\agent-harness\` with `setup.py`, the package `cli_anything\<software>\`, the command `cli-anything-<software>` (console script), `--json` on every command, undo/redo where the software has it, a `SKILL.md`, and tests (`tests\`). Get the software's source or documentation with WebFetch / curl (a repository zip needs no git).
6. **Test**: `harness_test` (apply=false checks the structure; apply=true installs it into the private environment and runs the tests, with the user's approval). Tests that need the real software must run against it, not be skipped. Fix until they pass.
7. **Show the user** what it does and the test result. Say what you did **not** cover: one run rarely covers everything (the method has a refine step: `harness_guide` topic `refine`).
8. **Share**: `harness_publish` (apply=false shows the plan; apply=true, the user approves). It goes to `tools\harnesses\<software>` on the office folder with a checksum; colleagues get it with `harness_find` and `harness_install`.
9. **Use it**: `harness_install`, read the SKILL.md (`harness_skill`), `harness_run` with `--json`.

## Rules
- **QC is read-only.** A harness must never be used to change a model, a drawing or a client document in a QC task. Run commands that change files only on copies, and say so.
- **Each run needs the user's approval** (they see the command). Say what it will do before you ask; afterwards say what it changed.
- **Be honest about what it is**: code written today, tested by its own tests, on the software versions available here. Never say it is verified beyond that. If the software is missing on a colleague's PC, the harness cannot work there: say what must be installed (`requires`).
- **No secrets** in a harness: no passwords, tokens or company paths in code or tests; use arguments or environment variables.
- A ready-made CLI-Hub harness is another project's code: tell the user where it comes from, and that it runs with their rights.
