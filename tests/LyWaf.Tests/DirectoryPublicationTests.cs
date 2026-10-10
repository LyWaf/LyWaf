using LyWaf.Services.Files;
using Xunit;

namespace LyWaf.Tests;

public sealed class DirectoryPublicationTests
{
    [Fact]
    public void DifferentDestinationsKeepIndependentBackups()
    {
        var root = Path.Combine(Path.GetTempPath(), "publication-test-" + Guid.NewGuid());
        var cs = Path.Combine(root, "cs");
        var cs1 = Path.Combine(root, "cs1");
        try
        {
            DirectoryPublication.Publish(root, cs, Stage(root, "cs-first"));
            DirectoryPublication.Publish(root, cs1, Stage(root, "cs1-first"));
            Assert.Empty(Directory.GetDirectories(Path.Combine(root, ".cs1_version")));

            DirectoryPublication.Publish(root, cs, Stage(root, "cs-second"));
            DirectoryPublication.Publish(root, cs1, Stage(root, "cs1-second"));
            DirectoryPublication.Publish(root, cs, Stage(root, "cs-third"));

            Assert.Equal("cs-third", File.ReadAllText(Path.Combine(cs, "index.html")));
            Assert.Equal("cs1-second", File.ReadAllText(Path.Combine(cs1, "index.html")));
            var csBackup = Assert.Single(Directory.GetDirectories(Path.Combine(root, ".cs_version")));
            var cs1Backup = Assert.Single(Directory.GetDirectories(Path.Combine(root, ".cs1_version")));
            Assert.Equal("cs-second", File.ReadAllText(Path.Combine(csBackup, "index.html")));
            Assert.Equal("cs1-first", File.ReadAllText(Path.Combine(cs1Backup, "index.html")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PublishesDirectFilesAndKeepsOnlyPreviousBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), "publication-test-" + Guid.NewGuid());
        var destination = Path.Combine(root, "cs");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "old.txt"), "legacy");
        try
        {
            DirectoryPublication.Publish(root, destination, Stage(root, "one"));
            Assert.Equal("one", File.ReadAllText(Path.Combine(destination, "index.html")));
            Assert.False(File.Exists(Path.Combine(destination, "old.txt")));
            Assert.False(File.Exists(Path.Combine(destination, ".lywaf-current")));
            var backup = Assert.Single(Directory.GetDirectories(Path.Combine(root, ".cs_version")));
            Assert.Equal("legacy", File.ReadAllText(Path.Combine(backup, "old.txt")));
            DirectoryPublication.Publish(root, destination, Stage(root, "two"));
            Assert.Equal("two", File.ReadAllText(Path.Combine(destination, "index.html")));
            backup = Assert.Single(Directory.GetDirectories(Path.Combine(root, ".cs_version")));
            Assert.Equal("one", File.ReadAllText(Path.Combine(backup, "index.html")));
            using var lease = DirectoryPublication.Acquire(destination + Path.DirectorySeparatorChar);
            Assert.Equal(destination, lease.Path);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(".")]
    [InlineData("../outside")]
    [InlineData(".cs_version")]
    public void RefusesUnsafeTargets(string target)
    {
        var root = Path.Combine(Path.GetTempPath(), "publication-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try { Assert.Throws<InvalidDataException>(() => DirectoryPublication.Publish(root, Path.Combine(root, target), Stage(root, "new"))); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MissingStagingDoesNotRemoveCurrentDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "publication-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var destination = Stage(root, "old");
        try
        {
            Assert.Throws<DirectoryNotFoundException>(() => DirectoryPublication.Publish(root, destination, Path.Combine(root, "missing")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(destination, "index.html")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Stage(string root, string value)
    {
        var path = Path.Combine(root, "staging-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "index.html"), value);
        return path;
    }
}
