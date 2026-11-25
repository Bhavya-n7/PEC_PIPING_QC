using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

// "Pec.Qc.Mcp.exe init-shared [folder]" creates the shared office folder layout (sub-folders, README, hub.json) and prints what is there.
// For IT and the release manager; the desktop app and Setup do the same at first run. Never overwrites, never deletes.
if (args.Length > 0 && args[0] == "init-shared")
{
    var root = args.Length > 1 ? args[1] : Pec.Qc.Core.Settings.Load().SharedRoot;
    if (string.IsNullOrWhiteSpace(root)) { Console.Error.WriteLine("No folder: pass it as the second argument, or set the shared folder in the settings / office-defaults.json."); return 2; }
    var st = Pec.Qc.Core.Knowledge.SharedHub.Ensure(root);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { st.Reachable, st.Writable, st.Counts }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return st.Reachable && st.Writable ? 0 : 1;
}

// a tool written by Claude runs in its own process of this program (see Scripting\ScriptEngine): checked, run, result on stdout, then gone
if (args.Length >= 2 && args[0] == "--run-script") return Pec.Qc.Mcp.Scripting.ScriptEngine.ChildMain(args[1]);

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol: every log goes to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new Implementation { Name = "pec-qc", Version = "0.1.0" };
        o.ServerInstructions =
            "Tools for PEC's QC team: piping isometric, P&ID and GA drawing checks against the Navisworks model, the project inputs (PMS, line list, P&IDs, vendor data) and client rules. " +
            "QC is read-only: never change a model, drawing or client document; report differences with the drawing value, the source value and where each came from. " +
            "Start with qc_status and who_am_i, then learn_pull. Anything that cannot be decided from the sources is a REVIEW item, never a guess. " +
            "Tools that save something shared (skills, lessons, requirement packs, tools) take apply=false by default and return a plan: show it to the user and call again with apply=true only after they agree. " +
            "Call learn_record when the user corrects you.";
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

// lessons and activity kept on this PC while the shared folder was away go back to it now (in the background: never delays start)
_ = Task.Run(() => { try { Pec.Qc.Core.Knowledge.OfflineQueue.Sync(Pec.Qc.Core.Settings.Load()); } catch { } });
await builder.Build().RunAsync();
return 0;
