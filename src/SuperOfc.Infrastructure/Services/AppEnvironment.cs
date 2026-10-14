using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SuperOfc.Application.Abstractions;

namespace SuperOfc.Infrastructure.Services;

/// <summary>
/// Resolves the data folders. Root is %ProgramData%\SuperOfc unless overridden by the SUPEROFC_DATA
/// environment variable (tests, portable use) or by "dataRoot" in %ProgramData%\SuperOfc\settings.json.
/// </summary>
public sealed class AppEnvironment : IAppEnvironment
{
    public const string EnvironmentVariable = "SUPEROFC_DATA";

    public AppEnvironment(string? rootOverride = null)
    {
        Root = rootOverride ?? ResolveRoot();
        AppVersion = ResolveVersion();
    }

    public string Root { get; }

    public string AppVersion { get; }

    public string DataDirectory => Path.Combine(Root, "data");

    public string FilesDirectory => Path.Combine(Root, "files");

    public string BackupDirectory => Path.Combine(Root, "backups");

    public string LogDirectory => Path.Combine(Root, "logs");

    public string DatabasePath => Path.Combine(DataDirectory, "superofc.db");

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SuperOfc");

    public string ConnectionString => $"Data Source={DatabasePath};Default Timeout=5;Pooling=True";

    /// <summary>Creates the folders and, under ProgramData, grants Users modify rights so every account can share data.</summary>
    public void EnsureDirectories()
    {
        var isNew = !Directory.Exists(Root);
        Directory.CreateDirectory(Root);
        if (isNew && Root.StartsWith(DefaultRoot, StringComparison.OrdinalIgnoreCase))
        {
            TryGrantUsersModify(Root);
        }

        foreach (var dir in new[] { DataDirectory, FilesDirectory, BackupDirectory, LogDirectory })
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static string ResolveRoot()
    {
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        var settingsPath = Path.Combine(DefaultRoot, "settings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("dataRoot", out var root) && root.GetString() is { Length: > 0 } custom)
                {
                    return custom;
                }
            }
            catch (JsonException)
            {
                // A broken settings file falls back to the default location; logged by the caller at startup.
            }
        }

        return DefaultRoot;
    }

    private static string ResolveVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppEnvironment).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return info ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    private static void TryGrantUsersModify(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            var security = dir.GetAccessControl();
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            security.AddAccessRule(new FileSystemAccessRule(
                users,
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            dir.SetAccessControl(security);
        }
        catch (UnauthorizedAccessException)
        {
            // Not fatal: the current user can still use the folder. Other accounts may not.
        }
    }
}
