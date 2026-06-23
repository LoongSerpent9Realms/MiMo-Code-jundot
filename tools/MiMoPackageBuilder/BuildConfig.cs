using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiMoPackageBuilder;

public enum BuildMode
{
    Single,
    SingleBaseline,
    AllTargets,
    Release
}

public sealed class BuildConfig
{
    public const string DefaultGitHubRepo = "LoongSerpent9Realms/MiMo-Code-jundot";

    public string RepoRoot { get; set; } = "";
    public BuildMode Mode { get; set; } = BuildMode.Single;
    public string VersionOverride { get; set; } = "";
    public string VersionBump { get; set; } = "";
    public string Channel { get; set; } = "Jundot";
    public string BunPath { get; set; } = "";
    public string GitHubRepo { get; set; } = DefaultGitHubRepo;
    public string GitHubTokenEnvVar { get; set; } = "GH_TOKEN";
    public string NpmTokenEnvVar { get; set; } = "NPM_TOKEN";
    public bool SkipInstall { get; set; } = true;
    public bool SkipEmbedWebUi { get; set; } = true;
    public bool ConfirmPublish { get; set; }
    public bool RunBunInstallFirst { get; set; }
    public bool FastMode { get; set; } = true;
    public bool CacheModelsDev { get; set; } = true;
    public int ParallelCount { get; set; } = 2;
    public bool PublishSelfToGitHub { get; set; }
    public string PublishSelfVersion { get; set; } = "";
    public bool SkipNpmPublish { get; set; }

    [JsonIgnore]
    public bool IsRelease => Mode == BuildMode.Release;

    private static string DetectVersion(string repoRoot)
    {
        try
        {
            var pkgPath = Path.Combine(repoRoot, "packages", "opencode", "package.json");
            if (File.Exists(pkgPath))
            {
                var pkg = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(pkgPath));
                if (pkg.TryGetProperty("version", out var versionProp) && versionProp.ValueKind == JsonValueKind.String)
                    return versionProp.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    public static string ConfigPath(string repoRoot) =>
        Path.Combine(repoRoot, "artifacts", "package-builder", "builder-config.json");

    public static BuildConfig Load(string repoRoot)
    {
        var defaultVersion = DetectVersion(repoRoot);
        var path = ConfigPath(repoRoot);
        if (!File.Exists(path))
            return new BuildConfig { RepoRoot = repoRoot, PublishSelfVersion = defaultVersion };

        try
        {
            var config = JsonSerializer.Deserialize<BuildConfig>(File.ReadAllText(path)) ?? new BuildConfig();
            config.RepoRoot = string.IsNullOrWhiteSpace(config.RepoRoot) ? repoRoot : config.RepoRoot;
            config.GitHubRepo = string.IsNullOrWhiteSpace(config.GitHubRepo) ? DefaultGitHubRepo : config.GitHubRepo;
            if (string.IsNullOrWhiteSpace(config.Channel) ||
                config.Channel.Equals("latest", StringComparison.OrdinalIgnoreCase))
                config.Channel = "Jundot";
            // Auto-fill PublishSelfVersion from package.json if empty
            if (string.IsNullOrWhiteSpace(config.PublishSelfVersion))
                config.PublishSelfVersion = defaultVersion;
            return config;
        }
        catch
        {
            return new BuildConfig { RepoRoot = repoRoot, PublishSelfVersion = defaultVersion };
        }
    }

    public void Save()
    {
        var path = ConfigPath(RepoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
