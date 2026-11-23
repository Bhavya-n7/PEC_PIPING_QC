using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

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

await builder.Build().RunAsync();
return 0;
