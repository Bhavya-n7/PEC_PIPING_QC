---
name: qc-iso
description: How to check a piping isometric (spool drawing, PDF) against the Navisworks model, the PMS, the P&ID, the line list and the vendor data. Read it before the first isometric check in a chat.
---

# Piping isometric check

Method for one line, which may run over several sheets. Plan with `qc_checklist` (type iso): 13 items. Findings and the report: skill `qc-findings-and-reports`. Where the facts come from: skill `qc-project-inputs`.

## Know what tools you have
Check your tool list. This skill describes the whole check; **a comparison you have no tool for is INFO "not verified (no tool)", never a pass.** The comparison with the Navisworks model needs a model tool; if there is none, items ISO-01 (against the model), 05, 08, 09 and 10 stay unverified and you say so first.

## Steps
1. **Read the drawing.** `pdf_read` the pages (text layer). Note per sheet: line number, sheet x of n, revision, the title-block table (test / design / operating pressure and temperature, test medium, insulation, paint, PWHT, phase, P&ID number, class), the BOM (fabrication and erection, item, size, quantity), the cut pipe table, coordinate labels, support tags, continuation (`CONT. ON / FROM`) lines and drawings, notes and hold items. If a page has no text, say the sheet cannot be read.
2. **Line list** (`line_list_lookup` on the line number): size, fluid, class, pressures, temperatures, insulation, P&ID number. Compare with the title block, field by field, **with units** (the isometric prints psig and degF). Missing in the line list = REVIEW.
3. **Line number parts**: size, fluid code, area, class in the line number must agree with the line list and with the title block (`PIPNG CLASS`).
4. **PMS** (`inputs_search` kind pms for the class and size, then `inputs_read`): component descriptions on the BOM (material, rating, schedule, ends, flange facing, gasket, studs) against the class sheet. Quote the page.
5. **P&ID** (`inputs_search` kind pid for the line number): the P&ID number on the drawing shows this line; the line's size, class and fluid on the P&ID agree.
6. **Vendor items** (valves, special items): tag in `inputs_search` kind vendor; rating, face to face, weight.
7. **Across sheets**: add the BOM quantities of all sheets of the line before comparing with the model; the sum of the cut lengths must equal the pipe quantity on the sheet; sheet numbers 1..n all present; one revision on all sheets; continuation drawings exist.
8. **Model** (when a model tool exists): line present, attributes, component counts by type and size, start / end / continuation coordinates, pipe length, supports. The model is in mm, Pa and degC; the isometric in feet-inches, psig and degF: convert with stated units and a tolerance (a coordinate to 1/16 inch).
9. **Client rules** (`client_requirements` for the client, then `requirements_check` for the line number format and other pattern rules; apply the manual rules yourself and quote their source).
10. **Report** with `qc_report_write` (type iso).

## What usually matters
- A value that is **blank in the model or the line list** is a REVIEW, and it is the most common finding. List it with the exact field.
- When the model, the line list and the drawing do not all agree, show all three values.
- Gaskets and bolts are normally not in the model: INFO, not a failure.
- Part numbers on the drawing (item numbers) are not in the model; compare by component type, size and specification.
- Do not recompute the drawing's dimensions: you check that numbers agree, you do not redesign.
