using System.Diagnostics;

namespace MiMoPackageBuilder;

public sealed class MainForm : Form
{
    private readonly TextBox _repoRoot = new();
    private readonly ComboBox _mode = new();
    private readonly TextBox _version = new();
    private readonly ComboBox _bump = new();
    private readonly ComboBox _channel = new();
    private readonly TextBox _bunPath = new();
    private readonly TextBox _ghRepo = new();
    private readonly TextBox _ghTokenEnv = new();
    private readonly TextBox _npmTokenEnv = new();
    private readonly CheckBox _skipInstall = new();
    private readonly CheckBox _skipEmbedWebUi = new();
    private readonly CheckBox _bunInstallFirst = new();
    private readonly CheckBox _confirmPublish = new();
    private readonly CheckBox _publishSelf = new();
    private readonly TextBox _publishSelfVersion = new();
    private readonly CheckBox _skipNpmPublish = new();
    private readonly CheckBox _fastMode = new();
    private readonly CheckBox _cacheModels = new();
    private readonly NumericUpDown _parallelCount = new();
    private readonly RichTextBox _log = new();
    private readonly Button _clearLog = new();
    private readonly ListView _builds = new();
    private readonly Button _run = new();
    private readonly Button _cancel = new();
    private readonly Label _status = new();

    private BuildConfig _config;
    private BuildManager _manager;
    private CancellationTokenSource? _cts;
    private BuildEngine? _engine;

    public MainForm()
    {
        Text = "MiMo Package Builder";
        Width = 980;
        Height = 720;
        MinimumSize = new Size(820, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;

        var root = EnvironmentProbe.FindRepoRoot();
        _config = BuildConfig.Load(root);
        _manager = new BuildManager(_config.RepoRoot);

        BuildUi();
        LoadConfigToUi();
        RefreshBuilds();
        FormClosing += (_, _) => SaveUiToConfig().Save();
    }

    private void BuildUi()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreateTab("Build", BuildSettingsPage()));
        tabs.TabPages.Add(CreateTab("Publish", PublishPage()));
        tabs.TabPages.Add(CreateTab("Builds", BuildsPage()));
        tabs.TabPages.Add(CreateTab("Log", LogPage()));

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            ColumnCount = 4,
            Padding = new Padding(8)
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));

        _status.Text = "Ready";
        _status.AutoEllipsis = true;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _run.Text = "Run";
        _run.Dock = DockStyle.Fill;
        _run.Click += async (_, _) => await RunAsync();
        _cancel.Text = "Cancel";
        _cancel.Dock = DockStyle.Fill;
        _cancel.Enabled = false;
        _cancel.Click += (_, _) => _engine?.Cancel();

        var probe = new Button { Text = "Check Env", Dock = DockStyle.Fill };
        probe.Click += async (_, _) => await CheckEnvironmentAsync();

        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(_run, 1, 0);
        bottom.Controls.Add(_cancel, 2, 0);
        bottom.Controls.Add(probe, 3, 0);

        Controls.Add(tabs);
        Controls.Add(bottom);
    }

    private Control BuildSettingsPage()
    {
        var panel = CreateFormPanel();
        var browse = new Button { Text = "Browse..." };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = _repoRoot.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) _repoRoot.Text = dialog.SelectedPath;
        };

        AddRow(panel, "Repo root", _repoRoot, browse);
        _mode.DropDownStyle = ComboBoxStyle.DropDownList;
        _mode.Items.AddRange(Enum.GetNames<BuildMode>());
        AddRow(panel, "Mode", _mode);
        AddRow(panel, "Version override", _version);
        _bump.DropDownStyle = ComboBoxStyle.DropDownList;
        _bump.Items.AddRange(new object[] { "", "patch", "minor", "major" });
        AddRow(panel, "Version bump", _bump);
        _channel.DropDownStyle = ComboBoxStyle.DropDown;
        _channel.Items.AddRange(new object[] { "Jundot", "latest", "beta", "dev" });
        AddRow(panel, "Channel", _channel);
        var browseBun = new Button { Text = "Browse..." };
        browseBun.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "Bun executable|bun.exe;bun.cmd;*.exe;*.cmd|All files|*.*",
                FileName = string.IsNullOrWhiteSpace(_bunPath.Text) ? "bun.exe" : _bunPath.Text
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) _bunPath.Text = dialog.FileName;
        };
        AddRow(panel, "Bun path", _bunPath, browseBun);

        _bunInstallFirst.Text = "Run bun install first";
        _skipInstall.Text = "Pass --skip-install to build script";
        _skipEmbedWebUi.Text = "Pass --skip-embed-web-ui";
        _fastMode.Text = "Fast mode (recommended): skip install, skip web-ui, parallel builds";
        _cacheModels.Text = "Cache models.dev API (24h)";
        _parallelCount.Minimum = 1;
        _parallelCount.Maximum = 8;
        _parallelCount.Value = 2;
        AddRow(panel, "", _bunInstallFirst);
        AddRow(panel, "", _skipInstall);
        AddRow(panel, "", _skipEmbedWebUi);
        AddRow(panel, "", _fastMode);
        AddRow(panel, "", _cacheModels);
        AddRow(panel, "Parallel builds (1-8)", _parallelCount);

        return panel;
    }

    private static TabPage CreateTab(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }

    private Control PublishPage()
    {
        var panel = CreateFormPanel();
        AddRow(panel, "GH_REPO", _ghRepo);
        AddRow(panel, "GitHub token env", _ghTokenEnv);
        AddRow(panel, "npm token env", _npmTokenEnv);
        _confirmPublish.Text = "I understand Release mode can publish npm packages and GitHub Releases";
        AddRow(panel, "", _confirmPublish);
        _skipNpmPublish.Text = "Skip npm publish (only upload to GitHub Releases)";
        AddRow(panel, "", _skipNpmPublish);

        // --- MiMoPackageBuilder self-publish section ---
        var header = new Label
        {
            Text = "— 发布打包器工具自身 —",
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            ForeColor = Color.DarkSlateGray,
            AutoSize = true,
            Padding = new Padding(0, 16, 0, 0)
        };
        panel.Controls.Add(header);

        _publishSelf.Text = "Publish MiMoPackageBuilder to GitHub Releases (instead of running an opencode build)";
        AddRow(panel, "", _publishSelf);
        AddRow(panel, "Version", _publishSelfVersion);

        var note = new Label
        {
            Text = "Self-publish will run `dotnet publish -c Release`, zip the output, and upload to GitHub Releases via `gh release create`.\nRequires: dotnet CLI + GitHub CLI (`gh auth login`).",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(0, 8, 0, 0)
        };
        panel.Controls.Add(note);
        return panel;
    }

    private Control BuildsPage()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(10) };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        var refresh = new Button { Text = "Refresh", Width = 90 };
        var open = new Button { Text = "Open", Width = 90 };
        var viewLog = new Button { Text = "View Log", Width = 90 };
        var delete = new Button { Text = "Delete", Width = 90 };
        refresh.Click += (_, _) => RefreshBuilds();
        open.Click += (_, _) => OpenSelected();
        viewLog.Click += (_, _) => ViewSelectedLog();
        delete.Click += (_, _) => DeleteSelected();
        actions.Controls.AddRange(new Control[] { refresh, open, viewLog, delete });

        _builds.Dock = DockStyle.Fill;
        _builds.View = View.Details;
        _builds.FullRowSelect = true;
        _builds.MultiSelect = false;
        _builds.Columns.Add("Name", 260);
        _builds.Columns.Add("Version", 90);
        _builds.Columns.Add("Mode", 110);
        _builds.Columns.Add("Platform", 90);
        _builds.Columns.Add("Arch", 80);
        _builds.Columns.Add("Date", 140);
        _builds.Columns.Add("Size", 90);
        _builds.Columns.Add("Status", 80);
        _builds.DoubleClick += (_, _) => OpenSelected();

        panel.Controls.Add(actions, 0, 0);
        panel.Controls.Add(_builds, 0, 1);
        return panel;
    }

    private Control LogPage()
    {
        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true;
        _log.Font = new Font("Consolas", 10);
        _log.BackColor = Color.FromArgb(30, 30, 30);
        _log.ForeColor = Color.Gainsboro;

        _clearLog.Text = "Clear Log";
        _clearLog.Dock = DockStyle.Top;
        _clearLog.Height = 30;
        _clearLog.Click += (_, _) => _log.Clear();

        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(_log);
        panel.Controls.Add(_clearLog);
        return panel;
    }

    private async Task RunAsync()
    {
        var config = SaveUiToConfig();
        if (!EnvironmentProbe.IsRepoRoot(config.RepoRoot))
        {
            MessageBox.Show("Repo root is invalid.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (config.PublishSelfToGitHub)
        {
            var answerSelf = MessageBox.Show(
                "This will publish MiMoPackageBuilder to GitHub Releases. It runs dotnet publish, creates a zip, and uploads to GitHub.\n\nGH_REPO must be set and `gh auth login` must be done. Continue?",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answerSelf != DialogResult.Yes) return;
        }

        if (config.Mode == BuildMode.Release && !config.ConfirmPublish)
        {
            MessageBox.Show("Release mode requires publish confirmation.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (config.Mode == BuildMode.Release)
        {
            var answer = MessageBox.Show(
                "Release mode runs script/release.ts and may publish npm packages and GitHub Releases. Continue?",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }

        _config = config;
        _manager = new BuildManager(config.RepoRoot);
        _cts = new CancellationTokenSource();
        _engine = new BuildEngine(config, _manager, _cts.Token);
        _engine.ProgressChanged += (_, e) => AppendLog(e.Message);

        SetRunning(true);
        try
        {
            var record = await _engine.RunAsync();
            _status.Text = record.ExitCode == 0 ? "Done" : record.Status;
            RefreshBuilds();
        }
        finally
        {
            SetRunning(false);
            _engine = null;
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task CheckEnvironmentAsync()
    {
        var config = SaveUiToConfig();
        AppendLog("=== Environment check ===");
        foreach (var result in await EnvironmentProbe.ProbeAsync(config))
            AppendLog($"{(result.Ok ? "OK" : "FAIL")} {result.Message}");
    }

    private void RefreshBuilds()
    {
        _builds.Items.Clear();
        foreach (var build in _manager.GetAllBuilds())
        {
            var item = new ListViewItem(build.Name);
            item.SubItems.Add(build.Version);
            item.SubItems.Add(build.Mode);
            item.SubItems.Add(build.Platform);
            item.SubItems.Add(build.Arch);
            item.SubItems.Add(build.FinishedAt.ToString("yyyy-MM-dd HH:mm"));
            item.SubItems.Add(build.SizeDisplay);
            item.SubItems.Add(build.Status);
            item.Tag = build;
            _builds.Items.Add(item);
        }
    }

    private BuildRecord? SelectedBuild =>
        _builds.SelectedItems.Count == 1 ? _builds.SelectedItems[0].Tag as BuildRecord : null;

    private void OpenSelected()
    {
        var build = SelectedBuild;
        if (build == null) return;
        var path = File.Exists(build.PrimaryPath) ? Path.GetDirectoryName(build.PrimaryPath) : build.PrimaryPath;
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void ViewSelectedLog()
    {
        var build = SelectedBuild;
        if (build == null || string.IsNullOrWhiteSpace(build.LogPath) || !File.Exists(build.LogPath)) return;
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{build.LogPath}\"") { UseShellExecute = true });
    }

    private void DeleteSelected()
    {
        var build = SelectedBuild;
        if (build == null) return;
        if (MessageBox.Show($"Delete build record and files?\n\n{build.Name}", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _manager.DeleteRecord(build);
        RefreshBuilds();
    }

    private BuildConfig SaveUiToConfig()
    {
        _config.RepoRoot = _repoRoot.Text.Trim();
        _config.Mode = Enum.TryParse<BuildMode>(_mode.Text, out var mode) ? mode : BuildMode.Single;
        _config.VersionOverride = _version.Text.Trim();
        _config.VersionBump = _bump.Text.Trim();
        _config.Channel = _channel.Text.Trim();
        _config.BunPath = _bunPath.Text.Trim();
        _config.GitHubRepo = _ghRepo.Text.Trim();
        _config.GitHubTokenEnvVar = _ghTokenEnv.Text.Trim();
        _config.NpmTokenEnvVar = _npmTokenEnv.Text.Trim();
        _config.SkipInstall = _skipInstall.Checked;
        _config.SkipEmbedWebUi = _skipEmbedWebUi.Checked;
        _config.RunBunInstallFirst = _bunInstallFirst.Checked;
        _config.ConfirmPublish = _confirmPublish.Checked;
        _config.FastMode = _fastMode.Checked;
        _config.CacheModelsDev = _cacheModels.Checked;
        _config.ParallelCount = (int)_parallelCount.Value;
        _config.PublishSelfToGitHub = _publishSelf.Checked;
        _config.PublishSelfVersion = _publishSelfVersion.Text.Trim();
        _config.SkipNpmPublish = _skipNpmPublish.Checked;
        _config.Save();
        return _config;
    }

    private void LoadConfigToUi()
    {
        _repoRoot.Text = _config.RepoRoot;
        _mode.SelectedItem = _config.Mode.ToString();
        _version.Text = _config.VersionOverride;
        _bump.SelectedItem = _config.VersionBump;
        _channel.Text = _config.Channel;
        _bunPath.Text = _config.BunPath;
        _ghRepo.Text = _config.GitHubRepo;
        _ghTokenEnv.Text = _config.GitHubTokenEnvVar;
        _npmTokenEnv.Text = _config.NpmTokenEnvVar;
        _skipInstall.Checked = _config.SkipInstall;
        _skipEmbedWebUi.Checked = _config.SkipEmbedWebUi;
        _bunInstallFirst.Checked = _config.RunBunInstallFirst;
        _confirmPublish.Checked = _config.ConfirmPublish;
        _fastMode.Checked = _config.FastMode;
        _cacheModels.Checked = _config.CacheModelsDev;
        _parallelCount.Value = Math.Clamp(_config.ParallelCount, 1, 8);
        _publishSelf.Checked = _config.PublishSelfToGitHub;
        _publishSelfVersion.Text = _config.PublishSelfVersion;
        _skipNpmPublish.Checked = _config.SkipNpmPublish;
    }

    private void SetRunning(bool running)
    {
        _run.Enabled = !running;
        _cancel.Enabled = running;
        _status.Text = running ? "Running..." : _status.Text;
    }

    private void AppendLog(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(text));
            return;
        }
        _log.AppendText(text + Environment.NewLine);
        _log.ScrollToCaret();
    }

    private static TableLayoutPanel CreateFormPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(12),
            ColumnCount = 3
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        return panel;
    }

    private static void AddRow(TableLayoutPanel panel, string label, Control control, Control? side = null)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, row);
        control.Dock = DockStyle.Fill;
        panel.Controls.Add(control, 1, row);
        if (side != null)
        {
            side.Dock = DockStyle.Fill;
            panel.Controls.Add(side, 2, row);
        }
    }
}
