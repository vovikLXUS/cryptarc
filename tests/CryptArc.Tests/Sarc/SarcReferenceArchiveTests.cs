using System.Text.Json;
using CryptArc.Core.Hashing;
using CryptArc.Core.Sarc;

namespace CryptArc.Tests.Sarc;

/// <summary>
/// The instructor's acceptance set: every <c>ref_*</c> must restore exactly as its
/// manifest says, every <c>bad_*</c> must fail with the code in <c>expected_failures.json</c>.
/// This is the oracle for SARC conformance; a failure here means the reader is wrong,
/// never the files.
/// </summary>
/// <remarks>
/// The set is not in git. It is read from <c>SARC_REFERENCE_DIR</c> if that is set,
/// otherwise from <c>team/reference</c> at the repository root, where the issued team
/// package goes (the folder is ignored). Without it these tests are reported as skipped.
/// </remarks>
public class SarcReferenceArchiveTests
{
    /// <summary>The password of the whole set, as the <c>ref_*.json</c> manifests state it.</summary>
    private const string ReferencePassword = "algo2026";

    /// <summary>Where the reference set is looked for.</summary>
    internal static readonly string ReferenceDirectory = FindReferenceDirectory();

    /// <summary>Whether the set is there to be tested.</summary>
    internal static bool IsAvailable => File.Exists(Path.Combine(ReferenceDirectory, "expected_failures.json"));

    public static TheoryData<string> Sets
    {
        get
        {
            var sets = new TheoryData<string>();
            foreach (string manifest in Directory.GetFiles(ReferenceDirectory, "ref_*.json").Order(StringComparer.Ordinal))
                sets.Add(Path.GetFileNameWithoutExtension(manifest));
            return sets;
        }
    }

    public static TheoryData<string, string> Failures
    {
        get
        {
            var failures = new TheoryData<string, string>();
            using JsonDocument expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(ReferenceDirectory, "expected_failures.json")));
            foreach (JsonProperty entry in expected.RootElement.EnumerateObject())
                failures.Add(entry.Name, entry.Value.GetProperty("очікуваний_код").GetString()!);
            return failures;
        }
    }

    [ReferenceSetFact]
    public void TheWholeSetIsPresent()
    {
        Assert.Equal(6, Directory.GetFiles(ReferenceDirectory, "ref_*.json").Length);
        Assert.Equal(6, Directory.GetFiles(ReferenceDirectory, "bad_*.arc").Length);

        using JsonDocument expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(ReferenceDirectory, "expected_failures.json")));
        Assert.Equal(6, expected.RootElement.EnumerateObject().Count());
    }

    [ReferenceSetTheory]
    [MemberData(nameof(Sets))]
    public void EveryReferenceArchiveRestoresAsItsManifestSays(string set)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(ReferenceDirectory, set + ".json")));
        JsonElement root = manifest.RootElement;

        string[] volumes = [.. Directory.GetFiles(ReferenceDirectory, set + ".arc"), .. Directory.GetFiles(ReferenceDirectory, set + ".part*.arc")];
        Assert.Equal(root.GetProperty("томів").GetInt32(), volumes.Length);

        // Last volume first: the reader must order volumes by volume_index, not by argument order.
        Array.Sort(volumes, StringComparer.Ordinal);
        Array.Reverse(volumes);
        IReadOnlyList<SarcFile> files = SarcArchiveReader.ReadFiles(volumes, root.GetProperty("пароль").GetString()!);

        JsonElement contents = root.GetProperty("вміст");
        Assert.Equal(
            contents.EnumerateObject().Select(f => f.Name).Order(StringComparer.Ordinal),
            files.Select(f => f.Name).Order(StringComparer.Ordinal));

        foreach (SarcFile file in files)
        {
            JsonElement expected = contents.GetProperty(file.Name);
            Assert.Equal(expected.GetProperty("розмір").GetInt64(), file.Data.Length);
            Assert.Equal(expected.GetProperty("crc32").GetString()!.ToUpperInvariant(), Crc32.Compute(file.Data).ToString("X8"));
        }
    }

    [ReferenceSetTheory]
    [MemberData(nameof(Failures))]
    public void EveryDamagedReferenceArchiveFailsWithItsExpectedCode(string file, string code)
    {
        SarcException rejection = Assert.Throws<SarcException>(
            () => SarcArchiveReader.ReadFiles([Path.Combine(ReferenceDirectory, file)], ReferencePassword));

        Assert.Equal(code, rejection.SpecName);
    }

    /// <summary>
    /// <c>SARC_REFERENCE_DIR</c>, or <c>team/reference</c> under the repository root, found
    /// by walking up from the test binaries to the folder that holds <c>CryptArc.slnx</c>.
    /// </summary>
    private static string FindReferenceDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("SARC_REFERENCE_DIR");
        if (!string.IsNullOrEmpty(configured))
            return configured;

        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CryptArc.slnx")))
                return Path.Combine(directory.FullName, "team", "reference");
        }

        throw new DirectoryNotFoundException("The repository root (CryptArc.slnx) was not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>A fact that is skipped, not failed, when the reference set is absent.</summary>
public sealed class ReferenceSetFactAttribute : FactAttribute
{
    /// <summary>Sets <see cref="FactAttribute.Skip"/> when there is nothing to test.</summary>
    public ReferenceSetFactAttribute()
    {
        if (!SarcReferenceArchiveTests.IsAvailable)
            Skip = ReferenceSetTheoryAttribute.Missing;
    }
}

/// <summary>A theory that is skipped, not failed, when the reference set is absent.</summary>
public sealed class ReferenceSetTheoryAttribute : TheoryAttribute
{
    internal static string Missing =>
        $"No reference set in {SarcReferenceArchiveTests.ReferenceDirectory}: copy reference/ from the issued " +
        "Project 2 package to team/reference, or set SARC_REFERENCE_DIR.";

    /// <summary>Sets <see cref="FactAttribute.Skip"/> when there is nothing to test.</summary>
    public ReferenceSetTheoryAttribute()
    {
        if (!SarcReferenceArchiveTests.IsAvailable)
            Skip = Missing;
    }
}
