---
name: qc-iso
description: How to check a piping isometric (spool drawing, PDF) against the Navisworks model, the PMS, the P&ID, the line list and the vendor data. Read it before the first isometric check in a chat.
---

# Piping isometric check

Method for one drawing, which may hold several lines and several sheets. Checklist: `qc_checklist` (type iso, 13 items). Findings and the report: skill `qc-findings-and-reports`. Where the facts come from: skill `qc-project-inputs`.

## The tools do the comparing, you report it
1. **`model_summary`** first. It says which exported model exists, when it was exported, how many lines it has, duplicated pipes and whether it is older than the model file. No exported model: say so; `model_export` makes one on a PC that has Navisworks (it takes up to a few minutes and asks the user to approve). A stale warning goes in the answer.
2. **`iso_check`** with the PDF (path, or its name in the project inputs folder). It reads the drawing and compares: identity (line in the model, duplicates, sheets 1..n, one revision, CONT. links, class and size in the line number), title block against the model, BOM against the model components (quantities and sizes by type), coordinates against model points and connected lines, cut pipe lengths against the BOM and the model, and where the project has them the line list (with units), the PMS class and the P&ID. A folder of drawings: **`iso_check_folder`**, then `iso_check` on the ones that need attention.
3. **Answer**: the verdict, then every FAIL and REVIEW with the drawing value, the source value and the source (it is in each problem), then `notVerified`. `iso_check` with `apply=true` saves the Excel and Markdown report after the user approves; offer it.

## Read the result honestly
- **FAIL** = the drawing and a source disagree. Both values and the source are in the finding; quote them. **REVIEW** = a person must look (a value missing, unclear, or the model disagrees with itself). **`notVerified`** = it was NOT checked: say so, never write it as a pass.
- "OK" findings are what matched. A verdict OK means nothing found among what could be compared, not that the drawing is right.
- Support codes (WE04, WG01...) are matched to model attachments by the **support map** in `iso-rules.json`. Until the QC lead adds it from the client's support collection, supports are REVIEW with the specs to add, and only their total is compared. Do not guess the mapping.
- PMS: only the class is looked up. Rating, material and schedule against the class sheet are **not** verified by the tool: for a PMS question use `inputs_search` kind pms and `inputs_read`, quote the page, and compare yourself, labelled as such.
- Vendor data (valve face-to-face) is not compared by the tool: INFO. Check by hand with `inputs_search` kind vendor when asked.
- A coordinate within the tolerance is OK; 25 mm off the model's own start point is a FAIL (a wrong label); far from every point is a REVIEW (a tee branch end, or an error).
- Gaskets and bolts are derived by the isometric tool and not modelled: INFO.
- A page with no text (a scan) cannot be read: REVIEW, say the sheet was not checked.

## Look closer
- `iso_read` shows exactly what was read from the drawing (title block, BOM, cut pipe, coordinates, links, warnings). A warning means that part was not read.
- `model_line` shows the line in the model: attributes, branches, components in order with positions (mm). Use both to explain a finding or to answer a question about either.
- Client rules (`client_requirements`, then `requirements_check` for the line number format and other pattern rules): quote the rule's source.

## What usually matters
- A value **blank in the model or the line list** is a REVIEW and the most common finding: list the exact field.
- When the model, the line list and the drawing do not all agree, show all values.
- The model is in mm, Pa and degC; the drawing in feet-inches, psig and degF. The tool converts with a tolerance; do not recompute by hand unless asked.
- Do not redesign: you check that numbers agree.
