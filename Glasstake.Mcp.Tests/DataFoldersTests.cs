using System.IO;
using Glasstake.Core;
using Xunit;

namespace Glasstake.Mcp.Tests;

/// <summary>
/// The move from the old AgentReel data folders to the Glasstake ones.
/// </summary>
public sealed class DataFoldersTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "Glasstake.Mcp.Tests", "DataFolders-" + Guid.NewGuid().ToString("N")[..8]);

    public DataFoldersTests() => Directory.CreateDirectory(_parent);

    public void Dispose() => Directory.Delete(_parent, true);

    [Fact]
    public void An_old_AgentReel_folder_is_moved_to_Glasstake_with_its_files()
    {
        var old = Path.Combine(_parent, "AgentReel", "Recordings", "take");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "recording.json"), "{}");

        var folder = DataFolders.Resolve(_parent);

        Assert.Equal(Path.Combine(_parent, "Glasstake"), folder);
        Assert.True(File.Exists(Path.Combine(folder, "Recordings", "take", "recording.json")));
        Assert.False(Directory.Exists(Path.Combine(_parent, "AgentReel")));
    }

    [Fact]
    public void An_existing_Glasstake_folder_wins_and_the_old_one_stays_untouched()
    {
        Directory.CreateDirectory(Path.Combine(_parent, "Glasstake"));
        Directory.CreateDirectory(Path.Combine(_parent, "AgentReel"));

        Assert.Equal(Path.Combine(_parent, "Glasstake"), DataFolders.Resolve(_parent));
        Assert.True(Directory.Exists(Path.Combine(_parent, "AgentReel")));
    }

    [Fact]
    public void An_old_folder_in_use_is_kept_and_used()
    {
        var old = Path.Combine(_parent, "AgentReel");
        Directory.CreateDirectory(old);

        using (File.Open(Path.Combine(old, "settings.json"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(old, DataFolders.Resolve(_parent));

        Assert.False(Directory.Exists(Path.Combine(_parent, "Glasstake")));
    }

    [Fact]
    public void An_old_AGENTREEL_variable_still_counts_when_the_new_one_is_unset()
    {
        var name = "GLASSTAKE_TEST_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var oldName = "AGENTREEL_" + name["GLASSTAKE_".Length..];

        try
        {
            Environment.SetEnvironmentVariable(oldName, "old");
            Assert.Equal("old", DataFolders.Variable(name));

            Environment.SetEnvironmentVariable(name, "new");
            Assert.Equal("new", DataFolders.Variable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable(oldName, null);
        }
    }
}
