namespace MiMoPackageBuilder;

public static class CommandResolver
{
    public static string Resolve(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return command;

        if (Path.IsPathRooted(command) && File.Exists(command))
            return command;

        foreach (var candidate in KnownCandidates(command))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        var hasExtension = Path.HasExtension(command);
        var extensions = hasExtension
            ? new[] { "" }
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, command + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return command;
    }

    public static bool Exists(string command) => File.Exists(Resolve(command));

    private static IEnumerable<string> KnownCandidates(string command)
    {
        if (!command.Equals("bun", StringComparison.OrdinalIgnoreCase))
            yield break;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, ".bun", "bin", "bun.exe");
            yield return Path.Combine(userProfile, ".bun", "bin", "bun.cmd");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return Path.Combine(localAppData, "Programs", "Bun", "bun.exe");
    }
}
