---
name: qc-tool-building
description: What to do when the user asks for something that no tool, recipe or skill does: look for it, then build a small tool (C# with tests), test it, and share it through the QC lead so every colleague benefits. Read it before building any tool.
---

# When nothing you have does the job

Order, always:
1. **Look first.** `script_tool_find` (approved tools and the user's drafts), `custom_tools` (recipes of existing tools), `guidance` (skills), and the built-in tools. Use what exists. Do not build what a colleague already has.
2. **Say so.** Tell the user in one line that nothing does it and what you will build. If it is a one-off question that can be answered by reading a file with the tools you have, answer it: build a tool only for something that will be asked again or that needs real computation.
3. **Write a small tool** with `script_tool_draft`. Keep it to one job.
4. **Test it**, then **show the user** the test results and what it does.
5. **Publish** (`script_tool_publish`): the user approves it (they see the code) and, by default, it is available to every colleague at once. If the office set `ScriptReview` to `lead`, it waits for the QC lead's review and until then only the author can run it (as a draft).

## What the tool is
A C# script. The arguments are in `Args["name"]` (always text). Files are touched **only** through the `Qc` object:
- read: `Qc.ReadText(path)`, `Qc.ReadLines(path)`, `Qc.PdfText(path)`, `Qc.ExcelRows(path, sheet)`; a tool can only read the files that were passed to it as arguments and what is in the project's inputs, models and reports folders;
- write: `Qc.WriteText("name.txt", text)`, `Qc.WriteExcel("name.xlsx", rows)`: only into one output folder, only .txt .csv .json .md .tsv .xlsx;
- `Qc.Log("message")`.

The result is what the script `return`s: a string, or an object that becomes JSON. Regex, LINQ, JSON and the usual collections are available.

**Open mode (the default)**: a tool may use any .NET library: files, network, other programs. Use that only for what the user asked; say what the tool touches. **Strict mode** (`ScriptSandbox` = `strict`, set by the office): only the `Qc` object; no network, programs, reflection or direct files; the draft is refused with the line number. Either way a tool runs in its own process with a time limit, and changed code is never run.

## Tests
Give at least two tests: a normal case and a bad input (`expectError`). **Tests pass their data as plain text arguments, not file paths**, so they run on any PC. Put a small real sample inside the argument. Say what the result must contain (`expectContains`). A tool whose tests fail is not published.

## Be honest about what it is
- It is code you wrote a minute ago: say that it is new and what you tested. Never say a tool is verified beyond its tests.
- It does not replace the QC checks. If the result feeds a finding, the finding follows `qc-findings-and-reports`: both values and the source.
- If the user's request needs something the sandbox does not allow (a network service, a program on the PC), say so; do not build a workaround.

## Lead: reviewing (only when the office uses `ScriptReview` = `lead`)
`script_tool_pending` shows the code, the author's tests and the SHA-256. Read the code with the user: what it reads, what it writes, whether it does what the description says and nothing else. `script_tool_review` approve re-checks the rules and runs the tests on this PC; the tool then goes to everyone with its hash pinned (changed code is never run). Reject with the reason: the author sees it.

## Using an approved tool
`script_tool_run` with the arguments as JSON text. Give the user the result and the paths of any files it wrote.
