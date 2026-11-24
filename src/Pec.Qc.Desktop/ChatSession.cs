using System.Windows;
using System.Windows.Controls;
using Pec.Qc.Core.Claude;

namespace Pec.Qc.Desktop;

/// <summary>
/// One open chat: what is saved (<see cref="Data"/>) and what only lives while the app runs (its message panel, whether Claude is working on it, the stop button,
/// attached files, the half-typed message). Several chats can be busy at the same time; each has its own Claude process.
/// </summary>
internal sealed class ChatSession
{
    public ChatSession(StoredChat? data = null) => Data = data ?? new StoredChat();

    public StoredChat Data { get; }

    /// <summary>The conversation as it is shown. Built once; switching chats only swaps which panel is displayed.</summary>
    public StackPanel Panel { get; } = new() { Margin = new Thickness(36, 20, 36, 12), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };

    /// <summary>True once the first message is on screen (until then the welcome page with the quick tasks is shown).</summary>
    public bool Started { get; set; }
    public bool Busy { get; set; }
    /// <summary>An answer arrived while the user was looking at another chat.</summary>
    public bool Unread { get; set; }
    public CancellationTokenSource? Cts { get; set; }
    public FrameworkElement? Thinking { get; set; }
    public Expander? Step { get; set; }
    public TextBox? StepBox { get; set; }
    public string StepInput { get; set; } = "";
    public List<Attachment> Files { get; } = [];
    public string Batch { get; set; } = ChatAttachments.Batch();
    public string Draft { get; set; } = "";
    /// <summary>Counters of the turn in progress: they decide whether the assistant reflects afterwards.</summary>
    public int TurnTools { get; set; }
    public int TurnErrors { get; set; }
    public bool TurnBuilt { get; set; }
    public bool TurnCorrected { get; set; }
    public bool TurnLearned { get; set; }
    public bool TurnSavedTool { get; set; }
    public bool Reflecting { get; set; }
    public string LastTool { get; set; } = "";
}
