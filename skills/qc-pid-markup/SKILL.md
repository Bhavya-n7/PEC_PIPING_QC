---
name: qc-pid-markup
description: The full P&ID QC with a markup PDF: the user gives two PDFs (the client's RAW P&ID with client markups, and our AutoCAD redraw = OUTPUT) and wants every difference found zone by zone and marked as clouds, comments and yellow highlights on the OUTPUT. Read it whenever the user asks for a P&ID check, a QC markup, or a comparison of a redrawn P&ID with its raw file.
---

# P&ID QC with a markup PDF (RAW against OUTPUT)

This is the office's own working prompt for P&ID checking (from the QC lead), turned into a procedure. It goes further than `qc-pid` (which compares text): here the whole drawing is checked, graphics included, and the result is a **markup PDF on the OUTPUT**. Also read `qc-engineer-mindset` and `qc-findings-and-reports`.

## 0. Start
1. **Identify RAW and OUTPUT yourself.** RAW = the client or vendor file: client markups (red text, cyan highlights, green "NOTE FOR CADDING" boxes, struck-through text), often a scan or a plain PDF export. OUTPUT = our AutoCAD redraw: clean, standard symbols, our title block. Say which is which and why. If you cannot tell, ask.
2. **Drawing number from the title block** (read it on the drawing with `pdf_render`, not from the file name). The result file is named `<drawing number>_QC-Markup.pdf`, saved next to the OUTPUT. The OUTPUT file itself is never changed.
3. **Plan the work and say it**: pages, zones, what you will check. A sheet is checked **zone by zone** (the drawing's own grid, or a 3 x 3 / 4 x 4 split of a large sheet).

## 1. Look at the drawing, do not only read its text
- `pdf_render` the whole page (dpi about 100) to see it, then **every zone at 200-300 dpi** (`rect` in PDF points) for RAW **and** OUTPUT. Open each picture with the Read tool and compare the same zone side by side. A graphic you did not look at is not checked: say so at the end.
- `pid_compare` / `pid_read` give you the text of both (line numbers, tags, notes) with zones: use them as the first list of text differences, then confirm every one on the picture. `pdf_find` gives exact positions of a text (PDF points).
- If a PDF is a scan (no text): read it from the pictures only and say that nothing was text-compared.

## 2. What to check (all of it, nothing skipped)
**A. Client markups.** Every markup on RAW must be in OUTPUT: red line numbers replacing XXX, cyan-highlighted items, struck-through text (is it removed?), "NOTE FOR CADDING" instructions (for example ball / butterfly valves replaced by the TCM standard symbols). List each markup as incorporated or not incorporated.

**B. Text and data.** Line numbers and sizes (1-1/2" vs 1/2" vs 1"); equipment, instrument and PSV tags (PSV-512F); PSV data boxes (set pressure, IN, OUT, orifice, case, ratings: 300 LB / 150 LB only where RAW shows them); PCV and control valve set pressures; equipment data tables, every row, label and value (psi vs psig); valve status labels NC / NO / FC / FO / LO / LC / ILO / ILC / CLOSE / V; pipe size text on lines and reducer sizes (3"x2", 6"x4", 14"x8"); notes, general notes, holds, equipment list, title block (wording, "AND" vs "&"), revision, date, prepared / checked / approved.

**C. Flow arrows, each one individually.** On every line, at every junction and tee on both sides where RAW shows them; at tank nozzles, equipment inlets and outlets, into and out of headers at each branch; on PSV outlets, ATM vents, flare risers, FREE DRAINING arrows, package inlet / outlet; the flow arrow under a self-actuated / regulator valve points the same way as in RAW; instrument signal arrows (PT to PLC to PI, TT to PLC to TI, LT to PLC to LI). Keep a count per zone (RAW arrows / OUTPUT arrows) so "checked OK" is a number.

**D. Drafting and symbols.** Valve symbols (ball, butterfly, gate, globe, check, control, XV, self-actuated) per TCM standard; equipment symbols as RAW (vaporizer / heater internals, outline thickness); line types (TCM interface dash-dot, signals dashed, process continuous) and line weights; line crossings (which line is broken, break symbols as RAW); VENDOR / TCM interface symbols (solid / hollow triangle, orientation, present in RAW, location); end caps, blinds, hoses, reducers, flanges, spectacle blinds, CL marks; text style and orientation (bold / underlined tags and titles, rotated text); the **VENDOR PACKAGE BOUNDARY** dash-dot line enclosing the complete package, routed as in RAW; border items (VENDOR / TCM split flags, grid zone references, position of data tables).

**E. Overlaps and clashes in OUTPUT.** Every text against linework and against other text (touching or crossed by a line, symbol, boundary line, leader or other text); symbol on symbol; lines too close to read. Mark in RED: "TEXT CLASH" or "OVERLAP" and the fix ("SHIFT TEXT CLEAR OF LINE").

**F. Spelling and grammar** of all text (notes, labels, tables, title block) and consistency (VAPOUR vs VAPOR, LIST vs LISTS, "SUBJECTED TO" vs "SUBJECT TO", placeholders such as "xx"). Error also in RAW: BLUE (client query). Only in OUTPUT: RED.

**G. Additions.** Anything in OUTPUT that is not in RAW (extra "XXXX" tags, headings, dates, names): a query.

**H. Raw issues.** Errors in RAW itself (a note referenced but missing, tag mismatch between table and drawing, duplicate tags): CLIENT QUERY (blue).

## 3. The markup PDF (`pdf_markup`)
- A **cloud around every error at its exact location**; a repeated error (Tank A and Tank B) gets **a cloud at each place**. `find` puts a cloud on a text; `rect` (from `pdf_find`, or measured from the `pdf_render` zones) on anything else (a symbol, an arrow, a line).
- The **comment is next to its cloud in a small box with a leader arrow** (the tool does this when the item has a `comment`: it places the box in free space, not on text, lines or other comments). **No numbered list, no separate comment schedule.**
- Comments in **CAPITALS**, short and specific: what RAW shows and what OUTPUT shows. Example: `LINE SIZE TO BE 1-1/2" AS PER RAW. OUTPUT SHOWS 1/2".`
- **RED** = correction required. **BLUE** = query / confirm / client query. **YELLOW HIGHLIGHT** (`type: highlight`) = every text or line in OUTPUT that was updated against RAW as per client markup (each new line number, changed value, changed line), placed on the item, text still readable.
- A **legend box** (`type: legend`) in an empty area: red / blue / yellow meaning, and a green "OK" line listing what was checked and found correct.
- The annotations are real (Polygon cloud, FreeText, Line, Highlight): editable in Bluebeam and Acrobat.
- Several pages: one `pdf_markup` call with items for every page (`page`), one output file. Read the result's `warnings` (text not found, no free space for a comment) and fix each one (`rect`, `at`, shorter text) before you hand over.

## 4. Check before you hand over
For every page, `pdf_render` each zone of the **markup PDF** at 200-300 dpi and look: every cloud sits on the right item, every highlight is on its item, every comment is readable and overlaps nothing. Correct and repeat. Say how many zones you looked at.

## 5. Chat summary after the PDF (short)
Corrections (count and the main ones); overlaps; spelling / grammar; queries (client queries separate); flow arrows checked OK (count per zone); client markups incorporated / highlighted (and those not incorporated). Then **what was not checked or not sure** (a scan you could only read from pictures, a zone that was too dense, symbols you could not tell apart): never say a drawing is clean when a part was not looked at. Offer to save the findings as a report (`qc_report_write`).

## Honest limits
- No tool compares drawing graphics automatically: the symbol, arrow, line-type and routing checks are your own reading of the zone pictures. Take your time, one zone at a time, and say how sure you are. A thing you cannot decide is a BLUE query, never a guess.
- Do not judge the design; compare OUTPUT with RAW (and with the TCM standard where the user gives it: ask for the symbol standard if it was not attached).
