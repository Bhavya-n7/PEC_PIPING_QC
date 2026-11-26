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

## 1. Look at the drawing, by the sheet's own zones (never random cuts)
Drafting practice: a sheet is checked by the **grid zones of its border** (columns numbered along the top and bottom, rows lettered along the sides; a point is "zone J-12"), the same zones on the client's drawing and on ours. Do not cut a sheet into arbitrary pieces.
1. **`pdf_zones` on RAW and on OUTPUT** (page by page; give `cols` / `rows` with the labels you read on the border, for example `17-1` and `K-A`). It reads the real page size (A0 to A4, either orientation), finds the sheet's own grid from the border tick marks (the ISO 5457 grid of that size if there are none, and it says so), and gives review tiles that follow the grid with the zoom that suits the sheet (a scan is never rendered above its own resolution). **Give `across` / `down` the same on both drawings of a pair**, even when the sizes differ (an A3 scan against an A0 redraw): tile T2.2 of RAW is then the same part of the drawing as tile T2.2 of OUTPUT.
2. **`pdf_render` every tile** (`rect` and `dpi` from the plan) for RAW and for OUTPUT, open both pictures with the Read tool and compare them side by side. The overview (whole page, dpi about 40-60) first, then the tiles. Look into a tile more closely (a smaller `rect`, higher dpi) when something is dense. A graphic you did not look at is not checked: say so at the end.
3. Text: if a side has a text layer, `pid_compare` / `pid_read` / `pdf_find` give the text with positions: use them as the first list of text differences, then confirm each on the picture. If a side has **no text layer** (a scan, or text drawn as lines: `pdf_zones` says so), nothing is text-compared: read it from the pictures and say so.
4. Marks on the OUTPUT: `pdf_find` gives a box when the text layer exists; otherwise measure the item **on the tile picture** and give `pdf_markup` `"in": {"rect": <the tile rect>, "dpi": <the dpi you rendered>}` and `"px": [x0, y0, x1, y1]` (pixels of that picture, origin top left). Marks, comment text and line weights are scaled to the sheet size by the tool, so they read the same on A3 and on A0.

## What the real client sheets look like (learned from the project files)
- **RAW** is typically an **A3 scan** (about 200 dpi, no text layer) of a hand-marked "as built" print: red pen = changes (new sizes such as `3/4"`, `1 1/2" (TYP.)`, struck or crossed items, new valves drawn in red, "NOT IN USE"), blue pen = ringed vendor / analyser scope or items to check, yellow highlighter = lines or items the client has verified. Handwriting is read from the picture: when it is not clear, it is a BLUE query, never a guess. Do not assume what a colour means for the client: if the drawing does not say, ask the QC lead once and record it with `learn_record`.
- **OUTPUT** is typically an **A0 vector drawing** (our redraw, same drawing number and the same border grid as the client's sheet) whose text is drawn as lines: no text layer, so nothing can be searched; everything is read from pictures. The page is often **rotated** (landscape shown from a portrait page): the tools handle it, positions are always as displayed.
- **The PDFs do not line up page by page**: the OUTPUT book has a cover sheet and a list of drawings in front, the RAW book may have different pages or a different order. Pair sheets by the **drawing number in the title block**, never by page number. Check the list of drawings against the sheets present (a sheet in the list and not in the book, or the reverse, is a finding), and the revision, date and title block of each sheet.
- The OUTPUT shown "without markup" is the redraw **before** the client's hand markups are incorporated: then every markup on RAW is "not incorporated" until the user says the redraw is the corrected one. Ask which one it is when it is not stated.
- Each area comes with a **drawings list and comments sheet** from the client or the office (the comment list: things such as "sampling connections not in use", "instrument tags not available at site", "drawing done as NOT IN USE"). Read it (`file_read` on the Excel) before you check the area: it tells you what is known and what is deliberate, and a deliberate item is a query to confirm, not an error.
- **Office comment style** (from the team's own comment sheets): keyed by line number or tag, short, one fact: `VALVE NOT FOUND`, `DRAIN NOT FOUND`, `LINE NOT ROUTED`, `PARTS MISMATCH`, `INSTRUMENT NOT FOUND`, `SPECTACLE BLIND AND DRAIN NOT FOUND`, with a status column. Keep that brevity on the drawing, and add what RAW shows and what OUTPUT shows.
- Typical items on these P&IDs: instrument bubbles (tag over number, circled / squared, 12LG 103A style), interlock diamonds with a number, line numbers `size-service-area-seq-class` (for example `18"-SG-12-101-D1A`) and their size changes at reducers, vessel data tables, notes with note numbers referenced on the drawing (a referenced note that does not exist is a RAW issue), off-sheet connectors with a drawing number, spec breaks (`D6A | A6A`), vendor scope boxes, "NOT IN USE" areas drawn as a cloud, hatching for underground.

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
- A **cloud around every error at its exact location**; a repeated error (Tank A and Tank B) gets **a cloud at each place**. `find` puts a cloud on a text (needs a text layer); on anything else (a symbol, an arrow, a line, or any text on a sheet without a text layer) use `rect`, or `in` + `px` measured on the tile picture.
- The **comment is next to its cloud in a small box with a leader arrow** (the tool does this when the item has a `comment`: it places the box in free space, not on text, lines or other comments). **No numbered list, no separate comment schedule.**
- Comments in **CAPITALS**, short and specific: what RAW shows and what OUTPUT shows. Example: `LINE SIZE TO BE 1-1/2" AS PER RAW. OUTPUT SHOWS 1/2".`
- **RED** = correction required. **BLUE** = query / confirm / client query. **YELLOW HIGHLIGHT** (`type: highlight`) = every text or line in OUTPUT that was updated against RAW as per client markup (each new line number, changed value, changed line), placed on the item, text still readable.
- A **legend box** (`type: legend`) in an empty area: red / blue / yellow meaning, and a green "OK" line listing what was checked and found correct.
- The annotations are real (Polygon cloud, FreeText, Line, Highlight): editable in Bluebeam and Acrobat.
- Several pages: one `pdf_markup` call with items for every page (`page`), one output file. Read the result's `warnings` (text not found, no free space for a comment) and fix each one (`rect`, `at`, shorter text) before you hand over.

## 4. Check before you hand over
For every page, `pdf_render` each **tile** of the **markup PDF** (same tile plan, same dpi) and look: every cloud sits on the right item, every highlight is on its item, every comment is readable and overlaps nothing. Correct and repeat. Say how many zones you looked at.

## 5. Chat summary after the PDF (short)
Corrections (count and the main ones); overlaps; spelling / grammar; queries (client queries separate); flow arrows checked OK (count per zone); client markups incorporated / highlighted (and those not incorporated). Then **what was not checked or not sure** (a scan you could only read from pictures, a zone that was too dense, symbols you could not tell apart): never say a drawing is clean when a part was not looked at. Offer to save the findings as a report (`qc_report_write`).

## Honest limits
- No tool compares drawing graphics automatically: the symbol, arrow, line-type and routing checks are your own reading of the zone pictures. Take your time, one zone at a time, and say how sure you are. A thing you cannot decide is a BLUE query, never a guess.
- Do not judge the design; compare OUTPUT with RAW (and with the TCM standard where the user gives it: ask for the symbol standard if it was not attached).
