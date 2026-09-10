using Backcast.Controls;

namespace Backcast;

/// <summary>
/// Settings for the plugin architecture: OBS install (repair/uninstall),
/// audio endpoint (written to the plugin's config), and pointers to the
/// hotkeys that now live in OBS itself.
/// </summary>
internal sealed class SettingsForm : Form
{
    private const int RowH = 34, Edge = 24, LabelX = 24, InputX = 190, InputW = 440;

    private readonly AppSettings _settings;
    private PluginInstaller.ObsInstall? _obs;

    private readonly Label _obsValue = new();
    private readonly DarkButton _repairButton = new() { Text = "Repair install", Size = new Size(130, 32) };
    private readonly DarkButton _uninstallButton = new() { Text = "Uninstall", Size = new Size(100, 32) };

    private readonly DarkCombo _deviceList = new();
    private readonly DarkButton _refreshDevices = new() { Text = "Refresh", Size = new Size(90, 32) };
    private readonly DarkButton _autoButton = new() { Text = "Auto", Size = new Size(70, 32) };
    private readonly DarkButton _installCable = new() { Text = "Install VB-Cable", Size = new Size(150, 32) };

    private readonly DarkInput _windowTitle = new();

    private List<AudioEndpoints.Endpoint> _endpoints = new();

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;

        Text = "BackCast — Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(860, 660);
        BackColor = Theme.Bg;
        Font = Theme.Font();
        Load += (_, _) => Theme.EnableDarkFrame(Handle);

        DetectObs();

        int y = 22;
        y = Section(y, "OBS");
        _obsValue.ForeColor = Theme.Fg;
        _obsValue.AutoSize = true;
        _obsValue.Location = new Point(LabelX, y + 8);
        _obsValue.MaximumSize = new Size(InputX + InputW - LabelX, 0);
        Controls.Add(_obsValue);
        y += RowH + 4;
        _repairButton.Click += (_, _) => Repair();
        _uninstallButton.Click += (_, _) => Uninstall();
        _repairButton.Location = new Point(InputX, y);
        _uninstallButton.Location = new Point(InputX + 140, y);
        Controls.Add(_repairButton);
        Controls.Add(_uninstallButton);
        y += RowH + 10;

        y = Section(y, "AUDIO");
        y = StackNote(y, "The plugin plays the OBS master mix to this device. Any device works for Discord — pick one you don't listen to, or you'll hear everything twice.");
        RowLabel(ref y, "Output device");
        Place(_deviceList, y - RowH - 10);
        _refreshDevices.Click += (_, _) => RefreshDevices();
        _autoButton.Click += (_, _) =>
        {
            PluginInstaller.SetPluginConfig(_obs!, "endpoint_id", "");
            RefreshDevices();
        };
        _refreshDevices.Location = new Point(InputX + InputW + 10, y - RowH - 10);
        _autoButton.Location = new Point(InputX + InputW + 105, y - RowH - 10);
        Controls.Add(_refreshDevices);
        Controls.Add(_autoButton);
        _deviceList.SelectedIndexChanged += (_, _) => SaveEndpoint();
        y += 4;

        _installCable.Click += (_, _) =>
        {
            try
            {
                VbCable.RunInstaller();
                _installCable.Text = "Installer launched — then Refresh";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "VB-Cable",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        _installCable.Location = new Point(InputX, y + 4);
        Controls.Add(_installCable);
        y += RowH + 6;

        y = Section(y, "WINDOW");
        RowLabel(ref y, "Window title");
        Place(_windowTitle, y - RowH - 10);
        _windowTitle.Text = _obs?.IsInstalled == true
            ? PluginInstaller.GetPluginConfig(_obs, "title") ?? "BackCast"
            : "BackCast";
        y = StackNote(y, "Shown in the window header, taskbar and Discord's share picker. Applies the next time the window opens.");

        y = Section(y, "HOTKEYS");
        y = StackNote(y, "Hotkeys are managed by OBS: Settings → Hotkeys → \"BackCast: toggle window\" and \"BackCast: toggle always on top\".");

        y += 6;
        var ok = new DarkButton { Text = "OK", Size = new Size(104, 34), Location = new Point(ClientSize.Width - Edge - 104, y) };
        ok.Click += (_, _) => { SaveAndClose(); };
        Controls.Add(ok);

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
        };

        RefreshDevices();
        UpdateObsRow();
    }

    private void DetectObs()
    {
        var installs = PluginInstaller.FindObs();
        _obs = !string.IsNullOrEmpty(_settings.ObsRoot)
            ? installs.FirstOrDefault(i => i.RootPath.Equals(_settings.ObsRoot, StringComparison.OrdinalIgnoreCase))
              ?? (PluginInstaller.LooksLikeObsRoot(_settings.ObsRoot)
                  ? new PluginInstaller.ObsInstall(_settings.ObsRoot, PluginInstaller.IsPortable(_settings.ObsRoot))
                  : null)
            : null;
        _obs ??= installs.FirstOrDefault();
    }

    private void UpdateObsRow()
    {
        if (_obs == null)
        {
            _obsValue.Text = "OBS not found — run the setup wizard";
            _obsValue.ForeColor = Theme.Stop;
            _repairButton.Enabled = _uninstallButton.Enabled = false;
        }
        else
        {
            _obsValue.Text = _obs.Title + (_obs.IsInstalled ? "  —  plugin installed ✓" : "  —  plugin missing");
            _obsValue.ForeColor = _obs.IsInstalled ? Theme.Accent : Theme.Stop;
            _repairButton.Enabled = _uninstallButton.Enabled = true;
        }
    }

    private void Repair()
    {
        if (_obs == null) return;
        try
        {
            PluginInstaller.Install(_obs);
            MessageBox.Show(this, "Plugin reinstalled. Restart OBS if it's running.",
                "BackCast", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "BackCast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateObsRow();
    }

    private void Uninstall()
    {
        if (_obs == null) return;
        if (MessageBox.Show(this, "Remove the Backcast plugin from OBS?",
                "BackCast", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        try
        {
            PluginInstaller.Uninstall(_obs);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "BackCast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateObsRow();
    }

    private void RefreshDevices()
    {
        _endpoints = AudioEndpoints.List();
        _deviceList.Items.Clear();
        _deviceList.Items.Add("(auto — plugin picks an unused device)");
        foreach (var ep in _endpoints)
            _deviceList.Items.Add(ep.Name + (ep.IsDefaultConsole ? "   (default)" : ""));
        _deviceList.SelectedIndex = 0;

        string? current = _obs?.IsInstalled == true ? PluginInstaller.GetPluginConfig(_obs, "endpoint_id") : null;
        if (!string.IsNullOrEmpty(current))
        {
            int idx = _endpoints.FindIndex(e => e.Id == current);
            if (idx >= 0) _deviceList.SelectedIndex = idx + 1;
        }
        _deviceList.Enabled = _obs?.IsInstalled == true;
    }

    private void SaveEndpoint()
    {
        if (_obs == null || _deviceList.SelectedIndex < 0) return;
        // re-entrancy guard: SelectedIndexChanged fires while rebuilding
        if (!_deviceList.Enabled && _deviceList.SelectedIndex == 0) return;
        if (_deviceList.SelectedIndex == 0)
            PluginInstaller.SetPluginConfig(_obs, "endpoint_id", "");
        else if (_deviceList.SelectedIndex - 1 < _endpoints.Count)
            PluginInstaller.SetPluginConfig(_obs, "endpoint_id", _endpoints[_deviceList.SelectedIndex - 1].Id);
    }

    private void SaveAndClose()
    {
        if (_obs != null)
        {
            string title = string.IsNullOrWhiteSpace(_windowTitle.Text) ? "BackCast" : _windowTitle.Text.Trim();
            PluginInstaller.SetPluginConfig(_obs, "title", title);
        }
        Close();
    }

    // ---- layout helpers ----

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
        return y + 12;
    }

    private int StackNote(int y, string text)
    {
        var size = TextRenderer.MeasureText(text, Theme.Font(),
            new Size(ClientSize.Width - Edge * 2 - LabelX, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
        Label lbl = new()
        {
            Text = text,
            ForeColor = Theme.Gray,
            Bounds = new Rectangle(LabelX, y, ClientSize.Width - Edge * 2 - LabelX, size.Height),
            AutoSize = false,
        };
        Controls.Add(lbl);
        return y + size.Height + 12;
    }

    private void RowLabel(ref int y, string text)
    {
        Label lbl = new()
        {
            Text = text,
            ForeColor = Theme.Fg,
            AutoSize = true,
            Location = new Point(LabelX, y + 8),
        };
        Controls.Add(lbl);
        y += RowH + 10;
    }

    private void Place(Control input, int y, int? width = null)
    {
        input.Location = new Point(InputX, y);
        input.Size = new Size(width ?? InputW, RowH);
        Controls.Add(input);
    }
}
