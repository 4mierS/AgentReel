using System.IO;
using System.Runtime.CompilerServices;
using AgentReel.Core;

namespace AgentReel.Mcp.Tests;

/// <summary>
/// Every test of this assembly records into its own temporary folder, never into the user's recordings.
/// </summary>
internal static class TestFolders
{
    public static readonly string Recordings = Path.Combine(Path.GetTempPath(), "AgentReel.Tests", "Recordings-" + Guid.NewGuid().ToString("N")[..8]);

    [ModuleInitializer]
    internal static void Initialize() => Environment.SetEnvironmentVariable(RecordingStore.FolderVariable, Recordings);
}
