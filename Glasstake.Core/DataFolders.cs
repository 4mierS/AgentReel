namespace Glasstake.Core;

/// <summary>
/// Glasstake's data folders: <c>%LOCALAPPDATA%\Glasstake</c> (recordings, shot lists) and <c>%APPDATA%\Glasstake</c>
/// (settings). Before the rename they were called AgentReel; the first use moves an old folder to the new name. If it
/// cannot be moved (another program still has a file open), the old folder is used as it is.
/// </summary>
public static class DataFolders
{
    public const string Name = "Glasstake";
    private const string OldName = "AgentReel";

    private static readonly Lazy<string> LocalFolder = new(() => Resolve(Environment.SpecialFolder.LocalApplicationData));
    private static readonly Lazy<string> RoamingFolder = new(() => Resolve(Environment.SpecialFolder.ApplicationData));

    /// <summary>
    /// <c>%LOCALAPPDATA%\Glasstake</c>.
    /// </summary>
    public static string Local => LocalFolder.Value;

    /// <summary>
    /// <c>%APPDATA%\Glasstake</c>.
    /// </summary>
    public static string Roaming => RoamingFolder.Value;

    /// <summary>
    /// The value of a <c>GLASSTAKE_</c> environment variable, else of its old <c>AGENTREEL_</c> name.
    /// </summary>
    public static string? Variable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);

        return string.IsNullOrWhiteSpace(value) && name.StartsWith("GLASSTAKE_", StringComparison.Ordinal)
            ? Environment.GetEnvironmentVariable("AGENTREEL_" + name["GLASSTAKE_".Length..])
            : value;
    }

    internal static string Resolve(Environment.SpecialFolder folder) => Resolve(Environment.GetFolderPath(folder));

    internal static string Resolve(string parent)
    {
        var current = Path.Combine(parent, Name);
        var old = Path.Combine(parent, OldName);

        if (Directory.Exists(current) || !Directory.Exists(old))
            return current;

        try
        {
            Directory.Move(old, current);
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return old;
        }
    }
}
