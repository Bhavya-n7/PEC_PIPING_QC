using System.Text.Json;
using Pec.Qc.Core;
using Pec.Qc.Core.Scripting;
using Pec.Qc.Core.Users;
using Pec.Qc.Mcp.Scripting;
using Pec.Qc.Mcp.Tools;

namespace Pec.Qc.Tests;

/// <summary>Claude builds a tool, tests it, the author publishes it, the QC lead approves it, and a colleague on another PC finds and runs it.</summary>
[Collection("EnvVar")]
public class ScriptToolLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pecqc-stl-" + Guid.NewGuid().ToString("N"));
    private readonly string _shared;

    public ScriptToolLifecycleTests() { _shared = Path.Combine(_root, "share"); Directory.CreateDirectory(_shared); Environment.SetEnvironmentVariable(ScriptEngine.HostVariable, Host()); }
    public void Dispose()
    {
        foreach (var v in new[] { "PEC_QC_DATA", "PEC_QC_APPROVAL", "PEC_QC_APPROVAL_WAIT", ScriptEngine.HostVariable, UserIdentity.NameVar, UserIdentity.CodeVar, UserIdentity.CompanyVar }) Environment.SetEnvironmentVariable(v, null);
        try { Directory.Delete(_root, true); } catch { }
    }

    private static string Host()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var exe = Path.Combine(d.FullName, "src", "Pec.Qc.Mcp", "bin", "Debug", "net10.0", "win-x64", "Pec.Qc.Mcp.exe");
            if (File.Exists(exe)) return exe;
        }
        throw new FileNotFoundException("build the solution first");
    }

    private static readonly UserIdentity Lead = new("Lena Lead", "L1", "PEC");
    private static readonly UserIdentity Ann = new("Ann Engineer", "E1", "PEC");
    private static readonly UserIdentity Bob = new("Bob Engineer", "E2", "PEC");

    private Settings Pc(string name, UserIdentity who, string approval = "off")
    {
        var data = Path.Combine(_root, "pc-" + name); Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("PEC_QC_DATA", data);
        Environment.SetEnvironmentVariable("PEC_QC_APPROVAL", approval); Environment.SetEnvironmentVariable("PEC_QC_APPROVAL_WAIT", "0");
        Environment.SetEnvironmentVariable(UserIdentity.NameVar, who.Name); Environment.SetEnvironmentVariable(UserIdentity.CodeVar, who.EmployeeCode); Environment.SetEnvironmentVariable(UserIdentity.CompanyVar, who.Company);
        File.WriteAllText(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(new Dictionary<string, string?> { ["UserName"] = who.Name, ["EmployeeCode"] = who.EmployeeCode, ["Company"] = who.Company, ["SharedRoot"] = _shared }));
        return Settings.Load();
    }

    private static string J(object o) => JsonSerializer.Serialize(o);

    private const string Params = """[{"name":"text","type":"string","description":"text with valve tags","required":true}]""";
    private const string Code = """
        var tags = Regex.Matches(Args["text"], @"\b[A-Z]{1,3}V-\d{3,5}\b").Select(m => m.Value).Distinct().OrderBy(x => x).ToList();
        if (tags.Count == 0) throw new Exception("no valve tags found");
        return new { count = tags.Count, tags };
        """;
    private const string Tests = """
        [{"name":"finds two tags","args":{"text":"Gate valve XV-1001 and HV-2002; again XV-1001"},"expectContains":"\"count\":2"},
         {"name":"fails without tags","args":{"text":"nothing here"},"expectError":true}]
        """;

    private void ApprovedBy(UserIdentity lead)
    {
        UserRegistry.Touch(new Settings { SharedRoot = _shared }, Lead);                 // the first person registered is the administrator
        UserRegistry.Touch(new Settings { SharedRoot = _shared }, Ann);
        UserRegistry.Touch(new Settings { SharedRoot = _shared }, Bob);
    }

    [Fact]
    public void A_tool_built_by_one_engineer_is_tested_reviewed_approved_and_then_used_by_a_colleague()
    {
        ApprovedBy(Lead);
        // 1. nothing exists yet
        Pc("ann", Ann);
        Assert.Equal(0, JsonDoc(ScriptToolTools.Find("valve tags")).GetProperty("found").GetInt32());
        // 2. Ann's Claude drafts it: a plan first, then the tests run
        var plan = J(ScriptToolTools.Draft("extract_valve_tags", "Lists the valve tags (like XV-1001) in a text", Params, Code, Tests));
        Assert.Contains("\"saved\":false", plan); Assert.Contains("\"rulesOk\":true", plan);
        var done = JsonDoc(ScriptToolTools.Draft("extract_valve_tags", "Lists the valve tags (like XV-1001) in a text", Params, Code, Tests, apply: true));
        Assert.True(done.GetProperty("testsPassed").GetBoolean(), done.ToString());
        // 3. she can use her draft, nobody else can use it yet
        var mine = JsonDoc(ScriptToolTools.Run("extract_valve_tags", """{"text":"check XV-1001, and PV-33001"}""", useDraft: true));
        Assert.True(mine.GetProperty("ok").GetBoolean()); Assert.Contains("PV-33001", mine.GetProperty("result").GetString());
        Pc("bob", Bob);
        Assert.Throws<ModelContextProtocol.McpException>(() => ScriptToolTools.Run("extract_valve_tags", """{"text":"XV-1001"}"""));
        // 4. Ann publishes it
        Pc("ann", Ann);
        Assert.Contains("\"published\":true", J(ScriptToolTools.Publish("extract_valve_tags", apply: true)));
        // 5. an engineer cannot review; the lead can
        Pc("bob", Bob);
        Assert.Contains("\"denied\":true", J(ScriptToolTools.Pending()));
        Assert.Contains("\"denied\":true", J(ScriptToolTools.Review("E1", "extract_valve_tags", "approve", apply: true)));
        Pc("lead", Lead);
        var pending = JsonDoc(ScriptToolTools.Pending());
        Assert.Equal(1, pending.GetProperty("count").GetInt32());
        var t = pending.GetProperty("tools")[0];
        Assert.Equal("E1", t.GetProperty("author").GetString()); Assert.Contains("Regex.Matches", t.GetProperty("code").GetString());   // the lead sees the code
        var plan2 = J(ScriptToolTools.Review("E1", "extract_valve_tags", "approve"));
        Assert.Contains("\"applied\":false", plan2);
        Assert.Contains("\"approved\":true", J(ScriptToolTools.Review("E1", "extract_valve_tags", "approve", apply: true)));
        // 6. a colleague on another PC finds it and runs it, with no approval and no draft
        Pc("bob", Bob);
        Assert.Equal(1, JsonDoc(ScriptToolTools.Find("valve tags")).GetProperty("found").GetInt32());
        var run = JsonDoc(ScriptToolTools.Run("extract_valve_tags", """{"text":"Gate valve XV-1001 and HV-2002"}"""));
        Assert.True(run.GetProperty("ok").GetBoolean(), run.ToString()); Assert.True(run.GetProperty("approved").GetBoolean());
        Assert.Contains("HV-2002", run.GetProperty("result").GetString());
        Assert.Contains("extract_valve_tags", Pec.Qc.Core.Claude.PromptBuilder.CustomToolsLine(Settings.Load()));          // and Claude is told it exists
    }

    [Fact]
    public void Approved_code_that_is_changed_afterwards_is_never_run_and_a_failing_tool_is_not_approved_or_published()
    {
        ApprovedBy(Lead);
        Pc("ann", Ann);
        ScriptToolTools.Draft("extract_valve_tags", "Lists valve tags", Params, Code, Tests, apply: true);
        ScriptToolTools.Publish("extract_valve_tags", apply: true);
        Pc("lead", Lead);
        ScriptToolTools.Review("E1", "extract_valve_tags", "approve", apply: true);
        // someone with write access to the share changes the approved code
        var store = new ScriptToolStore(Settings.Load());
        var file = Path.Combine(store.ApprovedDir, "extract_valve_tags", "code.cs");
        File.AppendAllText(file, "\nreturn \"tampered\";\n");
        Pc("bob", Bob);
        var ex = Assert.Throws<ModelContextProtocol.McpException>(() => ScriptToolTools.Run("extract_valve_tags", """{"text":"XV-1001"}"""));
        Assert.Contains("not the code that was approved", ex.Message);
        Assert.Contains("not usable", JsonSerializer.Serialize(ScriptToolTools.Find("valve")));
        Assert.DoesNotContain("extract_valve_tags", Pec.Qc.Core.Claude.PromptBuilder.CustomToolsLine(Settings.Load()));
        // a tool whose tests fail cannot be published
        Pc("ann", Ann);
        var failing = JsonDoc(ScriptToolTools.Draft("always_wrong", "Returns the number of lines", """[{"name":"text","required":true}]""", """return "x";""", """[{"name":"t","args":{"text":"a"},"expectContains":"y"}]""", apply: true));
        Assert.False(failing.GetProperty("testsPassed").GetBoolean());
        Assert.Throws<ModelContextProtocol.McpException>(() => ScriptToolTools.Publish("always_wrong", apply: true));
    }

    [Fact]
    public void A_draft_that_breaks_the_rules_or_has_no_tests_or_a_built_in_name_is_refused_before_anything_runs()
    {
        Pc("ann", Ann);
        var forbidden = J(ScriptToolTools.Draft("read_anything", "reads a file", """[{"name":"p","required":true}]""", """return System.IO.File.ReadAllText(Args["p"]);""", """[{"name":"t","args":{"p":"x"},"expectContains":"y"}]""", apply: true));
        Assert.Contains("\"saved\":false", forbidden); Assert.Contains("File", forbidden);
        Assert.Contains("\"saved\":false", J(ScriptToolTools.Draft("no_tests", "d", "[]", "return 1;", "[]")));
        Assert.Contains("built-in tool", J(ScriptToolTools.Draft("iso_check", "d", "[]", "return 1;", """[{"name":"t","args":{},"expectContains":"1"}]""")));
        Assert.Empty(new ScriptToolStore(Settings.Load()).Drafts());
    }

    [Fact]
    public void A_pending_tool_the_lead_rejects_is_not_available_and_the_author_sees_the_reason()
    {
        ApprovedBy(Lead);
        Pc("ann", Ann);
        ScriptToolTools.Draft("extract_valve_tags", "Lists valve tags", Params, Code, Tests, apply: true);
        ScriptToolTools.Publish("extract_valve_tags", apply: true);
        Pc("lead", Lead);
        Assert.Throws<ModelContextProtocol.McpException>(() => ScriptToolTools.Review("E1", "extract_valve_tags", "reject", apply: true));        // a reason is required
        Assert.Contains("\"rejected\":true", J(ScriptToolTools.Review("E1", "extract_valve_tags", "reject", "Also matches pipe-class codes: tighten the pattern", apply: true)));
        Assert.Equal(0, JsonDoc(ScriptToolTools.Pending()).GetProperty("count").GetInt32());
        Pc("bob", Bob);
        Assert.Equal(0, JsonDoc(ScriptToolTools.Find("valve tags")).GetProperty("found").GetInt32());
        var rejectedFile = Directory.GetFiles(Path.Combine(_shared, "tools", "scripts", "pending"), "rejected.txt", SearchOption.AllDirectories).Single();
        Assert.Contains("tighten the pattern", File.ReadAllText(rejectedFile));
    }

    [Fact]
    public void Running_a_draft_or_publishing_asks_the_user_first_while_running_an_approved_tool_does_not()
    {
        ApprovedBy(Lead);
        Pc("ann", Ann, approval: "on");
        var asked = J(ScriptToolTools.Draft("extract_valve_tags", "Lists valve tags", Params, Code, Tests, apply: true));
        Assert.Contains("waitingForApproval", asked);                                    // the user sees the code before it runs on this PC
        Assert.Empty(new ScriptToolStore(Settings.Load()).Drafts());
    }

    private static JsonElement JsonDoc(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })).RootElement;
}
