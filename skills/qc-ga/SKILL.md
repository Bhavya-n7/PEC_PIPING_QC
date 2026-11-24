---
name: qc-ga
description: How to check a general arrangement (GA) drawing (PDF) against the Navisworks model, the P&IDs, the vendor data and the other project inputs. Read it before the first GA check in a chat.
---

# GA drawing check

Plan with `qc_checklist` (type ga): 7 items. Findings and the report: `qc-findings-and-reports`. Facts from the project inputs: `qc-project-inputs`.

## Know what tools you have
Check your tool list. The comparison with the Navisworks model needs a model tool; **without one, GA-01 and GA-02 (positions, dimensions, elevations against the model) are INFO "not verified (no model tool)"**. Everything that can be checked from the PDF and the inputs is still checked.

## Steps
1. **Read the drawing** with `pdf_read`: title block (drawing number, revision, date, scale), north arrow, grid references, equipment tags and names, dimensions, elevations, coordinates, nozzle orientation notes, general notes. A page with no text layer cannot be read: say so.
2. **Tags and services against the P&IDs**: for every equipment tag, `inputs_search` kind pid; the tag exists, the name and service agree. A tag on the GA that is on no P&ID is a finding (REVIEW if the P&ID set may be incomplete, FAIL if the P&ID for that area was read and the tag is not on it).
3. **Equipment against the vendor data**: `inputs_search` kind vendor for the tag or item: overall size, nozzle sizes, positions and orientations, weight. Quote the data sheet page.
4. **Against the model** (when a model tool exists): equipment present, position (E, N, U), elevation, size. The model is in mm; the drawing in the unit it states (feet-inches or mm): convert with the stated unit and a tolerance, and report the difference with both values.
5. **Drawing completeness**: north arrow, scale, grid, notes, revision and issue status. Missing is a REVIEW or FAIL as the client rule says (`client_requirements`).
6. **Access and clearance (GA-07)** is a judgement for the engineer: list what you looked at, never a pass or fail from text alone.
7. **Report** with `qc_report_write` (type ga).

## What usually matters
- A dimension that does not add up on the drawing (the parts do not equal the overall) is worth a finding with the numbers.
- Tag typos and look-alike characters (0/O, 1/I) between the GA and the P&ID.
- Units: always write the unit next to a number in a finding.
