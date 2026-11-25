# Security review (Phase 10)

Scope: the PEC QC Assistant as built in this repository: the desktop app, the MCP server (60 tools), Setup and the update path, the shared office folder. Method: an inventory of every tool and what it can write, a read of every place the code starts a process, opens a network connection, writes outside its own folder or handles a credential, and tests that fail when a fix is taken out. **This is a developer's review. The security sign-off (Phase 10 exit) is a decision for PEC's IT / security owner; the open items below are what they need to decide.**

## What the product does and does not touch
| Area | Finding |
|---|---|
| Network | Only the **update check and download** (an optional web address; normally a folder on the share). Nothing else in the code opens a connection. The Claude Code CLI makes its own connection to Anthropic with the user's own sign-in: **what a user types, and the text of files the assistant reads for them, goes to Anthropic**. The guide must tell users that client drawings are processed by Claude. |
| Credentials | None stored by the app. Claude sign-in stays the user's own. No API keys, no passwords in settings. The release signing key is **off the share and out of the repository** (see below). |
| Processes started | The Claude CLI, the Navisworks runner (a copy of the model is opened by a hidden Navisworks; nothing is saved), Explorer / the default app for opening a folder or file the user clicked, Setup. No tool runs a command line given by the model: custom tools are recipes of the built-in tools only. |
| Registry | Per-user only: the uninstall entry. No administrator rights are needed or used. |
| Navisworks | The product references the user's own Navisworks DLLs and never copies them into the package (checked in the payload: only the two small tools of ours). |

## Findings and what was done
| # | Finding | Risk | Status |
|---|---|---|---|
| S1 | **The auto-update ran `PecQcSetup.exe` from the update folder without any check.** The share lets every user create and overwrite files, so anyone could have pushed code to every PC. | High | **Fixed.** `latest.json` is signed (RSA-PSS, SHA-256) with a release key kept off the share; every PC holds only the public key (from the shipped `office-defaults.json`, which a settings file cannot override). An unsigned, wrongly signed or altered release is never offered; the SHA-256 of Setup and of `payload.zip` are checked before Setup starts. Tested incl. three mutations and a round trip PowerShell signer / C# verifier. **Until the key pair is made and a build is released with it, PCs still accept unsigned updates:** see "To do before the pilot". |
| S2 | The Office writers (`excel_write`, `word_write`, `pdf_write`, `pdf_merge`, `pdf_extract`, and `saveAs` of the editors) could **replace any existing file** the user can write, with no question, when `overwrite=true`: text inside a document the assistant reads could make it do that. | Medium | **Fixed.** Replacing an existing file asks the user. New files and in-place edits (with `.bak_` backup) are as before. |
| S3 | `activity_report` had **no role check**: any engineer could read everyone's prompts. | Medium | **Fixed**: qc-lead / admin only (also `activity_summary`). |
| S4 | `convert_training` let any user rewrite the **shared knowledge pack** that everyone reads. | Medium | **Fixed**: qc-lead / admin and the user's approval. |
| S5 | Lessons are usable by everyone the moment they are recorded, so a **wrong or planted lesson** spreads at once. | Medium | **Mitigated**: unreviewed lessons are shown to Claude as hints labelled "not yet reviewed" that do not override project documents or the curated list; the QC lead approves or rejects; rejected ones disappear for everyone. A planted lesson still reaches Claude's context as a hint until rejected. |
| S6 | Credentials typed into a chat could be copied into lessons and the activity log. | Medium | **Improved**: bearer tokens, `sk-` keys, AWS keys, private key blocks, `user:password@host` and connection strings are now scrubbed in addition to `password=...` patterns. A scrubber cannot find every secret: users must not type them. |
| S7 | The activity log keeps **full prompts and answers**, readable by anyone who can open the share's `activity` folder, and those may hold client data. | Medium | **Option added**: `ActivityDetail = "metadata"` in settings (or the shipped defaults) records who, when, which tool and whether it worked, and nothing that was typed, read or answered. Default is `full`. PEC decides which. |
| S8 | **Roles are enforced by the tools, not by the file system.** The registry file `users\<code>.json` is writable by its owner and anyone with write access to the share can edit it to become admin. | Medium | **Not fixable in the code**: needs share permissions. Recommended below. Every role change through the tools is audited; an edit by hand is not. |
| S9 | The logs on the share (`audit`, `activity`, `learning\inbox`) can be edited by anyone with write access: they are a record, not tamper-proof evidence. | Low | Accepted; appends are the only thing the code does. ACLs below make it stronger. |
| S10 | `PEC_QC_APPROVAL=off` (an environment variable / the user's approval setting) turns approvals off. | Low | Accepted: it is the user's own choice on their own PC. The installer registers the tools with approvals **on**. |
| S11 | Path handling: project and model names become folder names. | Low | Checked: names are reduced to a single safe folder name (no separators, never `.` or `..`); tests added. Tools that read files read what the user can read (the server runs as the user). |
| S12 | The packaged executables are **not code-signed**: Windows shows "unknown publisher" and SmartScreen may warn. | Low–Medium | **Script ready, certificate needed.** `scripts\sign-binaries.ps1` signs Setup and the executables in the payload and refreshes the hashes in `latest.json` (tested with a throw-away self-signed certificate). A certificate from a certificate authority is required for the SmartScreen benefit. |
| S13 | Antivirus | — | Windows Defender (signatures of the day of the build) found no threat in `PecQcSetup.exe` and `payload.zip`. Other products and SmartScreen were **not** tested; unsigned single-file executables are a known source of false positives. |

## Share permissions recommended (done by the server administrator, not by the product)
The share is used with: create, overwrite and append allowed for users; delete denied. On top of that:
- `updates\`: **write for the release manager only**, read for everyone (S1 is then doubly covered).
- `users\`: users may create their own `<code>.json`; consider modify rights for administrators only after creation, or accept S8.
- `skill\`, `learning\approved\`, `requirements\`, `prompts\`: write for QC leads and administrators; read for everyone.
- `audit\`, `activity\`: append / create for everyone; read for QC leads and administrators only (read-only for the rest).
- `models\`, `reports\`, `projects\`: create / overwrite for everyone, as now.

## Tools written by Claude and shared (script tools)
- **What it is**: C# scripts written by Claude at a user's request, shared through the QC lead. They are code from a model, run on the engineers' PCs: treat them like macros.
- **Default changed (owner's instruction)**: tools are approved directly by their author and may use any .NET library. This means code written by a model, seen only by the author, runs with the user's rights on every PC that uses it. Set `ScriptReview` to `lead` and `ScriptSandbox` to `strict` in settings (or office-defaults) to get the controls below. With the defaults only controls (1), (3) and (6) apply.
- **Controls** (in order of weight): (1) the user approves the code before the first run of a draft (approval mode task / strict); (2) the QC lead reads the code and re-runs the author's tests before approving; (3) the approved code is pinned by SHA-256, changed code is never run; (4) a rules filter refuses network, processes, reflection, registry, environment, `System.IO.File/Directory`, native code, `dynamic` and extra libraries (tested: 14 kinds refused, mutation-checked); (5) files only through the `Qc` object (given files and project inputs only; writes limited to text-like extensions in one folder); (6) own process with a time limit, killed with its children.
- **Residual risk**: the filter is a policy check on the compiled code, **not a hard sandbox**; the process runs with the user's rights and could use a gap in the filter. Anyone with write access to `tools\scriptspproved` can replace code, but then the hash no longer matches and the tool is refused; they could also rewrite the hash file, so keep that folder write-restricted to the QC lead and administrators (same share advice as above). The review only protects if the lead actually reads the code.
- Not done: running tools in an OS-level sandbox (AppContainer / job object with no file access), signing approvals with a key.

## MCP servers for the Autodesk programs (installed by the program)
- They are code from other publishers, run with the user's rights, and one of them (Revit) has an unclear licence. The installer only fetches from the publishers' own release pages (GitHub, PyPI, nodejs.org, dot.net), verifies the published SHA-256 of each package before extracting it, refuses archives that write outside their folder, backs up and keeps the user's existing Claude settings, and never kills a running program. It cannot verify more than the publisher publishes: a compromised publisher release with a matching checksum would be installed. Civil 3D builds from a pinned source commit with Node.js and the .NET SDK downloaded from their publishers.
- Not done: signature checks of the downloaded executables beyond the publishers' checksums, an allow-list of publishers set by IT, an uninstall button.

## What is NOT covered
- A penetration test, and a review of the Claude Code CLI or of the MCP SDK. Dependencies: `dotnet list package --vulnerable --include-transitive` reported no known vulnerable package on the day of the build; the build no longer hides the NuGet advisory warnings (`NU1902` / `NU1903`), so a new advisory shows up as a warning. Run it again before each release.
- SmartScreen reputation, other antivirus products, an install on a PC that has no .NET, no Claude Code and no Navisworks (no clean-PC / sandbox machine was available; the package was run from an unzipped folder instead).
- Data protection obligations of PEC's clients (what may be sent to a cloud model). That is a policy decision; the metadata option and the "client data goes to Anthropic" notice exist to support it.

## To do before the pilot (needs a person)
1. Make the release key pair (`scripts\new-release-key.ps1`), back up the private key, put the public key and the private key's path in `config.local.json`, rebuild, publish: **from then on every PC refuses unsigned updates**. Install this build on the pilot PCs by hand once (it carries the key).
2. Buy / obtain a code-signing certificate and run `sign-binaries.ps1` before publishing.
3. Decide `ActivityDetail` (`full` or `metadata`) and the share permissions above.
4. Run the NuGet vulnerability check again before the pilot build.
5. Sign-off by IT / security.
