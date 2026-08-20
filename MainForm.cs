using System.Diagnostics;
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
    private static readonly Color Bad = Color.FromArgb(197, 34, 31);

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
    private readonly Button _enableButton = new();
    private readonly Button _logFolderButton = new();
    private readonly CheckBox _purgeCheck = new();
    private readonly CheckBox _emptyWorkingSetCheck = new();
    private readonly CheckBox _autoTrimCheck = new();
    private readonly NumericUpDown _autoTrimMinutes = new();
    private readonly NumericUpDown _portInput = new();
    private readonly RichTextBox _log = new();

    private int? _activePort;
    private bool _busy;
    private bool _reallyClosing;

    public MainForm()
    {
        Log("MainForm is being constructed.", LogLevel.Info);

        _settings = AppSettings.Load();
        _trimmer = new ChromeTrimmer(_settings);

        Text = "Chrome RAM Reducer";
        MinimumSize = new Size(660, 660);
        Size = new Size(660, 720);
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

        Logger.Logged += OnLogged;

        _statsTimer.Tick += (_, _) => RefreshStats();
        _autoTrimTimer.Tick += async (_, _) =>
        {
            Log("Automatic trim timer fired.", LogLevel.Info);
            await RunTrimAsync(automatic: true);
        };

        Load += async (_, _) =>
        {
            Log($"Form loaded. Log file: {Logger.LogFileName}", LogLevel.Info);
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
            Log($"Icon could not be extracted from the executable: {ex.Message}", LogLevel.Warning);
            return null;
        }
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        ContextMenuStrip menu = new();

        ToolStripMenuItem open = new("Open", null, (_, _) =>
        {
            Log("Tray menu: Open.", LogLevel.Info);
            RestoreFromTray();
        });

        ToolStripMenuItem trim = new("Trim now", null, async (_, _) =>
        {
            Log("Tray menu: Trim now.", LogLevel.Info);
            await RunTrimAsync(automatic: false);
        });

        ToolStripMenuItem exit = new("Exit", null, (_, _) =>
        {
            Log("Tray menu: Exit.", LogLevel.Info);
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
            WrapContents = false,
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
        _statusLabel.AutoSize = false;
        _statusLabel.Height = 36;
        _statusLabel.Width = 600;
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
            WrapContents = false,
        };

        _trimButton.Text = "Trim now";
        _trimButton.Size = new Size(140, 38);
        _trimButton.FlatStyle = FlatStyle.Flat;
        _trimButton.FlatAppearance.BorderSize = 0;
        _trimButton.BackColor = Accent;
        _trimButton.ForeColor = Color.White;
        _trimButton.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
        _trimButton.Cursor = Cursors.Hand;
        _trimButton.Click += async (_, _) =>
        {
            Log("Button clicked: Trim now.", LogLevel.Info);
            await RunTrimAsync(automatic: false);
        };

        _enableButton.Text = "Enable debugging";
        _enableButton.Size = new Size(170, 38);
        _enableButton.FlatStyle = FlatStyle.Flat;
        _enableButton.Cursor = Cursors.Hand;
        _enableButton.Margin = new Padding(10, 3, 3, 3);
        _enableButton.Click += async (_, _) =>
        {
            Log("Button clicked: Enable debugging.", LogLevel.Info);
            await EnableDebuggingAsync();
        };

        _logFolderButton.Text = "Open logs";
        _logFolderButton.Size = new Size(110, 38);
        _logFolderButton.FlatStyle = FlatStyle.Flat;
        _logFolderButton.Cursor = Cursors.Hand;
        _logFolderButton.Margin = new Padding(10, 3, 3, 3);
        _logFolderButton.Click += (_, _) =>
        {
            Log($"Button clicked: Open logs -> {Logger.LogsFolder}", LogLevel.Info);

            try
            {
                Process.Start(new ProcessStartInfo(Logger.LogsFolder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log($"Log folder could not be opened: {ex}", LogLevel.Error);
            }
        };

        _processCountLabel.Text = string.Empty;
        _processCountLabel.ForeColor = Muted;
        _processCountLabel.AutoSize = true;
        _processCountLabel.Margin = new Padding(12, 12, 0, 0);

        actions.Controls.AddRange([_trimButton, _enableButton, _logFolderButton, _processCountLabel]);

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
            Log($"Setting changed: PurgeJavaScriptMemory = {_purgeCheck.Checked}", LogLevel.Info);
        };

        _emptyWorkingSetCheck.Text = "Also empty working sets (cosmetic - lowers Task Manager only)";
        _emptyWorkingSetCheck.AutoSize = true;
        _emptyWorkingSetCheck.ForeColor = Warn;
        _emptyWorkingSetCheck.CheckedChanged += (_, _) =>
        {
            _settings.EmptyWorkingSets = _emptyWorkingSetCheck.Checked;
            _settings.Save();
            Log($"Setting changed: EmptyWorkingSets = {_emptyWorkingSetCheck.Checked}", LogLevel.Info);
        };

        FlowLayoutPanel autoRow = new()
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0),
            WrapContents = false,
        };

        _autoTrimCheck.Text = "Trim automatically every";
        _autoTrimCheck.AutoSize = true;
        _autoTrimCheck.Margin = new Padding(0, 5, 6, 0);
        _autoTrimCheck.CheckedChanged += (_, _) =>
        {
            _settings.AutoTrimEnabled = _autoTrimCheck.Checked;
            _settings.Save();
            Log($"Setting changed: AutoTrimEnabled = {_autoTrimCheck.Checked}", LogLevel.Info);
            ConfigureAutoTrimTimer();
        };

        _autoTrimMinutes.Minimum = 1;
        _autoTrimMinutes.Maximum = 720;
        _autoTrimMinutes.Width = 64;
        _autoTrimMinutes.ValueChanged += (_, _) =>
        {
            _settings.AutoTrimMinutes = (int)_autoTrimMinutes.Value;
            _settings.Save();
            Log($"Setting changed: AutoTrimMinutes = {_settings.AutoTrimMinutes}", LogLevel.Info);
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
            WrapContents = false,
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
            Log($"Setting changed: DebuggingPort = {_settings.DebuggingPort}", LogLevel.Info);
        };

        Button rediscover = new()
        {
            Text = "Re-detect",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(10, 1, 0, 0),
            Cursor = Cursors.Hand,
        };
        rediscover.Click += async (_, _) =>
        {
            Log("Button clicked: Re-detect.", LogLevel.Info);
            await DiscoverPortAsync();
        };

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
        _log.ReadOnly = true;
        _log.Dock = DockStyle.Fill;
        _log.BackColor = Surface;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.Font = new Font("Consolas", 8.5F);
        _log.WordWrap = true;
        _log.DetectUrls = false;

        return _log;
    }

    private void ApplySettingsToControls()
    {
        _purgeCheck.Checked = _settings.PurgeJavaScriptMemory;
        _emptyWorkingSetCheck.Checked = _settings.EmptyWorkingSets;
        _autoTrimCheck.Checked = _settings.AutoTrimEnabled;
        _autoTrimMinutes.Value = Math.Clamp(_settings.AutoTrimMinutes, 1, 720);
        _portInput.Value = Math.Clamp(_settings.DebuggingPort, 1024, 65535);

        Log($"Settings applied: port={_settings.DebuggingPort}, purge={_settings.PurgeJavaScriptMemory}, "
            + $"emptyWorkingSets={_settings.EmptyWorkingSets}, autoTrim={_settings.AutoTrimEnabled}/"
            + $"{_settings.AutoTrimMinutes}min", LogLevel.Info);
    }

    private void ConfigureAutoTrimTimer()
    {
        _autoTrimTimer.Stop();

        if (_settings.AutoTrimEnabled)
        {
            _autoTrimTimer.Interval = Math.Max(1, _settings.AutoTrimMinutes) * 60_000;
            _autoTrimTimer.Start();
            Log($"Automatic trim armed for every {_settings.AutoTrimMinutes} minutes.", LogLevel.Info);
        }
        else
        {
            Log("Automatic trim disarmed.", LogLevel.Info);
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

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(8));

        try
        {
            _activePort = await ChromeLocator.DiscoverPortAsync(_settings, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log("Port discovery timed out.", LogLevel.Warning);
            _activePort = null;
        }
        catch (Exception ex)
        {
            Log($"Port discovery threw: {ex}", LogLevel.Error);
            _activePort = null;
        }

        UpdatePortStatus();
    }

    private void UpdatePortStatus()
    {
        int chromeProcesses = ChromeLocator.CountChromeProcesses();

        if (_activePort is int port)
        {
            _statusLabel.Text = $"Ready. DevTools endpoint answering on port {port}, {chromeProcesses} Chrome processes.";
            _statusLabel.ForeColor = Good;
            _enableButton.Enabled = false;
        }
        else if (chromeProcesses > 0)
        {
            _statusLabel.Text = "Chrome is running WITHOUT a debugging port, so V8 cannot be reached.\n"
                              + "Press \"Enable debugging\" to restart it with the flag.";
            _statusLabel.ForeColor = Bad;
            _enableButton.Enabled = true;
        }
        else
        {
            _statusLabel.Text = "Chrome is not running. Press \"Enable debugging\" to start it with the flag.";
            _statusLabel.ForeColor = Muted;
            _enableButton.Enabled = true;
        }

        // The trim button stays clickable on purpose: a dead button teaches the user nothing.
        _trimButton.Enabled = !_busy;

        Log($"Status updated. activePort={_activePort?.ToString() ?? "none"}, chromeProcesses={chromeProcesses}",
            LogLevel.Info);
    }

    private async Task EnableDebuggingAsync()
    {
        int running = ChromeLocator.CountChromeProcesses();

        string question = running > 0
            ? $"Chrome is running with {running} processes and ignores the debugging flag while it owns the "
              + "profile.\n\nEvery Chrome window will be asked to close, then Chrome restarts with "
              + $"--remote-debugging-port={_settings.DebuggingPort} and restores the last session. Anything you "
              + "typed into a page but did not submit will be lost.\n\nWhile that port is open, any program on "
              + "this machine can control the browser through it.\n\nContinue?"
            : $"Chrome will start with --remote-debugging-port={_settings.DebuggingPort} and restore your last "
              + "session.\n\nWhile that port is open, any program on this machine can control the browser "
              + "through it.\n\nContinue?";

        DialogResult confirm = MessageBox.Show(
            this, question, "Enable Chrome debugging", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        Log($"Enable debugging confirmation: {confirm}", LogLevel.Info);

        if (confirm != DialogResult.Yes)
        {
            return;
        }

        _enableButton.Enabled = false;

        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));

            if (running > 0)
            {
                _statusLabel.Text = "Waiting for Chrome to close...";
                _statusLabel.ForeColor = Warn;

                bool closed = await ChromeLocator.CloseChromeAsync(TimeSpan.FromSeconds(20), cts.Token);

                if (!closed)
                {
                    MessageBox.Show(
                        this,
                        "Chrome did not close. It may be showing a \"leave site?\" prompt on one of the tabs. "
                        + "Close the remaining windows yourself and press \"Enable debugging\" again.",
                        "Chrome is still running",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);

                    UpdatePortStatus();
                    return;
                }

                await Task.Delay(1500, cts.Token);
            }

            ChromeLocator.LaunchWithDebugging(_settings.DebuggingPort);

            _statusLabel.Text = "Chrome is starting...";
            _statusLabel.ForeColor = Muted;

            // Chrome needs a moment before the endpoint accepts connections.
            for (int attempt = 1; attempt <= 12; attempt++)
            {
                await Task.Delay(1000, cts.Token);

                _activePort = await ChromeLocator.DiscoverPortAsync(_settings, cts.Token);

                if (_activePort is not null)
                {
                    Log($"Endpoint became available after {attempt} attempts.", LogLevel.Info);
                    break;
                }
            }

            UpdatePortStatus();
        }
        catch (Exception ex)
        {
            Log($"Enabling debugging failed: {ex}", LogLevel.Error);

            MessageBox.Show(this, ex.Message, "Could not enable debugging",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

            UpdatePortStatus();
        }
    }

    private async Task RunTrimAsync(bool automatic)
    {
        Log($"RunTrimAsync entered. automatic={automatic}, busy={_busy}, "
            + $"activePort={_activePort?.ToString() ?? "none"}", LogLevel.Info);

        if (_busy)
        {
            Log("A trim is already running; this request was ignored.", LogLevel.Warning);
            return;
        }

        if (_activePort is null)
        {
            Log("No known port; running discovery before giving up.", LogLevel.Info);
            await DiscoverPortAsync();
        }

        if (_activePort is null)
        {
            int running = ChromeLocator.CountChromeProcesses();

            string reason = running > 0
                ? $"Chrome is running with {running} processes, but none of them exposes a DevTools endpoint on "
                  + $"port {_settings.DebuggingPort}.\n\nV8's garbage collector cannot be triggered from outside "
                  + "the browser by any other means, so nothing can be freed until Chrome is restarted with "
                  + "--remote-debugging-port.\n\nPress \"Enable debugging\" to do that now."
                : "Chrome is not running, so there is nothing to trim.";

            Log($"Trim refused: {reason.Replace("\n", " ")}", LogLevel.Warning);

            if (!automatic)
            {
                MessageBox.Show(this, reason, "Nothing to trim", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return;
        }

        _busy = true;
        _trimButton.Enabled = false;
        _trimButton.Text = "Working...";

        Progress<string> progress = new(message => Log(message, LogLevel.Info));

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
            Log("Trim cancelled: it took longer than two minutes.", LogLevel.Error);
        }
        catch (Exception ex)
        {
            Log($"Trim failed: {ex}", LogLevel.Error);
            _activePort = null;
            UpdatePortStatus();
        }
        finally
        {
            _busy = false;
            _trimButton.Text = "Trim now";
            _trimButton.Enabled = true;
        }
    }

    private void ReportResult(TrimResult result)
    {
        if (!result.Succeeded)
        {
            Log($"Trim reported an error: {result.Error}", LogLevel.Error);
            return;
        }

        Log($"Released {result.ReleasedMb:N0} MB of committed memory. "
            + $"Working set moved by {result.WorkingSetDropMb:N0} MB.", LogLevel.Info);

        if (result.ReleasedMb >= 1)
        {
            _trayIcon.BalloonTipTitle = "Chrome RAM Reducer";
            _trayIcon.BalloonTipText = $"{result.ReleasedMb:N0} MB released back to Windows.";
            _trayIcon.ShowBalloonTip(3000);
        }
    }

    private void OnLogged(string message, Logger.LogLevel level)
    {
        if (IsDisposed || _log.IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => AppendToView(message, level));
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                // The window went away while a background thread was logging.
            }

            return;
        }

        AppendToView(message, level);
    }

    private void AppendToView(string message, Logger.LogLevel level)
    {
        if (_log.IsDisposed)
        {
            return;
        }

        if (_log.TextLength > 200_000)
        {
            _log.Clear();
        }

        Color colour = level.Name switch
        {
            nameof(Logger.LogLevel.Error) => Bad,
            nameof(Logger.LogLevel.Warning) => Warn,
            nameof(Logger.LogLevel.Info) => Ink,
            _ => Muted,
        };

        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = colour;
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _log.SelectionColor = _log.ForeColor;
        _log.ScrollToCaret();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    /// <summary>Called when a second launch signals this instance instead of starting its own window.</summary>
    public void ShowFromAnotherInstance()
    {
        Log("Restoring the window on behalf of a second launch.", LogLevel.Info);
        RestoreFromTray();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        Log($"FormClosing. reason={e.CloseReason}, reallyClosing={_reallyClosing}", LogLevel.Info);

        if (!_reallyClosing && _settings.MinimiseToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        Logger.Logged -= OnLogged;
        _statsTimer.Stop();
        _autoTrimTimer.Stop();
        _trayIcon.Visible = false;
        _settings.Save();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Logger.Logged -= OnLogged;
            _trayIcon.Dispose();
            _statsTimer.Dispose();
            _autoTrimTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
