using System.Text.Json;
using Pec.Qc.Core.Setup;

namespace Pec.Qc.Tests;

/// <summary>The update folder is on a share many people can write to: an update is only offered when its release file is signed with the company key and the files match it.</summary>
public class UpdateTrustTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pecqc-trust-" + Guid.NewGuid().ToString("N"));
    private static readonly (string Priv, string Pub) Key = UpdateTrust.NewKeyPair();
    private static readonly (string Priv, string Pub) OtherKey = UpdateTrust.NewKeyPair();

    public UpdateTrustTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Release(string version = "9.0.0", string? signWith = null, bool tamperAfterSigning = false, bool writeSig = true, string? setupHash = "abc", string? payloadHash = "def")
    {
        var folder = Path.Combine(_dir, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var json = JsonSerializer.Serialize(new { Version = version, Notes = "n", Setup = "PecQcSetup.exe", Released = "2026-01-01T00:00:00", SetupSha256 = setupHash, PayloadSha256 = payloadHash });
        var bytes = UpdateTrust.Utf8(json);
        if (writeSig && signWith is not null) File.WriteAllText(Path.Combine(folder, UpdateTrust.SignatureFile), UpdateTrust.Sign(bytes, signWith));
        if (tamperAfterSigning) json = json.Replace(version, "9.9.9");
        File.WriteAllText(Path.Combine(folder, "latest.json"), json);
        return folder;
    }

    [Fact]
    public void A_signature_verifies_only_for_the_same_bytes_and_the_same_key()
    {
        var data = UpdateTrust.Utf8("hello");
        var sig = UpdateTrust.Sign(data, Key.Priv);
        Assert.True(UpdateTrust.Verify(data, sig, Key.Pub));
        Assert.False(UpdateTrust.Verify(UpdateTrust.Utf8("hellp"), sig, Key.Pub));
        Assert.False(UpdateTrust.Verify(data, sig, OtherKey.Pub));
        Assert.False(UpdateTrust.Verify(data, "not base64!!", Key.Pub));
        Assert.False(UpdateTrust.Verify(data, sig, "<not a key/>"));
        Assert.False(UpdateTrust.Verify(data, "", Key.Pub));
    }

    [Fact]
    public async Task A_release_signed_with_the_company_key_is_offered_and_marked_authenticated()
    {
        var r = await Updates.CheckAsync(Release(signWith: Key.Priv), "1.0.0", default, Key.Pub);
        Assert.True(r.Available); Assert.True(r.Authenticated); Assert.True(r.KeyConfigured);
        Assert.Equal("9.0.0", r.Latest!.Version);
    }

    [Fact]
    public async Task An_unsigned_release_a_wrongly_signed_one_and_one_changed_after_signing_are_all_ignored_however_new_they_look()
    {
        foreach (var folder in new[] { Release(signWith: null), Release(signWith: OtherKey.Priv), Release(signWith: Key.Priv, tamperAfterSigning: true) })
        {
            var r = await Updates.CheckAsync(folder, "1.0.0", default, Key.Pub);
            Assert.False(r.Available);
            Assert.Null(r.SetupLocation);
            Assert.NotNull(r.Error);
            Assert.True(r.KeyConfigured);
        }
    }

    [Fact]
    public async Task Without_a_company_key_the_old_behaviour_remains_and_the_result_says_it_is_not_authenticated()
    {
        var r = await Updates.CheckAsync(Release(signWith: null), "1.0.0");
        Assert.True(r.Available); Assert.False(r.Authenticated); Assert.False(r.KeyConfigured);
    }

    [Fact]
    public void The_copied_files_are_checked_against_the_release()
    {
        var setup = Path.Combine(_dir, "PecQcSetup.exe"); var payload = Path.Combine(_dir, "payload.zip");
        File.WriteAllText(setup, "setup bytes"); File.WriteAllText(payload, "payload bytes");
        var good = new ReleaseInfo("9.0.0", "", "PecQcSetup.exe", null, UpdateTrust.Sha256Hex(setup), UpdateTrust.Sha256Hex(payload));
        Assert.Null(UpdateTrust.CheckFiles(good, setup, payload, keyConfigured: true));
        // a swapped payload, a swapped Setup, and a release that states no hash while a key is configured are all refused
        File.WriteAllText(payload, "payload bytes, replaced");
        Assert.Contains("payload.zip does not match", UpdateTrust.CheckFiles(good, setup, payload, true));
        File.WriteAllText(payload, "payload bytes"); File.WriteAllText(setup, "evil");
        Assert.Contains("PecQcSetup.exe does not match", UpdateTrust.CheckFiles(good, setup, payload, true));
        File.WriteAllText(setup, "setup bytes");
        var noHash = new ReleaseInfo("9.0.0", "", "PecQcSetup.exe", null);
        Assert.Contains("does not state the SHA-256", UpdateTrust.CheckFiles(noHash, setup, payload, keyConfigured: true));
        Assert.Null(UpdateTrust.CheckFiles(noHash, setup, payload, keyConfigured: false));            // no key: nothing to enforce
        Assert.Null(UpdateTrust.CheckFiles(good, setup, payload, keyConfigured: false));              // matching hashes pass either way
    }

    [Fact]
    public void A_hash_that_is_stated_is_checked_even_without_a_company_key()
    {
        var setup = Path.Combine(_dir, "s.exe"); var payload = Path.Combine(_dir, "p.zip");
        File.WriteAllText(setup, "a"); File.WriteAllText(payload, "b");
        var wrong = new ReleaseInfo("9.0.0", "", "s.exe", null, "00", UpdateTrust.Sha256Hex(payload));
        Assert.Contains("does not match", UpdateTrust.CheckFiles(wrong, setup, payload, keyConfigured: false));
    }

    [Fact]
    public void The_company_key_always_comes_from_the_shipped_defaults_so_a_settings_file_cannot_switch_the_check_off()
    {
        var s = new Pec.Qc.Core.Settings { UpdatePublicKey = "<tampered/>" };
        new Pec.Qc.Core.OfficeDefaults(null, null, Key.Pub).ApplyTo(s);
        Assert.Equal(Key.Pub, s.UpdatePublicKey);
        var empty = new Pec.Qc.Core.Settings { UpdatePublicKey = Key.Pub };
        Pec.Qc.Core.OfficeDefaults.None.ApplyTo(empty);
        Assert.Equal(Key.Pub, empty.UpdatePublicKey);        // defaults without a key leave an existing one alone
    }
}

/// <summary>Runs only with PEC_QC_RELEASE_TEST naming a folder published by publish-release.ps1 with a signing key, and PEC_QC_RELEASE_PUBKEY naming the file with the matching public key (made by new-release-key.ps1): the PowerShell signer and the C# verifier must agree.</summary>
public sealed class RealReleaseFactAttribute : FactAttribute
{
    public RealReleaseFactAttribute()
    {
        var d = Environment.GetEnvironmentVariable("PEC_QC_RELEASE_TEST"); var k = Environment.GetEnvironmentVariable("PEC_QC_RELEASE_PUBKEY");
        if (string.IsNullOrWhiteSpace(d) || !Directory.Exists(d) || string.IsNullOrWhiteSpace(k) || !File.Exists(k)) Skip = "set PEC_QC_RELEASE_TEST and PEC_QC_RELEASE_PUBKEY";
    }
}

public class RealReleaseTests
{
    [RealReleaseFact]
    public async Task A_release_signed_by_the_powershell_tooling_is_accepted_and_its_files_match_and_a_copy_altered_afterwards_is_not()
    {
        var folder = Environment.GetEnvironmentVariable("PEC_QC_RELEASE_TEST")!;
        var pub = File.ReadAllText(Environment.GetEnvironmentVariable("PEC_QC_RELEASE_PUBKEY")!).Trim();
        var r = await Updates.CheckAsync(folder, "0.0.1", default, pub);
        Assert.True(r.Available, r.Error); Assert.True(r.Authenticated);
        Assert.Null(UpdateTrust.CheckFiles(r.Latest!, Path.Combine(folder, "PecQcSetup.exe"), Path.Combine(folder, "payload.zip"), true));
        // the same folder with another key, and with the release file changed, are refused
        Assert.False((await Updates.CheckAsync(folder, "0.0.1", default, UpdateTrust.NewKeyPair().PublicXml)).Available);
        var copy = Path.Combine(Path.GetTempPath(), "pecqc-rel-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(copy);
        try
        {
            File.Copy(Path.Combine(folder, "latest.json.sig"), Path.Combine(copy, "latest.json.sig"));
            File.WriteAllText(Path.Combine(copy, "latest.json"), File.ReadAllText(Path.Combine(folder, "latest.json")).Replace("0.1.0", "9.9.9"));
            Assert.False((await Updates.CheckAsync(copy, "0.0.1", default, pub)).Available);
        }
        finally { Directory.Delete(copy, true); }
    }
}
