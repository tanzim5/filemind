namespace FileMind.Core;

/// <summary>Where the native app keeps things. Everything lives under one folder, so uninstalling is deleting it.</summary>
public sealed class AppPaths
{
    public string Root { get; }
    public string Data => Path.Combine(Root, "data");
    public string Models => Path.Combine(Root, "models");
    public string Runtime => Path.Combine(Root, "runtime");
    public string Logs => Path.Combine(Root, "logs");
    public string SettingsFile => Path.Combine(Root, "settings.json");

    public AppPaths(string? root = null)
    {
        Root = root ?? Environment.GetEnvironmentVariable("FILEMIND_NATIVE_HOME")
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "File Mind");
        Directory.CreateDirectory(Root);
    }
}
