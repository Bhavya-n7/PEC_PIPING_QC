---
name: qc-pid
description: How to check a redrawn P&ID (PDF) against the client's original P&ID (PDF): every line, equipment, instrument, valve, tag and note copied accurately and completely. Read it before the first P&ID check in a chat.
---

# P&ID check (redrawn against the original)

Plan with `qc_checklist` (type pid): 7 items. Findings and the report: `qc-findings-and-reports`. The original comes from the project inputs (`pid` folder); the redrawn one is the file the user names.

## Know what tools you have
Check your tool list. If a P&ID comparison tool exists, use it and then verify a few of its findings yourself. If not, you work from the **text of both PDFs** (`pdf_read`). Text cannot show symbols or how lines are routed: say so, and mark graphic content **INFO "not verified"**. If a PDF has no text layer (a scan), the check cannot be done on text: say so for that sheet.

## Steps
1. **Find the original**: `inputs_list` / `inputs_search` kind pid for the sheet number or drawing number. Confirm it is the same sheet and revision as the redrawn one; if the revisions differ, say it first.
2. **Read both** with `pdf_read` (same sheet). Write down, for each, the set of:
   - line numbers (size, fluid, area, class, sequence), with their notes (insulation, tracing, slope)
   - equipment tags and names (and ratings or sizes printed beside them)
   - instrument tags (function letters and loop number)
   - valve tags and types, and special items
   - notes, hold items, legends, revision block, title block
3. **Compare the sets**, both ways:
   - on the original but **missing** on the redrawn one
   - on the redrawn one but **not** on the original (extra)
   - on both but **different** (a character in a tag, a size, a class, a note's wording)
4. **Tag hygiene**: look for look-alike characters (0/O, 1/I/l, 5/S, 8/B), dropped suffixes (V-101A vs V-101), swapped digits. Report the exact characters on each side.
5. **Counts**: the number of lines, equipment, instruments, valves per sheet on each side. A different count with no listed difference means you missed one: look again.
6. **Client rules** (`client_requirements`, `requirements_check` for the tag and line number formats).
7. **Report** with `qc_report_write` (type pid). One finding per difference, each with both values; the source value is the original, with its sheet.

## What usually matters
- **Missing** items are the serious ones: a missing line, valve or instrument is a FAIL ("on the original, not on the redrawn one").
- Drawing text split across lines (a long note, a tag over two lines) may read in a different order: compare the content, not the order, and quote both.
- Sheet continuation (off-page connectors) must point to the same sheet and line on both sides.
- You compare the redrawn drawing with the original; you do not judge the design.
