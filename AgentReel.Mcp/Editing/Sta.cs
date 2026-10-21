namespace AgentReel.Mcp.Editing;

/// <summary>
/// WPF visuals need an STA thread, the MCP tools run on the thread pool.
/// </summary>
internal static class Sta
{
    public static T Run<T>(Func<T> work, string failure)
    {
        T result = default!;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return error == null ? result : throw new InvalidOperationException(failure + ": " + error.Message, error);
    }
}
