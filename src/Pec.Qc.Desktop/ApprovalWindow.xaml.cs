using System.Windows;
using Pec.Qc.Core.Approvals;

namespace Pec.Qc.Desktop;

public partial class ApprovalWindow : Window
{
    public ApprovalWindow(ApprovalRequest request, bool coversTask = false)
    {
        InitializeComponent();
        HintText.Text = coversTask
            ? "Approve once: Claude then finishes the rest of this task without asking again. Deleting still asks each time."
            : request.Destructive ? "This kind of action always asks, each time." : "An approval is for this one action and expires after 10 minutes.";
        SummaryText.Text = request.Summary;
        DetailText.Text = request.Detail;
    }

    private void Approve_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Deny_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
