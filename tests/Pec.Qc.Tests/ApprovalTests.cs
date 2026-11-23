using Pec.Qc.Core.Approvals;

namespace Pec.Qc.Tests;

public class ApprovalGateTests
{
    private static ApprovalGate Gate(TempDir t, ApprovalMode mode) => new(System.IO.Path.Combine(t.Path, "approvals"), () => mode);

    [Theory]
    [InlineData(null, ApprovalMode.Off)] [InlineData("", ApprovalMode.Off)] [InlineData("off", ApprovalMode.Off)]
    [InlineData("on", ApprovalMode.Task)] [InlineData("task", ApprovalMode.Task)] [InlineData("strict", ApprovalMode.Strict)]
    public void The_mode_comes_from_the_variable_and_an_absent_one_means_the_other_client_asks_itself(string? v, ApprovalMode expected) =>
        Assert.Equal(expected, ApprovalGate.ParseMode(v));

    [Fact] public void Off_never_asks()
    {
        using var t = new TempDir(); var g = Gate(t, ApprovalMode.Off);
        Assert.Equal(ApprovalState.Allowed, g.Check("model_build_spec", "a", "s", "d"));
        Assert.Empty(g.Pending());
    }

    [Fact] public void An_answered_request_is_removed_so_the_app_does_not_ask_the_same_question_again()
    {
        // the bug: after the user approved, the .pending file stayed and the app's timer asked again and again
        using var t = new TempDir(); var g = Gate(t, ApprovalMode.Strict);
        Assert.Equal(ApprovalState.Pending, g.Check("model_build_spec", "a", "Build", "d"));
        var req = Assert.Single(g.Pending());
        g.Approve(req.Id);
        Assert.Empty(g.Pending());                                                      // nothing left to show again
        Assert.Equal(ApprovalState.Allowed, g.Check("model_build_spec", "a", "Build", "d"));
        Assert.Equal(ApprovalState.Pending, g.Check("model_build_spec", "a", "Build", "d"));   // strict: one approval, one run
    }

    [Fact] public void In_task_mode_one_approval_covers_the_rest_of_the_task()
    {
        using var t = new TempDir(); var g = Gate(t, ApprovalMode.Task);
        Assert.Equal(ApprovalState.Pending, g.Check("model_build_spec", "build A", "Build A", "d"));
        g.Approve(g.Pending().Single().Id, openTaskWindow: true);
        Assert.Equal(ApprovalState.Allowed, g.Check("model_build_spec", "build A", "Build A", "d"));
        // different arguments and a different tool: no new question (this is what made it ask dozens of times)
        Assert.Equal(ApprovalState.Allowed, g.Check("model_build_spec", "build B with other names", "Build B", "d"));
        Assert.Equal(ApprovalState.Allowed, g.Check("convert_build", "x", "Convert", "d"));
        Assert.Empty(g.Pending());
    }

    [Fact] public void Destructive_actions_always_ask_and_are_not_covered_by_the_task()
    {
        using var t = new TempDir(); var g = Gate(t, ApprovalMode.Task);
        g.Check("model_build_spec", "a", "Build", "d");
        g.Approve(g.Pending().Single().Id, openTaskWindow: true);
        Assert.Equal(ApprovalState.Pending, g.Check("model_delete_list", "names=T-1", "Delete T-1", "d", destructive: true));
        var del = Assert.Single(g.Pending()); Assert.True(del.Destructive);
        g.Approve(del.Id, openTaskWindow: true);                                         // approving a delete does NOT open or extend a window
        Assert.Equal(ApprovalState.Allowed, g.Check("model_delete_list", "names=T-1", "Delete T-1", "d", destructive: true));
        Assert.Equal(ApprovalState.Pending, g.Check("model_delete_list", "names=T-1", "Delete T-1", "d", destructive: true));
        Assert.Equal(ApprovalState.Pending, g.Check("model_delete_list", "names=T-2", "Delete T-2", "d", destructive: true));
    }

    [Fact] public void An_approved_delete_alone_does_not_open_the_task_window()
    {
        using var t = new TempDir(); var g = Gate(t, ApprovalMode.Task);
        g.Check("model_delete_list", "names=T-1", "Delete", "d", destructive: true);
        g.Approve(g.Pending().Single().Id, openTaskWindow: true);
        Assert.False(g.TaskWindowOpen);
        Assert.Equal(ApprovalState.Pending, g.Check("model_build_spec", "a", "Build", "d"));
    }

    [Fact] public void The_task_window_closes_when_idle_when_denied_and_when_a_new_task_starts()
    {
        using var t = new TempDir(); var g = Gate(t, ApprovalMode.Task);
        void Open() { g.Check("model_build_spec", "a" + Guid.NewGuid(), "Build", "d"); g.Approve(g.Pending().Last().Id, true); }
        Open(); Assert.True(g.TaskWindowOpen);
        File.SetLastWriteTimeUtc(System.IO.Path.Combine(g.Directory_, "task.grant"), DateTime.UtcNow - ApprovalGate.TaskIdle - TimeSpan.FromMinutes(1));
        Assert.False(g.TaskWindowOpen);                                                  // idle too long
        Assert.Equal(ApprovalState.Pending, g.Check("model_build_spec", "next", "Build", "d"));

        Open(); g.EndTask(); Assert.False(g.TaskWindowOpen);                             // a new chat
        Open(); g.Deny(g.Pending().Count > 0 ? g.Pending()[0].Id : "none"); Assert.False(g.TaskWindowOpen);   // a refusal ends it too
    }

    [Fact] public void The_task_window_slides_while_claude_keeps_working()
    {
        using var t = new TempDir(); var g = Gate(t, ApprovalMode.Task);
        g.Check("model_build_spec", "a", "Build", "d"); g.Approve(g.Pending().Single().Id, true);
        var grant = System.IO.Path.Combine(g.Directory_, "task.grant");
        File.SetLastWriteTimeUtc(grant, DateTime.UtcNow - ApprovalGate.TaskIdle + TimeSpan.FromMinutes(1));   // nearly expired
        Assert.Equal(ApprovalState.Allowed, g.Check("model_build_spec", "b", "Build", "d"));                  // a use renews it
        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(grant) < TimeSpan.FromMinutes(1));
    }

    [Fact] public void Strict_still_asks_for_every_change_even_after_an_approval_with_the_window_flag()
    {
        using var t = new TempDir(); var g = Gate(t, ApprovalMode.Strict);
        g.Check("model_build_spec", "a", "Build", "d"); g.Approve(g.Pending().Single().Id, openTaskWindow: true);
        Assert.Equal(ApprovalState.Allowed, g.Check("model_build_spec", "a", "Build", "d"));
        Assert.Equal(ApprovalState.Pending, g.Check("model_build_spec", "b", "Build", "d"));
    }

    [Fact] public void An_old_approval_expires()
    {
        using var t = new TempDir(); var g = Gate(t, ApprovalMode.Strict);
        g.Check("model_build_spec", "a", "Build", "d"); var id = g.Pending().Single().Id; g.Approve(id);
        File.SetLastWriteTimeUtc(System.IO.Path.Combine(g.Directory_, id + ".approved"), DateTime.UtcNow - ApprovalGate.Lifetime - TimeSpan.FromMinutes(1));
        Assert.Equal(ApprovalState.Pending, g.Check("model_build_spec", "a", "Build", "d"));
    }
}
