using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace MiMoPackageBuilder;

public sealed class BuildProgressEventArgs : EventArgs
{
    public string Message { get; init; } = "";
    public string MessageType { get; init; } = "info";
}

public sealed class BuildEngine
{
    private readonly BuildConfig _config;
    private readonly BuildManager _buildManager;
    private readonly CancellationToken _ct;
    private Process? _currentProcess;
    private readonly StringBuilder _log = new();

    public event EventHandler<BuildProgressEventArgs>? ProgressChanged;

    public BuildEngine(BuildConfig config, BuildManager buildManager, CancellationToken ct)
    {
        _config = config;
        _buildManager = buildManager;
        _ct = ct;
    }

    public async Task<BuildRecord> RunAsync()
    {
        var startedAt = DateTime.Now;
        var commandLine = BuildCommandPreview(_config);
        var logPath = Path.Combine(_buildManager.LogDir, $"mimo-package-builder-{startedAt:yyyyMMdd-HHmmss}.log");
        var exitCode = -1;

        try
        {
            if (_config.PublishSelfToGitHub)
            {
                Report("Publishing MiMoPackageBuilder to GitHub Releases", "step");
                exitCode = await PublishSelfToGitHubAsync();
                if (exitCode != 0)
                    throw new InvalidOperationException($"Self-publish failed with exit code {exitCode}.");
                Report("Self-publish finished", "success");
            }
            else
            {
                if (_config.FastMode)
                    Report("Fast mode: skip install + skip embed web-ui + parallel builds", "info");
                if (_config.CacheModelsDev)
                    Report("Models.dev caching: enabled (24h)", "info");
                Report($"Parallel build limit: {Math.Clamp(_config.ParallelCount, 1, 8)}", "info");

                Report("Checking environment", "step");
                var probe = await EnvironmentProbe.ProbeAsync(_config, _ct);
                foreach (var result in probe)
                    Report($"{(result.Ok ? "OK" : "FAIL")} {result.Message}", result.Ok ? "info" : "error");
                if (probe.Any(r => !r.Ok))
                    throw new InvalidOperationException("Environment check failed.");

                if (_config.RunBunInstallFirst)
                    await RunProcessAsync("bun", ["install"], _config.RepoRoot);

                var command = BuildCommand(_config);
                Report($"Running: {Mask(commandLine)}", "step");
                exitCode = await RunProcessAsync(command.FileName, command.Arguments, _config.RepoRoot);
                if (exitCode != 0)
                    throw new InvalidOperationException($"Command failed with exit code {exitCode}.");

                CreatePackageArchives();
                Report("Build finished", "success");
            }
        }
        catch (OperationCanceledException)
        {
            Report("Build cancelled", "warning");
            exitCode = -2;
        }
        catch (Exception ex)
        {
            Report($"ERROR: {ex.Message}", "error");
            if (exitCode == -1) exitCode = 1;
        }
        finally
        {
            await File.WriteAllTextAsync(logPath, _log.ToString(), Encoding.UTF8);
        }

        var record = CreateRecord(startedAt, DateTime.Now, exitCode, commandLine, logPath);
        _buildManager.SaveRecord(record);
        return record;
    }

    public void Cancel()
    {
        try { _currentProcess?.Kill(entireProcessTree: true); } catch { }
    }

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

    private static string DetectVersion(string repoRoot)
    {
        try
        {
            var pkgPath = Path.Combine(repoRoot, "packages", "opencode", "package.json");
            if (File.Exists(pkgPath))
            {
                var pkg = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(File.ReadAllText(pkgPath));
                if (pkg.TryGetProperty("version", out var versionProp) && versionProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    return versionProp.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    private async Task<int> PublishSelfToGitHubAsync()
    {
        var projectDir = Path.Combine(_config.RepoRoot, "tools", "MiMoPackageBuilder");
        var projectFile = Path.Combine(projectDir, "MiMoPackageBuilder.csproj");
        if (!File.Exists(projectFile))
            throw new InvalidOperationException($"Cannot find project file at {projectFile}");

        var version = string.IsNullOrWhiteSpace(_config.PublishSelfVersion) ? DetectVersion(_config.RepoRoot) : _config.PublishSelfVersion.Trim();
        if (string.IsNullOrWhiteSpace(version)) version = "1.0.0";
        var tagName = $"mimo-package-builder-v{version}";

        // 1) dotnet publish -c Release
        Report($"[1/4] dotnet publish MiMoPackageBuilder.csproj (v{version})", "info");
        var publishExit = await RunProcessAsync(
            "dotnet",
            new[] { "publish", projectFile, "-c", "Release", "--nologo", "-o", Path.Combine(projectDir, "publish-out") },
            _config.RepoRoot);
        if (publishExit != 0) return publishExit;

        // 2) 压缩 publish 目录为 zip
        var zipName = $"MiMoPackageBuilder-v{version}-win-x64.zip";
        var zipPath = Path.Combine(_config.RepoRoot, "artifacts", "package-builder", zipName);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        Report($"[2/4] Creating {zipName}", "info");
        var zipSource = Path.Combine(projectDir, "publish-out");
        System.IO.Compression.ZipFile.CreateFromDirectory(zipSource, zipPath);
        Report($"  Created {zipPath} ({new FileInfo(zipPath).Length / 1024} KB)", "info");

        // 3) 检查 gh CLI 可用性
        if (!CommandResolver.Exists("gh"))
            throw new InvalidOperationException("GitHub CLI (gh) not found. Install from https://cli.github.com and run `gh auth login`.");

        // 4) 创建或更新 GitHub Release 并上传 zip
        Report($"[3/4] Creating GitHub Release {tagName}", "info");
        var ghToken = ResolveToken(_config.GitHubTokenEnvVar);
        if (string.IsNullOrWhiteSpace(ghToken))
            throw new InvalidOperationException($"GitHub token not found. Either set environment variable {_config.GitHubTokenEnvVar}, or paste the token directly into the \"GitHub token env\" field.");

        var releaseNotes = $"MiMoPackageBuilder v{version} — 自动化发布";
        var createExit = await RunProcessAsync(
            "gh",
            new[]
            {
                "release", "create", tagName,
                "--repo", _config.GitHubRepo,
                "--title", $"MiMoPackageBuilder v{version}",
                "--notes", releaseNotes
            },
            _config.RepoRoot);
        if (createExit != 0) return createExit;

        Report($"[4/4] Uploading {zipName} to GitHub Release", "info");
        var uploadExit = await RunProcessAsync(
            "gh",
            new[] { "release", "upload", tagName, zipPath, "--repo", _config.GitHubRepo, "--clobber" },
            _config.RepoRoot);
        if (uploadExit != 0) return uploadExit;

        Report($"  Published to https://github.com/{_config.GitHubRepo}/releases/tag/{tagName}", "success");
        return 0;
    }

    public static string BuildCommandPreview(BuildConfig config)
    {
        var command = BuildCommand(config);
        return $"{command.FileName} {string.Join(" ", command.Arguments.Select(QuoteIfNeeded))}";
    }

    private static (string FileName, List<string> Arguments) BuildCommand(BuildConfig config)
    {
        var bun = string.IsNullOrWhiteSpace(config.BunPath) ? "bun" : config.BunPath.Trim();
        var args = new List<string>();
        switch (config.Mode)
        {
            case BuildMode.Single:
                args.Add("packages/opencode/script/build.ts");
                args.Add("--single");
                AddCommonBuildFlags(config, args);
                break;
            case BuildMode.SingleBaseline:
                args.Add("packages/opencode/script/build.ts");
                args.Add("--single");
                args.Add("--baseline");
                AddCommonBuildFlags(config, args);
                break;
            case BuildMode.AllTargets:
                args.Add("packages/opencode/script/build.ts");
                AddCommonBuildFlags(config, args);
                break;
            case BuildMode.Release:
                args.Add("script/release.ts");
                break;
        }

        return (bun, args);
    }

    private static void AddCommonBuildFlags(BuildConfig config, List<string> args)
    {
        if (config.SkipInstall) args.Add("--skip-install");
        if (config.SkipEmbedWebUi) args.Add("--skip-embed-web-ui");
    }

    private async Task<int> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.FileName = CommandResolver.Resolve(process.StartInfo.FileName);
        ApplyEnvironment(process.StartInfo);

        process.OutputDataReceived += (_, e) => { if (e.Data != null) Report(e.Data, "output"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Report(e.Data, "output"); };

        _currentProcess = process;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(_ct);
        _currentProcess = null;
        return process.ExitCode;
    }

    private void ApplyEnvironment(ProcessStartInfo psi)
    {
        psi.Environment["GH_REPO"] = _config.GitHubRepo;
        psi.Environment["OPENCODE_CHANNEL"] = _config.Channel;

        if (!string.IsNullOrWhiteSpace(_config.VersionOverride))
            psi.Environment["OPENCODE_VERSION"] = _config.VersionOverride.Trim();
        if (!string.IsNullOrWhiteSpace(_config.VersionBump))
            psi.Environment["OPENCODE_BUMP"] = _config.VersionBump.Trim();

        var ghToken = ResolveToken(_config.GitHubTokenEnvVar);
        if (!string.IsNullOrWhiteSpace(ghToken))
            psi.Environment["GH_TOKEN"] = ghToken;

        var npmToken = ResolveToken(_config.NpmTokenEnvVar);
        if (!string.IsNullOrWhiteSpace(npmToken))
        {
            psi.Environment["NPM_TOKEN"] = npmToken;
            psi.Environment["NODE_AUTH_TOKEN"] = npmToken;
        }

        if (_config.FastMode)
        {
            psi.Environment["MIMO_FAST"] = "1";
        }

        if (_config.CacheModelsDev)
        {
            psi.Environment["MIMO_MODELS_CACHE"] = "1";
        }

        if (_config.SkipNpmPublish)
        {
            psi.Environment["SKIP_NPM_PUBLISH"] = "1";
        }

        var parallel = Math.Clamp(_config.ParallelCount, 1, 8);
        psi.Environment["MIMO_PARALLEL"] = parallel.ToString();
    }

    private void CreatePackageArchives()
    {
        var dist = Path.Combine(_config.RepoRoot, "packages", "opencode", "dist");
        if (!Directory.Exists(dist)) return;

        foreach (var packageDir in Directory.EnumerateDirectories(dist, "mimocode-*", SearchOption.TopDirectoryOnly))
        {
            var binDir = Path.Combine(packageDir, "bin");
            var sourceDir = Directory.Exists(binDir) ? binDir : packageDir;
            var archivePath = Path.Combine(dist, $"{Path.GetFileName(packageDir)}.zip");
            if (File.Exists(archivePath)) File.Delete(archivePath);

            Report($"Creating package archive: {Path.GetFileName(archivePath)}", "info");
            ZipFile.CreateFromDirectory(sourceDir, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            Report($"  Created {archivePath} ({new FileInfo(archivePath).Length / 1024} KB)", "info");
        }
    }

    private BuildRecord CreateRecord(DateTime startedAt, DateTime finishedAt, int exitCode, string commandLine, string logPath)
    {
        var dist = Path.Combine(_config.RepoRoot, "packages", "opencode", "dist");
        var newestArchive = Directory.Exists(dist)
            ? Directory.EnumerateFiles(dist, "mimocode-*.*", SearchOption.TopDirectoryOnly)
                .Where(p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault() ?? ""
            : "";
        var newestPackage = Directory.Exists(dist)
            ? Directory.EnumerateDirectories(dist, "mimocode-*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Directory.GetLastWriteTime)
                .FirstOrDefault() ?? ""
            : "";

        return new BuildRecord
        {
            Name = string.IsNullOrWhiteSpace(newestArchive) ? Path.GetFileName(newestPackage) : Path.GetFileName(newestArchive),
            Version = _config.VersionOverride,
            Mode = _config.Mode.ToString(),
            PackageDir = newestPackage,
            ArchivePath = newestArchive,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            ExitCode = exitCode,
            CommandLine = commandLine,
            LogPath = logPath
        };
    }

    private void Report(string message, string type)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _log.AppendLine(line);
        ProgressChanged?.Invoke(this, new BuildProgressEventArgs { Message = line, MessageType = type });
    }

    private static string QuoteIfNeeded(string value) =>
        value.Contains(' ') ? $"\"{value}\"" : value;

    private static string Mask(string text)
    {
        foreach (var key in new[] { "NPM_TOKEN", "GH_TOKEN", "GITHUB_TOKEN", "NODE_AUTH_TOKEN" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
                text = text.Replace(value, "***", StringComparison.Ordinal);
        }

        return text;
    }
}
