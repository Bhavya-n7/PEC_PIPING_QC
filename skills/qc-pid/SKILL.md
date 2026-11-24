---
name: qc-pid
description: How to check a redrawn P&ID (PDF) against the client's original P&ID (PDF): every line, equipment, instrument, valve, tag and note copied accurately and completely. Read it before the first P&ID check in a chat.
---

# P&ID check (redrawn against the original)

Checklist: `qc_checklist` (type pid), 7 items. Findings and the report: `qc-findings-and-reports`. The original is in the project inputs (`pid` folder); the redrawn drawing is the file the user names.

## The tool compares the text, you report it
1. **`pid_compare`** with the redrawn PDF. It finds the original in the project's pid inputs by file name (or you pass `original`; if several could match it says so, ask the user). It compares sheet by sheet: line numbers, tags (equipment, instruments, valves), short labels and notes, text read horizontally and rotated.
2. **Answer**: the verdict, every FAIL (missing or changed: both texts, the sheet and zone on the original, look-alike characters named), then the REVIEWs, then `notVerified`. `apply=true` saves the Excel and Markdown report after the user approves; offer it.
3. To look closer or to answer a question about one drawing: **`pid_read`** (items with their zone A1..H6; `full` adds notes and labels).

## Read the result honestly
- **FAIL** = on the original but missing on the redrawn drawing, or different (a character, a digit, a note's wording), or a count that is lower. **REVIEW** = only on the redrawn drawing, an item in a different place (a redraw can shift things: look), or a sheet with no text. A sheet with no text is not compared: say so.
- **The graphics are never compared**: symbols, how lines are routed and connected, flow arrows, line types, valve types. `notVerified` says so every time: tell the user a person must check these on the drawing. A clean text comparison is not a clean drawing.
- Off-page connectors and continuations are text and are compared as text; whether they point to the right sheet and line is yours to check by reading both.
- A note that is on the redrawn drawing with different line breaks is INFO ("read it on the drawing"), not a failure.
- The tool is tuned on synthetic drawings only. If a result looks wrong (a tag read as a label, a line number split), check the drawing with `pid_read` and say what you saw: do not report a tool artefact as a drafting error.
- Client rules (`client_requirements`, `requirements_check` for tag and line number formats): quote their source.

## Counts and sanity
The number of lines, tags and notes on each side is in the OK / FAIL findings. A different count with no listed difference means the reader missed something: look at both with `pid_read`.

You compare the redrawn drawing with the original; you do not judge the design.
