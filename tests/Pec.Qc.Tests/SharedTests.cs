using System.Text.Json.Nodes;
using Pec.Qc.Core;
using Pec.Qc.Core.Knowledge;
using Pec.Qc.Core.Tools;

namespace Pec.Qc.Tests;

public class SharedFolderTests
{
    [Fact]
    public void One_shared_root_puts_everything_inside_it_and_each_part_can_still_be_moved()
    {
        var s = new Settings { SharedRoot = @"X:\Office\Spoolgen_MCP" };
        Assert.Equal(@"X:\Office\Spoolgen_MCP\skills", s.EffectiveSkillsDirectory);
        Assert.Equal(@"X:\Office\Spoolgen_MCP\learning", s.EffectiveKnowledgeDirectory);
        Assert.Equal(@"X:\Office\Spoolgen_MCP\context", s.EffectiveContextDirectory);
        Assert.Equal(@"X:\Office\Spoolgen_MCP\documents", s.EffectiveDocumentsDirectory);
        Assert.Equal(@"X:\Office\Spoolgen_MCP\tools", s.EffectiveToolsDirectory);
        Assert.Equal(@"X:\Office\Spoolgen_MCP\prompts\master-prompt.md", s.EffectiveMasterPromptFile);
        Assert.Equal(@"X:\Office\Spoolgen_MCP\prompts\quick-tasks.json", s.EffectiveQuickTasksFile);
        Assert.Equal(@"X:\Office\Spoolgen_MCP\updates", s.EffectiveUpdateSource);
        s.SkillsDirectory = @"Y:\other\skills";
        Assert.Equal(@"Y:\other\skills", s.EffectiveSkillsDirectory);
        Assert.Equal(@"X:\Office\Spoolgen_MCP\learning", s.EffectiveKnowledgeDirectory);
        Assert.Null(new Settings().EffectiveUpdateSource);                          // nothing shared, nothing to check
    }

    [Fact]
    public void Hub_is_created_with_its_layout_and_a_readme()
    {
        using var t = new TempDir();
        var root = System.IO.Path.Combine(t.Path, "Spoolgen_MCP");
        var st = SharedHub.Ensure(root);
        Assert.True(st.Reachable);
        Assert.True(st.Writable);
        foreach (var f in SharedHub.Folders) Assert.True(Directory.Exists(System.IO.Path.Combine(root, f)), f);
        Assert.Contains("tools", File.ReadAllText(System.IO.Path.Combine(root, "README.md")));
        File.WriteAllText(System.IO.Path.Combine(root, "README.md"), "mine");
        SharedHub.Ensure(root);
        Assert.Equal("mine", File.ReadAllText(System.IO.Path.Combine(root, "README.md")));      // never overwritten
    }

    [Fact]
    public void Context_notes_go_to_the_context_folder_and_other_knowledge_to_learning()
    {
        using var t = new TempDir();
        var learning = System.IO.Path.Combine(t.Path, "learning"); var context = System.IO.Path.Combine(t.Path, "context");
        var k = new KnowledgeStore(learning, context);
        k.Save("Context: Reliance", "Erection sheets A3.", "context", "session", "Reliance");
        k.Save("Weld numbering", "J, F, Y", "client-rule", "PP-028 p8", "Reliance");
        Assert.True(File.Exists(System.IO.Path.Combine(context, "context-reliance.md")));
        Assert.True(File.Exists(System.IO.Path.Combine(learning, "weld-numbering.md")));
        Assert.Equal(2, k.All().Count);
        Assert.Equal("Context: Reliance", k.Recall("erection sheets").First().Entry.Title);
        Assert.True(k.Delete("context-reliance"));
        Assert.Single(k.All());
    }
}

public class CustomToolTests
{
    private static readonly IReadOnlySet<string> Known = new HashSet<string> { "preflight_check", "pcf_check", "import_pcf", "generate_drawings", "run_custom_tool", "create_custom_tool" };

    private static CustomToolDef Sample(string name = "check_and_import") => new(
        name, "Check the PCFs in a folder, then import them.",
        [new CustomToolParam("folder", "Folder with PCFs"), new CustomToolParam("iso", "Iso directory"), new CustomToolParam("project", "Project"), new CustomToolParam("tolerance", "mm", "number", false, "1.0")],
        [
            new CustomToolStep("pcf_check", JsonNode.Parse("""{"path":"{{folder}}","connectionToleranceMm":"{{tolerance}}","skipInfo":true}""")!.AsObject()),
            new CustomToolStep("import_pcf", JsonNode.Parse("""{"files":["{{folder}}"],"isoDirectory":"{{iso}}","project":"{{project}}","apply":true}""")!.AsObject(), "needs approval"),
        ],
        "alice", "PC-1", new DateTime(2026, 11, 19), 1);

    [Fact]
    public void A_valid_definition_has_no_errors_and_bad_ones_say_why()
    {
        Assert.Empty(ToolRegistry.Validate(Sample(), Known));
        Assert.Contains(ToolRegistry.Validate(Sample("pcf_check"), Known), e => e.Contains("built-in"));
        Assert.Contains(ToolRegistry.Validate(Sample("Bad Name"), Known), e => e.StartsWith("Name"));
        var loop = Sample() with { Steps = [new CustomToolStep("run_custom_tool", new JsonObject())] };
        Assert.Contains(ToolRegistry.Validate(loop, Known), e => e.Contains("cannot be used"));
        var unknownTool = Sample() with { Steps = [new CustomToolStep("nope", new JsonObject())] };
        Assert.Contains(ToolRegistry.Validate(unknownTool, Known), e => e.Contains("no tool 'nope'"));
        var undeclared = Sample() with { Parameters = [new CustomToolParam("folder", "f")] };
        Assert.Contains(ToolRegistry.Validate(undeclared, Known), e => e.Contains("{{iso}}"));
        var unused = Sample() with { Parameters = Sample().Parameters.Append(new CustomToolParam("extra", "x")).ToList() };
        Assert.Contains(ToolRegistry.Validate(unused, Known), e => e.Contains("'extra'"));
    }

    [Fact]
    public void Resolve_fills_placeholders_and_keeps_types()
    {
        var steps = ToolRegistry.Resolve(Sample(), new Dictionary<string, string> { ["folder"] = "D:/pcf", ["iso"] = "D:/iso", ["project"] = "P1", ["tolerance"] = "2.5" });
        var first = steps[0].Args;
        Assert.Equal("D:/pcf", (string?)first["path"]);
        Assert.Equal(2.5, (double?)first["connectionToleranceMm"]);              // a number stays a number
        Assert.True((bool?)first["skipInfo"]);                                    // untouched values are kept
        Assert.Equal("D:/pcf", (string?)steps[1].Args["files"]![0]);
        Assert.Equal(1.0, (double?)ToolRegistry.Resolve(Sample(), new Dictionary<string, string> { ["folder"] = "a", ["iso"] = "b", ["project"] = "c" })[0].Args["connectionToleranceMm"]);   // default
        Assert.Throws<ArgumentException>(() => ToolRegistry.Resolve(Sample(), new Dictionary<string, string> { ["folder"] = "a" }));
    }

    [Fact]
    public void A_list_parameter_becomes_a_json_array()
    {
        var d = new CustomToolDef("gen_all", "Generate", [new CustomToolParam("pipes", "Pipelines", "list")],
            [new CustomToolStep("generate_drawings", JsonNode.Parse("""{"pipelines":"{{pipes}}","style":"Final-Erection"}""")!.AsObject())], "a", "m", DateTime.Now);
        var args = ToolRegistry.Resolve(d, new Dictionary<string, string> { ["pipes"] = "P-1, P-2;P-3" })[0].Args;
        Assert.Equal(["P-1", "P-2", "P-3"], args["pipelines"]!.AsArray().Select(x => (string?)x));
    }

    [Fact]
    public void A_tool_made_on_one_pc_reaches_the_shared_folder_and_another_pc()
    {
        using var t = new TempDir();
        var shared = System.IO.Path.Combine(t.Path, "Spoolgen_MCP", "tools");
        var pc1 = new ToolRegistry(shared, System.IO.Path.Combine(t.Path, "pc1"));
        var pc2 = new ToolRegistry(shared, System.IO.Path.Combine(t.Path, "pc2"));
        var (local, sharedOk) = pc1.Save(Sample());
        Assert.True(local && sharedOk);
        Assert.Equal("check_and_import", Assert.Single(pc2.List()).Name);          // visible to the other PC straight away
        Assert.Equal(1, pc2.Sync().FromShared);                                    // and cached on its disk
        Assert.True(File.Exists(System.IO.Path.Combine(t.Path, "pc2", "check_and_import.json")));
        Assert.Equal("alice", pc2.Get("CHECK_AND_IMPORT")!.Author);
    }

    [Fact]
    public void A_tool_made_while_the_shared_folder_is_unreachable_is_synced_later()
    {
        using var t = new TempDir();
        var blocker = t.File("blocker", "this is a file, so a folder cannot be created under it");
        var offline = new ToolRegistry(System.IO.Path.Combine(blocker, "tools"), System.IO.Path.Combine(t.Path, "pc1"));
        var (local, sharedOk) = offline.Save(Sample());
        Assert.True(local);
        Assert.False(sharedOk);
        Assert.False(offline.Sync().SharedReachable);
        Assert.Single(offline.List());                                             // still usable on this PC

        var shared = System.IO.Path.Combine(t.Path, "share");
        var back = new ToolRegistry(shared, System.IO.Path.Combine(t.Path, "pc1"));
        Assert.Equal(1, back.Sync().ToShared);
        Assert.True(File.Exists(System.IO.Path.Combine(shared, "check_and_import.json")));
    }
}
