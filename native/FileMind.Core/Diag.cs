namespace FileMind.Core;

/// <summary>A quiet error log (logs/app.log) for failures the app recovers from but someone may need to see later.</summary>
public static class Diag
{
    public static string? LogFile { get; set; }

    public static void Log(string where, Exception e)
    {
        try
        {
            if (LogFile is null) return;
            File.AppendAllText(LogFile, $"{DateTime.Now:u} {where}: {e.GetType().Name}: {e.Message}{Environment.NewLine}");
        }
        catch (Exception) { }
    }
}
