using System.Text.Json;

namespace MiMoPackageBuilder;

public sealed class BuildRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Arch { get; set; } = "";
    public string PackageDir { get; set; } = "";
    public string ArchivePath { get; set; } = "";
    public string LogPath { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public int ExitCode { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime FinishedAt { get; set; } = DateTime.Now;

    public string PrimaryPath => File.Exists(ArchivePath) ? ArchivePath : PackageDir;
    public bool Exists => File.Exists(ArchivePath) || Directory.Exists(PackageDir);
    public string Status => ExitCode == 0 ? "OK" : $"Exit {ExitCode}";
    public string SizeDisplay => FormatSize(GetSize());

    private long GetSize()
    {
        try
        {
            if (File.Exists(ArchivePath)) return new FileInfo(ArchivePath).Length;
            if (Directory.Exists(PackageDir))
                return Directory.EnumerateFiles(PackageDir, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length);
        }
        catch { }

        return 0;
    }

    private static string FormatSize(long size) => size switch
    {
        >= 1024L * 1024 * 1024 => $"{size / (1024.0 * 1024 * 1024):F2} GB",
        >= 1024 * 1024 => $"{size / (1024.0 * 1024):F1} MB",
        >= 1024 => $"{size / 1024.0:F0} KB",
        > 0 => $"{size} B",
        _ => "N/A"
    };
}

public sealed class BuildHistory
{
    public List<BuildRecord> Records { get; set; } = new();
    public DateTime LastScanned { get; set; } = DateTime.Now;

    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };
}
