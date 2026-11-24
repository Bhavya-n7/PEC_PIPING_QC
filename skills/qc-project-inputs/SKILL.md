---
name: qc-project-inputs
description: How to find and quote what the project's own documents say: the PMS, the line list, the P&IDs and the vendor data. Read it before answering a question about a class, a line, a tag or a specification, and before any check that compares a drawing with project inputs.
---

# Project inputs

The inputs are the client's documents a drawing is checked against. They live in the active project's **inputs folder**, normally on the shared drive (`projects/<project>/inputs`), with a standard layout:

| Folder | Holds |
|---|---|
| `pms` | Piping Material Specification: class sheets, material and component descriptions |
| `line-list` | The line list (Excel or CSV): size, fluid, class, pressures, temperatures, insulation, P&ID number |
| `pid` | The client's P&IDs (and the redrawn ones when they are being checked) |
| `vendor` | Vendor data sheets and drawings |
| `other` | Anything else the project gave |

File names with "PMS", "line list", "P&ID" or "vendor" are recognised even outside these folders.

## Tools
- `inputs_list`: what is there, what each QC type still needs (`missing`), and which files **cannot be read** (scans, DWG). Call it first.
- `inputs_search`: passages with the **file and where** (page of a PDF, sheet and rows of Excel). A line number or tag is found whole or by a part (`B400A`, `60A07900`, `V-101`). `kind` narrows to one folder.
- `inputs_read`: the whole page, or rows of a sheet, for a hit you want to rely on.
- `line_list_summary`: which line list columns were recognised, which were not, duplicates, warnings.
- `line_list_lookup`: one line with every value, its unit, and file / sheet / row.

## How to answer
1. **Search, then read the page**, then answer. A snippet can cut a table row or a note; the page cannot.
2. **Quote the source every time**: "PMS-ACME.pdf, page 12, class B400A" or "LineList_Rev2.xlsx, sheet Line List, row 4". An answer without a source is not an answer.
3. **Say what you did not find, not what the project "does not say".** Only claim absence when every relevant file could be read. Look at `cannotBeRead` in the result: a scanned PMS has no text, so "not found" means nothing there.
4. **Several revisions?** If two files could both be the current one (rev A and rev B, different dates), say which you used and its date; ask which applies if it changes the answer.
5. **Units belong to the value.** The line list header says psig or barg, degF or degC; `line_list_lookup` keeps the unit. Never compare numbers with different or unknown units: that is a REVIEW. Convert only when both units are stated.
6. **A value that is blank in the source is a REVIEW**, not a pass and not a FAIL: "the line list does not give the operating pressure for this line".
7. **The same line twice in the line list** (`duplicates`) is itself a finding, with both rows.

## Example: what the PMS says
Question: "what does the PMS say for class B400A at 2 inch?"
1. `inputs_list` to confirm a PMS is there and readable.
2. `inputs_search` query `B400A 2 in` with `kind` pms.
3. `inputs_read` the page of the best hit.
4. Answer with the component rows for 2 in, and "Source: <file>, page <n>". If the page lists size ranges, say which range 2 in falls in.

## Never
- Never copy the client's documents anywhere. Read them where they are.
- Never fill a gap with general knowledge as if the project said it. If you add general knowledge, say it is yours.
