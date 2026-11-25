# Office rollout

Starts only after the pilot's one-page result says go. Everything the release manager needs is in `docs/runbook.md`; this page is the order of work.

## Before wave 1
- [ ] Pilot result signed off by the QC lead; open pilot problems fixed and released.
- [ ] Release key made and backed up; the build carries the public key; the share's `updates` folder is writable by the release manager only (`docs/security-review.md`).
- [ ] Code-signing certificate obtained and `scripts\sign-binaries.ps1` run (if not, tell people the "unknown publisher" warning is expected).
- [ ] Share permissions applied (security review), an administrator and at least one QC lead set in `users\`.
- [ ] The Autodesk MCP-server installers: decide per product whether Setup offers them (the Revit server's licence must be settled).
- [ ] Guide, quick start and a 30-minute walkthrough (`docs/demo-script.md`) ready; support hours announced.

## Waves
| Wave | Who | Gate to the next |
|---|---|---|
| 1 | the pilot engineers, now permanent users | a week without a blocking problem |
| 2 | one discipline team or half of the QC team | install success 100% after one retry; false-alarm rate still inside target |
| 3 | the whole QC team | all PCs on the same version |

For each PC: run Setup from the update folder, check the readiness list (Claude Code, Navisworks if needed, shared folder), first start (name, employee code), add the project, run the sample check from the quick start. Setup logs whether the install worked (`pilot\installs`).

## Support
- One named person and fixed hours for the first two weeks of each wave; problems go in one shared list with the PC, the drawing and what was on screen.
- The QC lead reviews **Lessons to review** and **Team activity** weekly: corrections and tool errors are where the assistant is wrong.
- Updates: a new version is published signed; PCs update themselves at start. Never publish on a Friday.

## Rollback
- A bad release: publish the previous version again (the script refuses to replace a newer version with an older one, so re-publish it with a higher version number and the old content, or ask the release manager to clear `latest.json` so PCs stop offering the bad one).
- A bad lesson or rule: reject it in **Lessons to review**; a bad requirement pack: save the previous one back (the `.bak_` copy is next to it).
- A PC with a problem: uninstall from Windows settings (leaves nothing behind) and reinstall.

## Done when
The QC team is on the stable version, the pilot metrics are still being collected for a month, and the QC lead has a monthly review in the calendar.
