using Backcast.Controls;

namespace Backcast;

/// <summary>
/// Control window for the plugin architecture: shows install state (OBS
/// found, plugin installed, audio endpoint) and offers install/repair and
/// settings. The actual video window lives inside OBS (Tools → Backcast
/// window) — this exe just keeps the plugin healthy.
/// </summary>
internal sealed class MainForm : Form
{
    private const int Edge = 24, RowH = 34;
    private readonly AppSettings _settings;

    private PluginInstaller.ObsInstall? _obs;
    private readonly Label _obsValue = new();
    private readonly Label _pluginValue = new();
    private readonly Label _audioValue = new();
    private readonly DarkButton _installButton = new() { Text = "Install plugin", Size = new Size(150, 36) };
    private readonly DarkButton _settingsButton = new() { Text = "Settings…", Size = new Size(110, 36) };
    private readonly DarkButton _cableButton = new() { Text = "Install VB-Cable", Size = new Size(140, 36) };
    private readonly Label _usage = new();

    private readonly NotifyIcon _tray;

    public MainForm(AppSettings settings)
    {
        _settings = settings;

        Text = "BackCast";
        Icon = LoadEmbeddedIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(600, 330);
        BackColor = Theme.Bg;
        Font = Theme.Font();
        Load += (_, _) => Theme.EnableDarkFrame(Handle);
        FormClosing += OnFormClosing;

        int y = 22;
        y = Section(y, "SETUP");

        y = Row("OBS Studio", _obsValue, y);
        y = Row("BackCast plugin", _pluginValue, y);
        y = Row("Audio output", _audioValue, y);

        _installButton.Click += (_, _) => InstallOrRepair();
        _settingsButton.Click += (_, _) => OpenSettings();
        _cableButton.Click += (_, _) =>
        {
            try
            {
                VbCable.RunInstaller();
                _cableButton.Text = "Installer launched";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "VB-Cable",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        _installButton.Location = new Point(Edge, y + 8);
        _settingsButton.Location = new Point(Edge + 165, y + 8);
        _cableButton.Location = new Point(Edge + 285, y + 8);
        Controls.Add(_installButton);
        Controls.Add(_settingsButton);
        Controls.Add(_cableButton);
        y += 58;

        y = Section(y, "HOW TO USE");
        _usage.ForeColor = Theme.Gray;
        _usage.TextAlign = ContentAlignment.MiddleLeft;
        _usage.Bounds = new Rectangle(Edge, y, ClientSize.Width - Edge * 2, 64);
        _usage.Text = "In OBS:  Tools → BackCast window  (or assign a hotkey in OBS Settings → Hotkeys).\n" +
                      "In Discord:  share the \"BackCast\" window with sound — it carries both video and audio.\n" +
                      "Close the BackCast window in OBS when you're done: it uses no resources while closed.";
        Controls.Add(_usage);

        _tray = new NotifyIcon
        {
            Text = "BackCast",
            Icon = LoadEmbeddedIcon() ?? SystemIcons.Application,
            Visible = true,
        };
        _tray.ContextMenuStrip = BuildTrayMenu();
        _tray.DoubleClick += (_, _) => ShowFromTray();

        Load += (_, _) =>
        {
            if (_settings.FirstRun)
            {
                using var wizard = new FirstRunWizard(_settings);
                wizard.ShowDialog(this);
                _settings.FirstRun = false;
                _settings.Save();
            }
            RefreshState();
        };
    }

    private static Icon? LoadEmbeddedIcon()
    {
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("Backcast.app.ico");
            return stream == null ? null : new Icon(stream);
        }
        catch { return null; }
    }

    // ---- layout helpers (settings-window conventions) ----

    private int Section(int y, string title)
    {
        Label h = new()
        {
            Text = title,
            ForeColor = Theme.Accent,
            Font = Theme.FontBold(),
            AutoSize = true,
            Location = new Point(Edge, y),
        };
        Controls.Add(h);
        y += 26;
        Panel rule = new()
        {
            Size = new Size(ClientSize.Width - Edge * 2, 1),
            BackColor = Theme.Sep,
            Location = new Point(Edge, y),
        };
        Controls.Add(rule);
        return y + 14;
    }

    private int Row(string label, Control value, int y)
    {
        Label lbl = new()
        {
            Text = label,
            ForeColor = Theme.Fg,
            AutoSize = true,
            Location = new Point(Edge, y + 8),
        };
        value.ForeColor = Theme.Gray;
        value.Font = Theme.Font();
        value.AutoSize = true;
        value.Location = new Point(Edge + 170, y + 8);
        value.MaximumSize = new Size(ClientSize.Width - Edge * 2 - 170, 0);
        Controls.Add(lbl);
        Controls.Add(value);
        return y + RowH + 6;
    }

    // ---- state ----

    /// <summary>Re-detects OBS + plugin + endpoint and updates every label.</summary>
    public void RefreshState()
    {
        // remembered root first, then full detection (running OBS wins)
        var installs = PluginInstaller.FindObs();
        _obs = !string.IsNullOrEmpty(_settings.ObsRoot)
            ? installs.FirstOrDefault(i => i.RootPath.Equals(_settings.ObsRoot, StringComparison.OrdinalIgnoreCase))
              ?? (PluginInstaller.LooksLikeObsRoot(_settings.ObsRoot)
                  ? new PluginInstaller.ObsInstall(_settings.ObsRoot, PluginInstaller.IsPortable(_settings.ObsRoot))
                  : null)
            : null;
        _obs ??= installs.FirstOrDefault();

        var endpoints = AudioEndpoints.List();
        string? endpointId = _obs?.IsInstalled == true ? PluginInstaller.GetPluginConfig(_obs, "endpoint_id") : null;

        if (_obs == null)
        {
            _obsValue.Text = "not found — run the setup wizard";
            _obsValue.ForeColor = Theme.Stop;
            _pluginValue.Text = "—";
            _pluginValue.ForeColor = Theme.Gray;
            _audioValue.Text = "—";
            _audioValue.ForeColor = Theme.Gray;
        }
        else
        {
            _settings.ObsRoot = _obs.RootPath;
            _settings.Save();
            _obsValue.Text = _obs.Title;
            _obsValue.ForeColor = Theme.Fg;
            _pluginValue.Text = _obs.IsInstalled ? "installed ✓" : "not installed";
            _pluginValue.ForeColor = _obs.IsInstalled ? Theme.Accent : Theme.Stop;
            if (endpointId == null)
            {
                _audioValue.Text = "auto (chosen on first open)";
                _audioValue.ForeColor = Theme.Gray;
            }
            else
            {
                _audioValue.Text = AudioEndpoints.NameOf(endpoints, endpointId);
                _audioValue.ForeColor = Theme.Fg;
            }
        }

        _installButton.Text = _obs == null ? "Run setup wizard" : _obs.IsInstalled ? "Repair install" : "Install plugin";
        _settingsButton.Enabled = _obs != null;

        _tray.Text = _obs?.IsInstalled == true ? "BackCast — plugin installed" : "BackCast — setup needed";
    }

    private void InstallOrRepair()
    {
        if (_obs == null || !_obs.IsInstalled)
        {
            using var wizard = new FirstRunWizard(_settings);
            wizard.ShowDialog(this);
            RefreshState();
            return;
        }
        try
        {
            PluginInstaller.Install(_obs);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "BackCast",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        RefreshState();
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsForm(_settings);
        dlg.ShowDialog(this);
        RefreshState();
    }

    // ---- tray ----

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = DarkMenu.Create();
        menu.Items.Add(new ToolStripMenuItem("Show window", null, (_, _) => ShowFromTray()));
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => BeginInvoke(OpenSettings)));
        menu.Items.Add(new ToolStripMenuItem("Run setup wizard", null, (_, _) => BeginInvoke(() =>
        {
            using var wizard = new FirstRunWizard(_settings);
            wizard.ShowDialog(this);
            RefreshState();
        })));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Close()));
        return menu;
    }

    private void ShowFromTray()
    {
        Visible = true;
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _tray.Visible = false;
        _tray.Dispose();
    }
}
