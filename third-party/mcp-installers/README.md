# MCP server installers for the Autodesk programs

The company's own one-click installers for third-party MCP servers, bundled **unmodified** in the installer package (`mcp-addons\` in the app folder) and offered by Setup:

| Id | Installer | For | Installs (from its publisher, when the script runs) | Licence of the server |
|---|---|---|---|---|
| navisworks | `Install-NavisworksMCP_2.cmd` | Navisworks Manage / Simulate 2022+ | nwd-mcp and NavisCoord plug-ins and servers | Apache-2.0 / MIT |
| autocad | `Install-AutoCADMCP_2.cmd` | AutoCAD 2022-2027 | dwg-mcp plug-in and server | Apache-2.0 |
| civil3d | `Install-Civil3DMCP_3.cmd` | Civil 3D 2025 / 2026 | builds the Civil 3D plug-in and MCP server (Node.js and .NET 8 SDK in the user folder) | MIT |
| plant3d | `PlantMCP.cmd` | AutoCAD Plant 3D 2022-2027 | the same dwg-mcp server, enabled in Plant 3D | Apache-2.0 |
| revit | `RevitMCP.cmd` | Revit 2022-2027 | Revit add-in, uv, the Model MCP server; sets user variables so uv uses the Windows certificates | **not stated in the installer: check before wide use** |

- `manifest.json` lists them with the SHA-256 of each script and the rule that tells Setup whether the Autodesk program is on the PC. **Setup runs a script only if its SHA-256 equals the manifest's.** After replacing a script run `scripts\make-mcp-addon-manifest.ps1` (the build does it too).
- Setup ticks an installer when its program is found, runs each ticked one in its own window after the QC tools are installed (they ask questions, may close the Autodesk program and Claude, and download from the internet), and never runs them for an automatic update. Uninstalling the QC Assistant does **not** remove them (they are separate products).
- A user can run any of them again later by double-clicking the script in `<app folder>\mcp-addons\`.
- These servers are not part of the QC Assistant and are not covered by its tests or its security review: they run inside the Autodesk programs and can read and change what is open in them.
