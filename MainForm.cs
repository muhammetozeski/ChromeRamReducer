using ChromeRamReducer.Core;

namespace ChromeRamReducer;

public sealed class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(28, 30, 34);
    private static readonly Color Muted = Color.FromArgb(110, 116, 126);
    private static readonly Color Surface = Color.FromArgb(246, 247, 249);
    private static readonly Color Accent = Color.FromArgb(26, 115, 232);
    private static readonly Color Good = Color.FromArgb(24, 128, 56);
    private static readonly Color Warn = Color.FromArgb(191, 84, 12);

    private readonly AppSettings _settings;
    private readonly ChromeTrimmer _trimmer;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer _autoTrimTimer = new();

    private readonly Label _statusLabel = new();
    private readonly Label _workingSetValue = new();
    private readonly Label _committedValue = new();
    private readonly Label _processCountLabel = new();
    private readonly Button _trimButton = new();
    private readonly Button _launchButton = new();
    private readonly CheckBox _purgeCheck = new();
    private readonly CheckBox _emptyWorkingSetCheck = new();
    private readonly CheckBox _autoTrimCheck = new();
    private readonly NumericUpDown _autoTrimMinutes = new();
    private readonly NumericUpDown _portInput = new();
    private readonly TextBox _log = new();

    private int? _activePort;
    private bool _busy;
    private bool _reallyClosing;

    public MainForm()
    {
        _settings = AppSettings.Load();
        _trimmer = new ChromeTrimmer(_settings);

        Text = "Chrome RAM Reducer";
        MinimumSize = new Size(620, 640);
        Size = new Size(620, 690);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 9F);
        Icon = TryLoadIcon();

        _trayIcon = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = "Chrome RAM Reducer",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu(),
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        BuildLayout();
        ApplySettingsToControls();

        _statsTimer.Tick += (_, _) => RefreshStats();
        _autoTrimTimer.Tick += async (_, _) => await RunTrimAsync(automatic: true);

        Load += async (_, _) =>
        {
            RefreshStats();
            _statsTimer.Start();
            ConfigureAutoTrimTimer();
            await DiscoverPortAsync();
        };

        FormClosing += OnFormClosing;
    }

    private static Icon? TryLoadIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
        }
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        ContextMenuStrip menu = new();

        ToolStripMenuItem open = new("Open", null, (_, _) => RestoreFromTray());
        ToolStripMenuItem trim = new("Trim now", null, async (_, _) => await RunTrimAsync(automatic: false));
        ToolStripMenuItem exit = new("Exit", null, (_, _) =>
        {
            _reallyClosing = true;
            Close();
        });

        menu.Items.AddRange([open, trim, new ToolStripSeparator(), exit]);
        return menu;
    }

    private void BuildLayout()
    {
        Padding = new Padding(18);

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = Color.White,
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildStatsPanel(), 0, 1);
        root.Controls.Add(BuildActionPanel(), 0, 2);
        root.Controls.Add(BuildOptionsPanel(), 0, 3);
        root.Controls.Add(BuildLegend(), 0, 4);
        root.Controls.Add(BuildLogPanel(), 0, 5);

        Controls.Add(root);
    }

    private Control BuildHeader()
    {
        FlowLayoutPanel header = new()
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 12),
        };

        Label title = new()
        {
            Text = "Chrome RAM Reducer",
            Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0),
        };

        _statusLabel.Text = "Looking for Chrome...";
        _statusLabel.ForeColor = Muted;
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(0, 4, 0, 0);

        header.Controls.Add(title);
        header.Controls.Add(_statusLabel);

        return header;
    }

    private Control BuildStatsPanel()
    {
        TableLayoutPanel panel = new()
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        };

        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

        panel.Controls.Add(
            BuildStatCard("Committed (real)", _committedValue, Good,
                "Memory Chrome actually holds. Only a drop here is a real saving."), 0, 0);

        panel.Controls.Add(
            BuildStatCard("Working set (Task Manager)", _workingSetValue, Muted,
                "What Task Manager shows. Falls when pages merely move to standby."), 1, 0);

        return panel;
    }

    private static Control BuildStatCard(string caption, Label valueLabel, Color valueColor, string explanation)
    {
        Panel card = new()
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            Padding = new Padding(14, 12, 14, 12),
            Margin = new Padding(0, 0, 8, 0),
            AutoSize = true,
        };

        Label captionLabel = new()
        {
            Text = caption,
            ForeColor = Muted,
            AutoSize = true,
            Dock = DockStyle.Top,
        };

        valueLabel.Text = "-";
        valueLabel.Font = new Font("Segoe UI Semibold", 20F, FontStyle.Bold);
        valueLabel.ForeColor = valueColor;
        valueLabel.AutoSize = true;
        valueLabel.Dock = DockStyle.Top;
        valueLabel.Margin = new Padding(0, 4, 0, 4);

        Label note = new()
        {
            Text = explanation,
            ForeColor = Muted,
            AutoSize = false,
            Height = 34,
            Dock = DockStyle.Top,
        };

        card.Controls.Add(note);
        card.Controls.Add(valueLabel);
        card.Controls.Add(captionLabel);

        return card;
    }

    private Control BuildActionPanel()
    {
        FlowLayoutPanel actions = new()
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 12),
        };

        _trimButton.Text = "Trim now";
        _trimButton.Size = new Size(150, 38);
        _trimButton.FlatStyle = FlatStyle.Flat;
        _trimButton.FlatAppearance.BorderSize = 0;
        _trimButton.BackColor = Accent;
        _trimButton.ForeColor = Color.White;
        _trimButton.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
        _trimButton.Cursor = Cursors.Hand;
        _trimButton.Click += async (_, _) => await RunTrimAsync(automatic: false);

        _launchButton.Text = "Start Chrome with debugging";
        _launchButton.Size = new Size(220, 38);
        _launchButton.FlatStyle = FlatStyle.Flat;
        _launchButton.Cursor = Cursors.Hand;
        _launchButton.Margin = new Padding(10, 3, 3, 3);
        _launchButton.Click += (_, _) => LaunchChrome();

        _processCountLabel.Text = string.Empty;
        _processCountLabel.ForeColor = Muted;
        _processCountLabel.AutoSize = true;
        _processCountLabel.Margin = new Padding(12, 12, 0, 0);

        actions.Controls.AddRange([_trimButton, _launchButton, _processCountLabel]);

        return actions;
    }

    private Control BuildOptionsPanel()
    {
        GroupBox group = new()
        {
            Text = "Options",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(12, 8, 12, 12),
            Margin = new Padding(0, 0, 0, 12),
        };

        FlowLayoutPanel stack = new()
        {
            FlowDirection = FlowDirection.TopDown,
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
        };

        _purgeCheck.Text = "Aggressive V8 purge (Memory.forciblyPurgeJavaScriptMemory)";
        _purgeCheck.AutoSize = true;
        _purgeCheck.CheckedChanged += (_, _) =>
        {
            _settings.PurgeJavaScriptMemory = _purgeCheck.Checked;
            _settings.Save();
        };

        _emptyWorkingSetCheck.Text = "Also empty working sets (cosmetic - lowers Task Manager only)";
        _emptyWorkingSetCheck.AutoSize = true;
        _emptyWorkingSetCheck.ForeColor = Warn;
        _emptyWorkingSetCheck.CheckedChanged += (_, _) =>
        {
            _settings.EmptyWorkingSets = _emptyWorkingSetCheck.Checked;
            _settings.Save();
        };

        FlowLayoutPanel autoRow = new()
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0),
        };

        _autoTrimCheck.Text = "Trim automatically every";
        _autoTrimCheck.AutoSize = true;
        _autoTrimCheck.Margin = new Padding(0, 5, 6, 0);
        _autoTrimCheck.CheckedChanged += (_, _) =>
        {
            _settings.AutoTrimEnabled = _autoTrimCheck.Checked;
            _settings.Save();
            ConfigureAutoTrimTimer();
        };

        _autoTrimMinutes.Minimum = 1;
        _autoTrimMinutes.Maximum = 720;
        _autoTrimMinutes.Width = 64;
        _autoTrimMinutes.ValueChanged += (_, _) =>
        {
            _settings.AutoTrimMinutes = (int)_autoTrimMinutes.Value;
            _settings.Save();
            ConfigureAutoTrimTimer();
        };

        Label minutesLabel = new()
        {
            Text = "minutes",
            AutoSize = true,
            Margin = new Padding(6, 5, 0, 0),
        };

        autoRow.Controls.AddRange([_autoTrimCheck, _autoTrimMinutes, minutesLabel]);

        FlowLayoutPanel portRow = new()
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0),
        };

        Label portLabel = new()
        {
            Text = "DevTools port",
            AutoSize = true,
            Margin = new Padding(0, 5, 6, 0),
        };

        _portInput.Minimum = 1024;
        _portInput.Maximum = 65535;
        _portInput.Width = 80;
        _portInput.ValueChanged += (_, _) =>
        {
            _settings.DebuggingPort = (int)_portInput.Value;
            _settings.Save();
        };

        Button rediscover = new()
        {
            Text = "Re-detect",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(10, 1, 0, 0),
            Cursor = Cursors.Hand,
        };
        rediscover.Click += async (_, _) => await DiscoverPortAsync();

        portRow.Controls.AddRange([portLabel, _portInput, rediscover]);

        stack.Controls.AddRange([_purgeCheck, _emptyWorkingSetCheck, autoRow, portRow]);
        group.Controls.Add(stack);

        return group;
    }

    private static Control BuildLegend()
    {
        Label legend = new()
        {
            Text = "Chrome must be started with --remote-debugging-port for the garbage collector to be "
                 + "reachable. Nothing else can reach V8 from outside the browser.",
            ForeColor = Muted,
            AutoSize = false,
            Height = 34,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
        };

        return legend;
    }

    private Control BuildLogPanel()
    {
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Dock = DockStyle.Fill;
        _log.BackColor = Surface;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.Font = new Font("Consolas", 9F);

        return _log;
    }

    private void ApplySettingsToControls()
    {
        _purgeCheck.Checked = _settings.PurgeJavaScriptMemory;
        _emptyWorkingSetCheck.Checked = _settings.EmptyWorkingSets;
        _autoTrimCheck.Checked = _settings.AutoTrimEnabled;
        _autoTrimMinutes.Value = Math.Clamp(_settings.AutoTrimMinutes, 1, 720);
        _portInput.Value = Math.Clamp(_settings.DebuggingPort, 1024, 65535);
    }

    private void ConfigureAutoTrimTimer()
    {
        _autoTrimTimer.Stop();

        if (_settings.AutoTrimEnabled)
        {
            _autoTrimTimer.Interval = Math.Max(1, _settings.AutoTrimMinutes) * 60_000;
            _autoTrimTimer.Start();
        }
    }

    private void RefreshStats()
    {
        MemorySnapshot snapshot = MemorySnapshot.Capture();

        _committedValue.Text = snapshot.HasChrome ? $"{snapshot.PrivateMb:N0} MB" : "-";
        _workingSetValue.Text = snapshot.HasChrome ? $"{snapshot.WorkingSetMb:N0} MB" : "-";
        _processCountLabel.Text = snapshot.HasChrome
            ? $"{snapshot.ProcessCount} Chrome processes"
            : "Chrome is not running";
    }

    private async Task DiscoverPortAsync()
    {
        _statusLabel.Text = "Looking for the DevTools endpoint...";
        _statusLabel.ForeColor = Muted;

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(6));

        try
        {
            _activePort = await ChromeLocator.DiscoverPortAsync(_settings, cts.Token);
        }
        catch (OperationCanceledException)
        {
            _activePort = null;
        }

        UpdatePortStatus();
    }

    private void UpdatePortStatus()
    {
        if (_activePort is int port)
        {
            _statusLabel.Text = $"DevTools endpoint reachable on port {port}. Garbage collection is available.";
            _statusLabel.ForeColor = Good;
            _trimButton.Enabled = !_busy;
            _launchButton.Visible = false;
        }
        else if (ChromeLocator.IsChromeRunning())
        {
            _statusLabel.Text = "Chrome is running without a debugging port. Close it, then start it from here.";
            _statusLabel.ForeColor = Warn;
            _trimButton.Enabled = false;
            _launchButton.Visible = true;
        }
        else
        {
            _statusLabel.Text = "Chrome is not running.";
            _statusLabel.ForeColor = Muted;
            _trimButton.Enabled = false;
            _launchButton.Visible = true;
        }
    }

    private void LaunchChrome()
    {
        if (ChromeLocator.IsChromeRunning())
        {
            MessageBox.Show(
                this,
                "Chrome is already running. Chrome ignores the debugging flag while another instance owns "
                + "the profile, so close every Chrome window first and then press this button again.",
                "Close Chrome first",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        DialogResult confirm = MessageBox.Show(
            this,
            $"Chrome will start with --remote-debugging-port={_settings.DebuggingPort} and restore your last "
            + "session.\n\nWhile that port is open, any program running on this machine can control the "
            + "browser through it. Continue?",
            "Start Chrome with debugging",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.Yes)
        {
            return;
        }

        try
        {
            ChromeLocator.LaunchWithDebugging(_settings.DebuggingPort);
            AppendLog($"Chrome launched with --remote-debugging-port={_settings.DebuggingPort}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not start Chrome", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RunTrimAsync(bool automatic)
    {
        if (_busy)
        {
            return;
        }

        if (_activePort is null)
        {
            await DiscoverPortAsync();

            if (_activePort is null)
            {
                if (!automatic)
                {
                    MessageBox.Show(
                        this,
                        "No DevTools endpoint answered. Start Chrome with the debugging port first.",
                        "Nothing to trim",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                return;
            }
        }

        _busy = true;
        _trimButton.Enabled = false;
        _trimButton.Text = "Working...";

        Progress<string> progress = new(AppendLog);

        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
            TrimResult result = await _trimmer.TrimAsync(_activePort!.Value, progress, cts.Token);

            ReportResult(result);
            RefreshStats();

            if (!result.Succeeded)
            {
                _activePort = null;
                UpdatePortStatus();
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Trim cancelled: it took longer than two minutes.");
        }
        catch (Exception ex)
        {
            AppendLog($"Trim failed: {ex.Message}");
            _activePort = null;
            UpdatePortStatus();
        }
        finally
        {
            _busy = false;
            _trimButton.Text = "Trim now";
            _trimButton.Enabled = _activePort is not null;
        }
    }

    private void ReportResult(TrimResult result)
    {
        if (!result.Succeeded)
        {
            AppendLog($"Error: {result.Error}");
            return;
        }

        AppendLog(
            $"Done in {result.Duration.TotalSeconds:F1}s - {result.TargetsVisited} targets collected, "
            + $"{result.TargetsFailed} skipped.");

        AppendLog(
            $"  Committed  {result.Before.PrivateMb:N0} MB -> {result.After.PrivateMb:N0} MB "
            + $"({result.ReleasedMb:+#,##0;-#,##0;0} MB released)");

        AppendLog(
            $"  Working set {result.Before.WorkingSetMb:N0} MB -> {result.After.WorkingSetMb:N0} MB "
            + $"({result.WorkingSetDropMb:+#,##0;-#,##0;0} MB)");

        if (result.ReleasedMb >= 1)
        {
            _trayIcon.BalloonTipTitle = "Chrome RAM Reducer";
            _trayIcon.BalloonTipText = $"{result.ReleasedMb:N0} MB released back to Windows.";
            _trayIcon.ShowBalloonTip(3000);
        }
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";

        if (_log.TextLength > 60_000)
        {
            _log.Clear();
        }

        _log.AppendText(line);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_reallyClosing && _settings.MinimiseToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _statsTimer.Stop();
        _autoTrimTimer.Stop();
        _trayIcon.Visible = false;
        _settings.Save();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
            _statsTimer.Dispose();
            _autoTrimTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
