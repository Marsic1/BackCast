using Backcast.Controls;

namespace Backcast;

/// <summary>
/// Setup wizard for the plugin architecture: find OBS (standard or
/// portable), download the latest plugin from GitHub and install it into
/// it, then explain how to open the BackCast window in OBS and share it
/// in Discord with sound.
/// </summary>
internal sealed class FirstRunWizard : Form
{
    private const int Edge = 24;

    private readonly AppSettings _settings;
    private readonly DarkButton _nextButton = new() { Text = "Next  →", Size = new Size(118, 34) };
    private readonly DarkButton _backButton = new() { Text = "←  Back", Size = new Size(98, 34) };

    private int _page;
    private List<PluginInstaller.ObsInstall> _installs = new();
    private PluginInstaller.ObsInstall? _chosen;

    // page 1 controls
    private readonly Panel _installList = new();
    private readonly DarkButton _browseButton = new() { Text = "Browse for OBS folder…", Size = new Size(190, 34) };
    private readonly Label _detectNote = new();

    // page 2 controls
    private readonly DarkButton _installButton = new() { Text = "Install the plugin", Size = new Size(190, 36) };
    private readonly Label _installStatus = new();

    public FirstRunWizard(AppSettings settings)
    {
        _settings = settings;

        Text = "BackCast — Setup";
        Icon = TryIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 420);
        BackColor = Theme.Bg;
        Font = Theme.Font();
        ShowInTaskbar = false;
        Load += (_, _) => Theme.EnableDarkFrame(Handle);

        _nextButton.Click += Next;
        _backButton.Click += (_, _) => { _page--; ShowPage(); };
        _browseButton.Click += (_, _) => BrowseForObs();
        _installButton.Click += (_, _) => Install();

        _backButton.Location = new Point(Edge, ClientSize.Height - 48);
        _nextButton.Location = new Point(ClientSize.Width - Edge - 118, ClientSize.Height - 48);
        Controls.Add(_backButton);
        Controls.Add(_nextButton);

        DetectObs();
        ShowPage();
    }

    private static Icon? TryIcon()
    {
        try
        {
            using var stream = typeof(FirstRunWizard).Assembly.GetManifestResourceStream("Backcast.app.ico");
            return stream == null ? null : new Icon(stream);
        }
        catch { return null; }
    }

    // ---- page 1: detect OBS ----

    private void DetectObs()
    {
        _installs = PluginInstaller.FindObs();
        // remembered root wins if still valid
        if (PluginInstaller.LooksLikeObsRoot(_settings.ObsRoot))
        {
            var remembered = new PluginInstaller.ObsInstall(_settings.ObsRoot, PluginInstaller.IsPortable(_settings.ObsRoot));
            _chosen = _installs.FirstOrDefault(i => i.RootPath.Equals(_settings.ObsRoot, StringComparison.OrdinalIgnoreCase))
                      ?? remembered;
        }
        _chosen ??= _installs.FirstOrDefault();
    }

    private void BuildInstallList()
    {
        _installList.Controls.Clear();
        int y = 0;
        foreach (var install in _installs)
        {
            var row = new Panel
            {
                Size = new Size(_installList.Width, 44),
                Location = new Point(0, y),
                BackColor = Theme.BgPanel,
                Cursor = Cursors.Hand,
                Tag = install,
            };
            var title = new Label
            {
                Text = (install.IsPortable ? "Portable OBS" : "OBS Studio") +
                       (install.IsInstalled ? "   —   plugin already installed" : ""),
                ForeColor = install.Equals(_chosen) ? Theme.Accent : Theme.Fg,
                Font = Theme.FontBold(),
                AutoSize = true,
                Location = new Point(14, 5),
            };
            var path = new Label
            {
                Text = install.RootPath,
                ForeColor = Theme.Gray,
                AutoSize = true,
                Location = new Point(14, 24),
            };
            row.Controls.Add(title);
            row.Controls.Add(path);
            row.Click += (_, _) => { _chosen = install; RefreshListSelection(); UpdateNav(); };
            foreach (Control c in row.Controls)
                c.Click += (_, _) => { _chosen = install; RefreshListSelection(); UpdateNav(); };
            _installList.Controls.Add(row);
            y += 52;
        }
        RefreshListSelection();
    }

    private void RefreshListSelection()
    {
        foreach (Panel row in _installList.Controls.Cast<Control>().OfType<Panel>())
        {
            bool selected = row.Tag is PluginInstaller.ObsInstall ins && ins.Equals(_chosen);
            row.BackColor = selected ? Theme.Hover : Theme.BgPanel;
        }
    }

    private void BrowseForObs()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select your OBS folder (contains bin and obs-plugins)" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (!PluginInstaller.LooksLikeObsRoot(dlg.SelectedPath))
        {
            MessageBox.Show(this, "That folder doesn't contain bin\\64bit\\obs64.exe.",
                "BackCast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _chosen = new PluginInstaller.ObsInstall(dlg.SelectedPath, PluginInstaller.IsPortable(dlg.SelectedPath));
        UpdateNav();
    }

    // ---- page 2: install ----

    private async void Install()
    {
        if (_chosen == null) return;
        _installButton.Enabled = false;
        _installStatus.Text = "downloading the latest plugin from GitHub…";
        _installStatus.ForeColor = Theme.Amber;
        try
        {
            await PluginInstaller.InstallAsync(_chosen);
            _settings.ObsRoot = _chosen.RootPath;
            _installStatus.Text = "installed ✓ — restart OBS if it's running";
            _installStatus.ForeColor = Theme.Accent;
            _nextButton.Enabled = true;
        }
        catch (Exception ex)
        {
            _installStatus.Text = ex.Message;
            _installStatus.ForeColor = Theme.Stop;
            _installButton.Enabled = true;
        }
    }

    // ---- navigation ----

    private void Next(object? sender, EventArgs e)
    {
        if (_page == 2) { Close(); return; }
        if (_page == 0 && _chosen != null)
            _settings.ObsRoot = _chosen.RootPath;
        _page++;
        ShowPage();
    }

    private void ShowPage()
    {
        // clear page body (keep nav buttons)
        foreach (Control c in Controls.Cast<Control>().Where(c => c != _nextButton && c != _backButton).ToList())
        {
            Controls.Remove(c);
            if (c != _installList && c != _detectNote && c != _installButton && c != _installStatus && c != _browseButton)
                c.Dispose();
        }

        if (_page == 0) BuildPageDetect();
        else if (_page == 1) BuildPageInstall();
        else BuildPageDone();

        _backButton.Visible = _page > 0;
        UpdateNav();
    }

    private void UpdateNav()
    {
        _nextButton.Enabled = _page switch
        {
            0 => _chosen != null,
            1 => _chosen?.IsInstalled == true,
            _ => true, // done page: Finish is always available
        };
        _nextButton.Text = _page == 2 ? "Finish" : "Next  →";
    }

    // ---- pages ----

    private void PageTitle(string title, string sub)
    {
        Label h = new()
        {
            Text = title,
            Font = new Font(Theme.FontBold().FontFamily, 13f, FontStyle.Bold),
            ForeColor = Theme.Fg,
            AutoSize = true,
            Location = new Point(Edge, 24),
        };
        Label s = new()
        {
            Text = sub,
            ForeColor = Theme.Gray,
            AutoSize = true,
            Location = new Point(Edge, 54),
        };
        Controls.Add(h);
        Controls.Add(s);
    }

    private void BuildPageDetect()
    {
        PageTitle("Find OBS Studio", "BackCast installs a plugin into your OBS folder.");
        _installList.SetBounds(Edge, 96, ClientSize.Width - Edge * 2, 180);
        _installList.AutoScroll = true;
        _installList.BackColor = Theme.Bg;
        BuildInstallList();
        Controls.Add(_installList);

        _browseButton.Location = new Point(Edge, 290);
        Controls.Add(_browseButton);

        _detectNote.ForeColor = Theme.Gray;
        _detectNote.AutoSize = true;
        _detectNote.Text = _installs.Count == 0
            ? "No OBS found — start OBS once, or browse for its folder."
            : "Pick the installation to use. Running instances are found automatically.";
        _detectNote.Location = new Point(Edge, 335);
        Controls.Add(_detectNote);
    }

    private void BuildPageInstall()
    {
        PageTitle("Install the plugin",
            _chosen == null ? "" : $"Into: {_chosen.Title}");
        _installButton.Location = new Point(Edge, 110);
        _installButton.Enabled = _chosen?.IsInstalled != true;
        Controls.Add(_installButton);

        _installStatus.AutoSize = true;
        _installStatus.Location = new Point(Edge, 160);
        _installStatus.Text = _chosen?.IsInstalled == true
            ? "already installed ✓ — you can continue"
            : "downloaded from GitHub into obs-plugins\\64bit; no admin needed for portable installs";
        _installStatus.ForeColor = _chosen?.IsInstalled == true ? Theme.Accent : Theme.Gray;
        Controls.Add(_installStatus);

        Label note = new()
        {
            Text = "If OBS is running, close and reopen it after installing.",
            ForeColor = Theme.Gray,
            AutoSize = true,
            Location = new Point(Edge, 200),
        };
        Controls.Add(note);
    }

    private void BuildPageDone()
    {
        PageTitle("You're all set", "One window in OBS carries video + audio for Discord.");

        string text =
            "1.  Open OBS and press the  BackCast  button in its menu bar\n" +
            "     (or set a hotkey in OBS Settings → Hotkeys → \"BackCast: toggle window\").\n\n" +
            "2.  In Discord, share the \"BackCast\" window and enable sound.\n" +
            "     Video is a frame behind your scene; audio is the exact master mix.\n\n" +
            "3.  The plugin plays the mix to an unused audio device so you don't\n" +
            "     hear it twice — check or change it in BackCast settings.\n\n" +
            "Important:  keep audio monitoring OFF on your OBS sources — anything\n" +
            "OBS plays itself is picked up by the Discord share too.";

        Label body = new()
        {
            Text = text,
            ForeColor = Theme.Fg,
            Bounds = new Rectangle(Edge, 96, ClientSize.Width - Edge * 2, 240),
        };
        Controls.Add(body);
    }
}
