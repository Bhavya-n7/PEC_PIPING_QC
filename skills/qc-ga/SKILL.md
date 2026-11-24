---
name: qc-ga
description: How to check a general arrangement (GA) drawing (PDF) against the Navisworks model, the P&IDs, the vendor data and the other project inputs. Read it before the first GA check in a chat.
---

# GA drawing check

Checklist: `qc_checklist` (type ga), 7 items. Findings and the report: `qc-findings-and-reports`. Facts from the project inputs: `qc-project-inputs`.

## The tools do what they can, you say what they did not
1. **`model_summary`** first: the GA check needs an exported model **with equipment** (`equipment` count > 0). An older snapshot has none and says so (`equipmentWarning`): export again with `model_export` (asks the user to approve).
2. **`ga_check`** with the GA PDF. It reads the text and compares:
   - equipment **tags** against the model's equipment (any spelling: 60-D-302 = 60D302). A tag one character from a model tag, or text one character from one (60D3O2), is a **FAIL** with both. A tag not in the model at all is a REVIEW (it may be a pipe, an instrument or a differently named item).
   - **E / N / U coordinate labels** next to a tag against the equipment's extent in the model (the box the model gives, in mm; imperial labels are converted). Outside the extent plus the tolerance (100 mm) is a **FAIL** with the distance, both values and the extent.
   - **elevations** (`EL +...`) against the equipment's height range: outside is a REVIEW only, because what an elevation refers to (top of steel, centreline) is not known.
   - **tags against the P&IDs and the vendor data** of the project inputs: found = OK with the file and page; not found = REVIEW (the set may be incomplete, a tag may be drawn as graphics).
   - **drawing completeness**: scale, north arrow (as text) and revision found, else REVIEW.
3. **Answer**: the verdict, every FAIL (both values, the model item), the REVIEWs, then `notVerified`. `apply=true` saves the Excel and Markdown report after the user approves; offer it.
4. `ga_read` shows what was read (tags, coordinates in mm, elevations, scale, revision). Use it to explain a finding or to answer a question about the drawing; `model_summary` and `model_line` for the model side.

## Not checked, always say so
- **Dimensions** and equipment sizes against the model extents; **nozzle** positions and orientations, and sizes, against the vendor data (read the data sheet with `inputs_search` kind vendor and `inputs_read`, compare by hand and label it as yours).
- **Access and clearances (GA-07)**: a judgement for the engineer: list what you looked at, never a pass or fail from text.
- Equipment of the model that is not on the drawing is not reported: what area the drawing covers is not known.
- A scan has no text: not checked.

## Be careful
- The reader is tuned on drawings I generated plus the real model, not on a real client GA template. If a result looks odd (no tags, coordinates not attached to a tag), look at `ga_read` and say what was read: do not report a tool artefact as a drafting error.
- Always write the unit next to a number. The model is in mm; a coordinate written as a plain number is taken as mm and the result says so.
- Tag typos and look-alike characters (0/O, 1/I) between the GA and the P&ID are worth a finding with the exact characters.

## A piping plan (the client's GA sheets)
A GA may be a piping plan per tier: line numbers such as `GH5174-2111-AOC-4"-11731009-NI` with `EL 102.615 m` beside each, matchlines, `CONT. ON DRAWING 1-GH5171-28-GAS-4300-00`, `TIER ELEVATION` and `PLAN FROM .. TO ..`. `ga_read` returns them (`lineNumbers` with size in inches and elevation in mm, `references`, `matchlines`, `tierElevation`, `planRanges`). `ga_check` then compares the **line numbers**:
- against the model's pipes (size in the line number against the bore: a FAIL when it differs). If NONE is in the model it says so once: ask whether it is the right model, do not list them one by one;
- against the **line list** (size in inches; not found is a REVIEW) and the **P&ID text** (not found is a REVIEW);
- the drawings it continues on that are not in the project inputs are listed as not followed.
It does **not** judge a line number that appears with different sizes (branches normally have another size) or an elevation outside the sheet's plan range (vertical pipes): do not report those as errors.
A title block drawn as lines has no text: scale is read, revision and north arrow are then REVIEW ("look at the drawing"), once for the whole drawing.
