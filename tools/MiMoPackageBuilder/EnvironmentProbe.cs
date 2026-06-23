using System.Diagnostics;
using System.Text;

namespace MiMoPackageBuilder;

public sealed record ProbeResult(bool Ok, string Message);

public static class EnvironmentProbe
{
    private static string ResolveToken(string envVarNameOrValue)
    {
        if (string.IsNullOrWhiteSpace(envVarNameOrValue)) return "";
        var fromEnv = Environment.GetEnvironmentVariable(envVarNameOrValue);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
        var trimmed = envVarNameOrValue.Trim();
        var looksLikeToken = trimmed.StartsWith("ghp_") || trimmed.StartsWith("github_pat_") ||
                              trimmed.StartsWith("glpat-") || trimmed.StartsWith("glrt-") ||
                              trimmed.Length > 30;
        return looksLikeToken ? trimmed : "";
    }

    public static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (IsRepoRoot(dir)) return dir;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        var cwd = Directory.GetCurrentDirectory();
        if (IsRepoRoot(cwd)) return cwd;

        var fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return IsRepoRoot(fallback) ? fallback : cwd;
    }

    public static bool IsRepoRoot(string path) =>
        File.Exists(Path.Combine(path, "package.json")) &&
        File.Exists(Path.Combine(path, "script", "release.ts")) &&
        File.Exists(Path.Combine(path, "packages", "opencode", "script", "build.ts"));

    public static async Task<List<ProbeResult>> ProbeAsync(BuildConfig config, CancellationToken ct = default)
    {
        var results = new List<ProbeResult>
        {
            new(EnvironmentProbe.IsRepoRoot(config.RepoRoot), $"Repo root: {config.RepoRoot}")
        };

        var bunCommand = string.IsNullOrWhiteSpace(config.BunPath) ? "bun" : config.BunPath.Trim();
        results.Add(await ProbeCommandAsync(bunCommand, "--version", "bun", ct));
        results.Add(await ProbeCommandAsync("git", "--version", "git", ct));
        results.Add(await ProbeCommandAsync("npm", "--version", "npm", ct));

        var needsGitHub = config.Mode == BuildMode.Release || config.PublishSelfToGitHub;
        var gh = await ProbeCommandAsync("gh", "--version", "gh", ct);
        results.Add(needsGitHub ? gh : gh with { Ok = true, Message = $"Optional {gh.Message}" });

        if (config.Mode == BuildMode.Release || config.PublishSelfToGitHub)
        {
            var ghToken = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(config.GitHubTokenEnvVar))
                ? ResolveToken(config.GitHubTokenEnvVar)
                : Environment.GetEnvironmentVariable(config.GitHubTokenEnvVar);
            if (string.IsNullOrWhiteSpace(ghToken)) ghToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? "";
            results.Add(new(!string.IsNullOrWhiteSpace(ghToken), $"GitHub token ({config.GitHubTokenEnvVar} or GITHUB_TOKEN)"));
            
            if (!config.SkipNpmPublish)
            {
                var npmToken = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(config.NpmTokenEnvVar))
                    ? ResolveToken(config.NpmTokenEnvVar)
                    : Environment.GetEnvironmentVariable(config.NpmTokenEnvVar);
                results.Add(new(!string.IsNullOrWhiteSpace(npmToken), $"npm token ({config.NpmTokenEnvVar})"));
            }
            else
            {
                results.Add(new(true, "npm token: skipped (SKIP_NPM_PUBLISH)"));
            }
        }

        if (config.Mode == BuildMode.Release)
        {
            results.Add(new(config.ConfirmPublish, "Release mode publish confirmation"));
        }

        return results;
    }

    private static async Task<ProbeResult> ProbeCommandAsync(string fileName, string argument, string label, CancellationToken ct)
    {
        var resolved = CommandResolver.Resolve(fileName);
        if (!File.Exists(resolved))
        {
            var extra = label.Equals("bun", StringComparison.OrdinalIgnoreCase)
                ? " Install Bun or set the Bun executable path in Build settings."
                : "";
            return new ProbeResult(false, $"{label}: not found ({fileName}).{extra}");
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = resolved,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add(argument);
            var output = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(ct);

            var text = output.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "found";
            return new ProbeResult(process.ExitCode == 0, $"{label}: {text}");
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, $"{label}: {ex.Message}");
        }
    }
}
