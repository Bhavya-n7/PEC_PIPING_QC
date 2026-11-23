namespace Pec.Qc.Core.Setup;

/// <summary>
/// The skills / learning / documents folders the installer proposes for a given shared office folder. With a shared folder they live inside it, so every PC
/// reads and writes the same lessons and skills; without one they are local. The Setup window recomputes them when the shared folder changes
/// (unless the user edited a box), so a typed or browsed shared folder is never silently overridden by a local default.
/// </summary>
public static class FolderDefaults
{
    public static InstallFolders For(string? shared)
    {
        var s = new Settings { SharedRoot = string.IsNullOrWhiteSpace(shared) ? null : shared.Trim() };
        return new(s.EffectiveSkillsDirectory, s.EffectiveKnowledgeDirectory, s.EffectiveDocumentsDirectory, s.SharedRoot);
    }

    /// <summary>A box follows the shared folder only while it still shows the previous default (the user has not edited it).</summary>
    public static string Follow(string current, string previousDefault, string newDefault) =>
        current.Equals(previousDefault, StringComparison.OrdinalIgnoreCase) ? newDefault : current;
}
