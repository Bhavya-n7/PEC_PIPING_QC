namespace Pec.Qc.Core.Setup;

/// <summary>Where the assistant reads its skills, learning notes and documents from (local folders or shared drives).</summary>
public sealed record InstallFolders(string Skills, string Learning, string Documents, string? Shared = null)
{
    public static InstallFolders Defaults()
    {
        var s = new Settings();
        return new(s.EffectiveSkillsDirectory, s.EffectiveKnowledgeDirectory, s.EffectiveDocumentsDirectory);
    }
}
