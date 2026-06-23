using System.Text.Json;

namespace MiMoPackageBuilder;

public sealed class BuildManager
{
    private readonly string _repoRoot;
    private readonly string _historyPath;
    private BuildHistory _history = new();

    public BuildManager(string repoRoot)
    {
        _repoRoot = repoRoot;
        _historyPath = Path.Combine(repoRoot, "artifacts", "package-builder", ".build-history.json");
        Load();
    }

    public string LogDir
    {
        get
        {
            var path = Path.Combine(_repoRoot, "artifacts", "package-builder", "logs");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public List<BuildRecord> GetAllBuilds()
    {
        var records = new List<BuildRecord>();
        records.AddRange(ScanDist());

        foreach (var record in _history.Records)
        {
            if (records.Any(r => SamePath(r.PrimaryPath, record.PrimaryPath))) continue;
            if (record.Exists) records.Add(record);
        }

        return records
            .OrderByDescending(r => r.FinishedAt)
            .ThenByDescending(r => r.StartedAt)
            .ToList();
    }

    public void SaveRecord(BuildRecord record)
    {
        _history.Records.RemoveAll(r => r.Id == record.Id || SamePath(r.PrimaryPath, record.PrimaryPath));
        _history.Records.Insert(0, record);
        _history.LastScanned = DateTime.Now;
        Save();
    }

    public void DeleteRecord(BuildRecord record)
    {
        TryDeleteFile(record.ArchivePath);
        TryDeleteDirectory(record.PackageDir);
        TryDeleteFile(record.LogPath);
        _history.Records.RemoveAll(r => r.Id == record.Id || SamePath(r.PrimaryPath, record.PrimaryPath));
        Save();
    }

    private IEnumerable<BuildRecord> ScanDist()
    {
        var dist = Path.Combine(_repoRoot, "packages", "opencode", "dist");
        if (!Directory.Exists(dist)) yield break;

        foreach (var pkgJson in Directory.EnumerateFiles(dist, "package.json", SearchOption.AllDirectories))
        {
            BuildRecord? record = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pkgJson));
                var root = doc.RootElement;
                var dir = Path.GetDirectoryName(pkgJson)!;
                var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? Path.GetFileName(dir) : Path.GetFileName(dir);
                var version = root.TryGetProperty("version", out var versionProp) ? versionProp.GetString() ?? "" : "";
                var os = ReadFirstArrayValue(root, "os");
                var cpu = ReadFirstArrayValue(root, "cpu");
                record = new BuildRecord
                {
                    Name = name,
                    Version = version,
                    Mode = "discovered",
                    Platform = os,
                    Arch = cpu,
                    PackageDir = dir,
                    FinishedAt = Directory.GetLastWriteTime(dir),
                    StartedAt = Directory.GetCreationTime(dir),
                    ExitCode = 0
                };
            }
            catch { }

            if (record != null) yield return record;
        }

        foreach (var archive in Directory.EnumerateFiles(dist, "mimocode-*.*", SearchOption.TopDirectoryOnly)
                     .Where(p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                 p.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(archive);
            yield return new BuildRecord
            {
                Name = name,
                Mode = "archive",
                ArchivePath = archive,
                FinishedAt = File.GetLastWriteTime(archive),
                StartedAt = File.GetCreationTime(archive),
                ExitCode = 0
            };
        }
    }

    private static string ReadFirstArrayValue(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return "";
        return value.EnumerateArray().FirstOrDefault().GetString() ?? "";
    }

    private void Load()
    {
        if (!File.Exists(_historyPath)) return;
        try
        {
            _history = JsonSerializer.Deserialize<BuildHistory>(File.ReadAllText(_historyPath)) ?? new BuildHistory();
        }
        catch
        {
            _history = new BuildHistory();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
        File.WriteAllText(_historyPath, JsonSerializer.Serialize(_history, BuildHistory.JsonOptions));
    }

    private static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try { File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try { Directory.Delete(path, true); } catch { }
    }
}
