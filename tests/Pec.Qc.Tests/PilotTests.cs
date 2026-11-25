using System.Text.Json;
using Pec.Qc.Core;
using Pec.Qc.Core.Findings;
using Pec.Qc.Core.Knowledge;
using Pec.Qc.Core.Pilot;
using Pec.Qc.Core.Users;
using Pec.Qc.Mcp.Tools;

namespace Pec.Qc.Tests;

[Collection("EnvVar")]
public class PilotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pecqc-pilot-" + Guid.NewGuid().ToString("N"));
    private readonly string _shared;
    public PilotTests() { _shared = Path.Combine(_root, "share"); Directory.CreateDirectory(_shared); }
    public void Dispose()
    {
        foreach (var v in new[] { "PEC_QC_DATA", UserIdentity.NameVar, UserIdentity.CodeVar, UserIdentity.CompanyVar, "PEC_QC_APPROVAL" }) Environment.SetEnvironmentVariable(v, null);
        try { Directory.Delete(_root, true); } catch { }
    }

    private static readonly UserIdentity Ann = new("Ann Engineer", "E1", "PEC");
    private static readonly UserIdentity Lead = new("Lena Lead", "L1", "PEC");

    private Settings Pc(string name, UserIdentity who, string? shared)
    {
        var data = Path.Combine(_root, "pc-" + name); Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("PEC_QC_DATA", data);
        Environment.SetEnvironmentVariable(UserIdentity.NameVar, who.Name); Environment.SetEnvironmentVariable(UserIdentity.CodeVar, who.EmployeeCode); Environment.SetEnvironmentVariable(UserIdentity.CompanyVar, who.Company);
        Environment.SetEnvironmentVariable("PEC_QC_APPROVAL", "off");
        File.WriteAllText(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(new Dictionary<string, string?> { ["UserName"] = who.Name, ["EmployeeCode"] = who.EmployeeCode, ["Company"] = who.Company, ["SharedRoot"] = shared }));
        return Settings.Load();
    }

    // ----- feedback, time, installs
    [Theory]
    [InlineData("confirmed", "confirmed")] [InlineData("Confirmed", "confirmed")] [InlineData("correct", "confirmed")] [InlineData("accepted", "confirmed")]
    [InlineData("false-alarm", "false-alarm")] [InlineData("false alarm", "false-alarm")] [InlineData("false_alarm", "false-alarm")] [InlineData("wrong", "false-alarm")] [InlineData("missed", "missed")]
    public void Verdicts_are_understood_in_the_ways_people_say_them(string said, string stored)
    {
        var s = Pc("a", Ann, _shared);
        Assert.Equal(stored, PilotStore.RecordFeedback(s, Ann, "iso", "x.pdf", "L1", "bom", said, "").Verdict);
    }

    [Fact]
    public void An_unknown_verdict_or_impossible_minutes_are_refused_and_secrets_in_a_note_are_scrubbed()
    {
        var s = Pc("a", Ann, _shared);
        Assert.Throws<ArgumentException>(() => PilotStore.RecordFeedback(s, Ann, "iso", "x.pdf", "L1", "bom", "maybe", ""));
        Assert.Throws<ArgumentException>(() => PilotStore.RecordTime(s, Ann, "iso", "x.pdf", -1, 30));
        Assert.Throws<ArgumentException>(() => PilotStore.RecordTime(s, Ann, "iso", "x.pdf", 10, 5000));
        PilotStore.RecordFeedback(s, Ann, "iso", "x.pdf", "L1", "bom", "false-alarm", "the key is sk-ant-api03-abcdefghijklmnop1234567890");
        var raw = string.Concat(Directory.GetFiles(Path.Combine(_shared, "pilot"), "*.jsonl", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("abcdefghijklmnop1234567890", raw);
    }

    [Fact]
    public void The_summary_gives_precision_recall_time_saved_installs_and_where_the_false_alarms_are()
    {
        var s = Pc("a", Ann, _shared);
        void F(string check, string v, string type = "iso") => PilotStore.RecordFeedback(s, Ann, type, "x.pdf", "L1", check, v, "");
        for (var i = 0; i < 6; i++) F("bom", "confirmed");
        F("title-block", "confirmed"); F("title-block", "confirmed");                 // 8 confirmed
        F("coordinates", "false-alarm"); F("coordinates", "false-alarm");               // 2 false alarms
        F("lengths", "missed");                                                         // 1 missed
        F("tags", "confirmed", "ga");
        PilotStore.RecordTime(s, Ann, "iso", "a.pdf", 10, 40); PilotStore.RecordTime(s, Ann, "iso", "b.pdf", 20, 60);
        PilotStore.RecordInstall(s, "0.1.0", true, "installed"); PilotStore.RecordInstall(s, "0.1.0", false, "Claude Code not found");
        var sum = PilotStore.Summarize(s, 30);
        var iso = sum.ByType.Single(t => t.QcType == "iso");
        Assert.Equal((8, 2, 1), (iso.Confirmed, iso.FalseAlarms, iso.Missed));
        Assert.Equal(0.8, iso.Precision!.Value, 3);                                    // 8 / (8 + 2)
        Assert.Equal(8.0 / 9, iso.Recall!.Value, 3);                                   // 8 / (8 + 1)
        Assert.Equal((2, 15.0, 50.0), (iso.TimeRecords, iso.AvgMinutesWithTool!.Value, iso.AvgMinutesManual!.Value));
        Assert.Equal(70.0, iso.MinutesSaved!.Value, 3); Assert.Equal(70.0, iso.PercentSaved!.Value, 3);   // (40-10 + 60-20) / 100
        Assert.Equal(("coordinates", 2), sum.FalseAlarmsByCheck[0]); Assert.Equal(("lengths", 1), sum.MissedByCheck[0]);
        Assert.Equal((2, 1), (sum.Installs, sum.InstallsOk));
        Assert.Equal("Claude Code not found", Assert.Single(sum.FailedInstalls).Summary);
        Assert.Null(sum.ByType.Single(t => t.QcType == "ga").AvgMinutesWithTool);        // no time records for ga: not a zero
        Assert.Empty(PilotStore.Summarize(s, 30).ByType.Where(t => t.QcType == "pid"));
    }

    [Fact]
    public void Records_written_while_the_share_is_away_are_sent_back_with_the_other_offline_files()
    {
        var f = Path.Combine(_root, "file"); File.WriteAllText(f, "x");
        var away = Pc("a", Ann, Path.Combine(f, "share"));                                // an unreachable share
        PilotStore.RecordFeedback(away, Ann, "iso", "x.pdf", "L1", "bom", "confirmed", "");
        PilotStore.RecordTime(away, Ann, "iso", "x.pdf", 5, 20);
        Assert.Equal(2, PilotStore.Summarize(away, 30).ByType.Sum(t => t.Confirmed + t.TimeRecords));
        var back = new Settings { SharedRoot = _shared };
        Assert.Equal(2, OfflineQueue.Sync(back).Lines);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_shared, "pilot"), "*.jsonl", SearchOption.AllDirectories).Length);
        Assert.Equal(0, OfflineQueue.Sync(back).Lines);
    }

    [Fact]
    public void Only_the_lead_or_the_admin_can_read_the_pilot_numbers_and_anyone_can_record()
    {
        UserRegistry.Touch(new Settings { SharedRoot = _shared }, Lead);                  // first: administrator
        UserRegistry.Touch(new Settings { SharedRoot = _shared }, Ann);
        Pc("a", Ann, _shared);
        Assert.Contains("\"recorded\":true", JsonSerializer.Serialize(PilotTools.Feedback("iso", "x.pdf", "L1", "confirmed")));
        Assert.Contains("\"denied\":true", JsonSerializer.Serialize(PilotTools.Metrics()));
        Pc("l", Lead, _shared);
        var m = JsonSerializer.Serialize(PilotTools.Metrics());
        Assert.DoesNotContain("\"denied\":true", m); Assert.Contains("\"confirmed\":1", m, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ModelContextProtocol.McpException>(() => PilotTools.Feedback("zz", "x.pdf", "L1", "confirmed"));
    }

    // ----- the team's hand list against a saved report
    private static Finding Fail(string check, string subject, string message, string drawing = "x", string source = "y") => new(check, FindingStatus.Fail, subject, message, drawing, source, new SourceRef("model"));
    private static Finding Review(string check, string subject, string message) => new(check, FindingStatus.Review, subject, message);

    private string SaveReport(params Finding[] f)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".xlsx");
        QcReport.WriteExcel(path, new QcReportInfo("P", QcType.Isometric, "2-CD-1.pdf", "m", "me", DateTime.Now), f);
        return path;
    }

    [Fact]
    public void A_saved_report_is_read_back_with_its_findings()
    {
        var rep = HandComparison.ReadReport(SaveReport(Fail("bom", "L1", "tee: the quantity differs", "4", "3"), Review("title-block", "L1", "OPER PRESS. blank"), new Finding("bom", FindingStatus.Ok, "L1", "ok")));
        Assert.Equal(("2-CD-1.pdf", "FAIL"), (rep.Document, rep.Verdict));
        Assert.Equal(3, rep.Findings.Count);
        var f = rep.Findings.Single(x => x.Status == FindingStatus.Fail);
        Assert.Equal(("bom", "L1", "4", "3", "model"), (f.Check, f.Subject, f.DrawingValue, f.SourceValue, f.Source!.Document));
    }

    [Fact]
    public void The_hand_list_is_read_from_csv_and_excel_by_its_headers_and_needs_a_description()
    {
        var csv = Path.Combine(_root, "h.csv");
        File.WriteAllText(csv, "Dwg;Line;Category;Remark\n2-CD-1.pdf;L1;bom;\"tee count is 4, should be 3\"\n;;;\n");
        var h = Assert.Single(HandComparison.ReadHandList(csv));
        Assert.Equal(("2-CD-1.pdf", "L1", "bom"), (h.Document, h.Subject, h.Check)); Assert.Contains("tee count is 4, should be 3", h.Description);
        var xlsx = Path.Combine(_root, "h.xlsx");
        using (var wb = new ClosedXML.Excel.XLWorkbook()) { var ws = wb.AddWorksheet("x"); ws.Cell(1, 1).Value = "Description"; ws.Cell(2, 1).Value = "the class is wrong"; wb.SaveAs(xlsx); }
        Assert.Equal("the class is wrong", Assert.Single(HandComparison.ReadHandList(xlsx)).Description);
        File.WriteAllText(csv, "Line;Check\nL1;bom\n");
        Assert.Throws<InvalidDataException>(() => HandComparison.ReadHandList(csv));
    }

    [Fact]
    public void Each_hand_finding_is_found_flagged_or_missed_and_the_unlisted_fails_are_returned_for_a_person()
    {
        var rep = HandComparison.ReadReport(SaveReport(
            Fail("bom", "2\"-CD-1", "tee: the quantity differs", "4", "3"),
            Fail("title-block", "2\"-CD-1", "PIPNG CLASS differs from the model", "F400A", "B400A"),
            Fail("lengths", "2\"-CD-1", "total pipe length differs from the model", "39'6\"", "38'6\""),
            Review("coordinates", "2\"-CD-1", "no model point within the tolerance")));
        var hand = new[]
        {
            new HandFinding("2-CD-1.pdf", "2\"-CD-1", "bom", "tee quantity wrong"),
            new HandFinding("2-CD-1.pdf", "2\"-CD-1", "", "the piping class F400A is wrong"),
            new HandFinding("2-CD-1.pdf", "2\"-CD-1", "coordinates", "start coordinate wrong"),
            new HandFinding("2-CD-1.pdf", "2\"-CD-1", "supports", "a support is missing"),
            new HandFinding("other-drawing.pdf", "X", "bom", "belongs to another drawing: not counted"),
        };
        var a = HandComparison.Compare(hand, rep);
        Assert.Equal((4, 2, 1, 1), (a.HandCount, a.Found, a.Flagged, a.Missed));
        Assert.Equal(new[] { HandOutcome.Found, HandOutcome.Found, HandOutcome.Flagged, HandOutcome.Missed }, a.Matches.Select(m => m.Outcome).ToArray());
        Assert.Equal(0.5, a.StrictRecall!.Value, 3); Assert.Equal(0.75, a.Recall!.Value, 3);
        Assert.Equal(3, a.FailTotal); Assert.Equal(2.0 / 3, a.FailPrecision!.Value, 3);
        Assert.Equal("total pipe length differs from the model", Assert.Single(a.ExtraFails).Message);
        Assert.Empty(a.ExtraReviews);
    }

    [Fact]
    public void One_finding_answers_only_one_hand_finding_and_a_clean_drawing_with_an_empty_list_has_no_rates()
    {
        var rep = HandComparison.ReadReport(SaveReport(Fail("bom", "L1", "tee: the quantity differs")));
        var two = new[] { new HandFinding("", "L1", "bom", "tee quantity"), new HandFinding("", "L1", "bom", "another tee problem") };
        var a = HandComparison.Compare(two, rep);
        Assert.Equal((1, 1), (a.Found, a.Missed));
        var clean = HandComparison.Compare([], HandComparison.ReadReport(SaveReport(new Finding("bom", FindingStatus.Ok, "L1", "ok"))));
        Assert.Null(clean.Recall); Assert.Null(clean.FailPrecision); Assert.Equal(0, clean.HandCount);
    }

    [Fact]
    public void A_hand_finding_about_another_line_or_tag_is_not_matched_by_a_finding_of_this_one()
    {
        var rep = HandComparison.ReadReport(SaveReport(Fail("bom", "2\"-CD-60A07900-B400A-N-HS-017", "tee: the quantity differs", "4", "3")));
        var other = HandComparison.Compare([new HandFinding("", "3\"-SC-60A05700-F241S-H-HS-002", "bom", "tee quantity is wrong")], rep);
        Assert.Equal(1, other.Missed);
        Assert.Single(other.ExtraFails);                                  // the assistant's FAIL is then on nobody's list
        Assert.Equal(1, HandComparison.Compare([new HandFinding("", "2\"-CD-60A07900-B400A-N-HS-017", "bom", "tee quantity is wrong")], rep).Found);
    }

    [Fact]
    public void The_compare_tool_reads_a_report_by_name_and_a_hand_list_and_refuses_what_it_cannot_find()
    {
        Pc("a", Ann, _shared);
        var s = Settings.Load();
        var info = new QcReportInfo("P", QcType.Isometric, "2-CD-1.pdf", "m", "me", DateTime.Now);
        var (xl, _) = QcReport.Write(s, info, [Fail("bom", "L1", "tee: the quantity differs", "4", "3")]);
        var hand = Path.Combine(_root, "h.csv"); File.WriteAllText(hand, "Drawing,Subject,Check,Description\n2-CD-1.pdf,L1,bom,tee quantity is wrong\n");
        var byPath = JsonSerializer.Serialize(PilotTools.Compare(xl, hand));
        Assert.Contains("\"found\":1", byPath); Assert.Contains("\"strictRecall\":\"100%\"", byPath);
        Assert.Contains("\"found\":1", JsonSerializer.Serialize(PilotTools.Compare("2-CD-1", hand, "P")));       // by the drawing's name, newest report
        Assert.Throws<ModelContextProtocol.McpException>(() => PilotTools.Compare("nothing-like-this", hand, "P"));
        Assert.Throws<ModelContextProtocol.McpException>(() => PilotTools.Compare(xl, Path.Combine(_root, "none.csv")));
    }
}
