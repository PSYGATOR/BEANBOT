using System.Net;
using System.Runtime.InteropServices;
using BeanBot.Accounts;
using BeanBot.Commands;
using BeanBot.Host;
using BeanBot.Matchmaker;
using BeanBot.Observability;
using BeanBot.Orchestrator;
using BeanBot.Protocol;
using BeanBot.Transport;
using Microsoft.Extensions.Logging;

namespace BeanBot.Gui;

public sealed class MainForm : Form
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    private static readonly Color BgWindow = Color.FromArgb(18, 18, 22);
    private static readonly Color BgPanel = Color.FromArgb(27, 27, 34);
    private static readonly Color BgInput = Color.FromArgb(36, 36, 45);
    private static readonly Color BgHover = Color.FromArgb(50, 50, 62);
    private static readonly Color Accent = Color.FromArgb(88, 101, 242);
    private static readonly Color Danger = Color.FromArgb(185, 62, 62);
    private static readonly Color BorderColor = Color.FromArgb(55, 55, 68);
    private static readonly Color TextPrimary = Color.FromArgb(236, 236, 242);
    private static readonly Color TextMuted = Color.FromArgb(160, 162, 176);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly ComboBox _modeBox = new();
    private readonly TextBox _roomBox = new();
    private readonly NumericUpDown _botsNum = new();
    private readonly ComboBox _regionBox = new();
    private readonly ComboBox _profileBox = new();
    private readonly TextBox _matchmakerBox = new();
    private readonly TextBox _vaultKeyBox = new();
    private readonly CheckBox _publicCheck = new();
    private readonly Button _connectBtn = new();
    private readonly Button _disconnectBtn = new();
    private readonly Label _statusLabel = new();

    private readonly ListView _playersList = new();
    private readonly ListView _botsList = new();
    private readonly ComboBox _botTargetBox = new();
    private readonly CheckBox _killHoldCheck = new();
    private readonly Button _followBtn = new();
    private readonly Button _stopFollowBtn = new();
    private readonly Button _voteBtn = new();
    private readonly Button _skipVoteBtn = new();
    private readonly Button _killBtn = new();
    private readonly Button _tasksOnBtn = new();
    private readonly Button _tasksOffBtn = new();
    private readonly TextBox _sayBox = new();
    private readonly Button _sayBtn = new();
    private readonly TextBox _cmdBox = new();
    private readonly Button _cmdBtn = new();
    private readonly TextBox _logBox = new();

    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 400 };
    private CancellationTokenSource? _sessionCts;
    private HttpClient? _http;
    private Fleet? _fleet;
    private BotClient? _singleBot;
    private CommandSurface? _surface;
    private HttpControlServer? _controlServer;
    private int? _selectedPlayerClientId;

    public MainForm()
    {
        Text = "BeanBot";
        MinimumSize = new Size(920, 620);
        Size = new Size(1060, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgWindow;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9.5f);

        BuildLayout();
        ApplyDarkTheme(this);
        WireEvents();

        _refreshTimer.Tick += (_, _) => RefreshLiveLists();
        _refreshTimer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = 1;
        try
        {
            _ = DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch
        {
        }
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10),
            BackColor = BgWindow,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 62f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 38f));

        // 1. Session controls panel
        var sessionPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            BackColor = BgPanel,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 8),
        };

        _modeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeBox.Items.AddRange(["Join Room", "Self-Host"]);
        _modeBox.SelectedIndex = 0;
        _modeBox.Width = 105;

        _roomBox.Width = 90;
        _roomBox.Text = "auto";
        _roomBox.CharacterCasing = CharacterCasing.Normal;

        _botsNum.Minimum = 1;
        _botsNum.Maximum = Roster.MaxTotalSeats;
        _botsNum.Value = 5;
        _botsNum.Width = 54;

        _regionBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _regionBox.Items.AddRange(["na", "eu", "as"]);
        _regionBox.SelectedIndex = 0;
        _regionBox.Width = 58;

        _profileBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _profileBox.Items.AddRange(["live", "private", "local"]);
        _profileBox.SelectedIndex = 0;
        _profileBox.Width = 74;

        _matchmakerBox.Width = 140;
        _matchmakerBox.PlaceholderText = "http://127.0.0.1:22023";
        _matchmakerBox.Visible = false;

        _vaultKeyBox.Width = 120;
        _vaultKeyBox.UseSystemPasswordChar = true;
        _vaultKeyBox.Text = Environment.GetEnvironmentVariable(Vault.VaultKeyEnvVar) ?? string.Empty;
        _vaultKeyBox.PlaceholderText = "Vault key";

        _publicCheck.Text = "Public";
        _publicCheck.AutoSize = true;
        _publicCheck.Margin = new Padding(6, 6, 6, 0);
        _publicCheck.Visible = false;

        _connectBtn.Text = "Connect";
        _connectBtn.Width = 92;
        _connectBtn.Height = 28;
        _connectBtn.BackColor = Accent;

        _disconnectBtn.Text = "Disconnect";
        _disconnectBtn.Width = 92;
        _disconnectBtn.Height = 28;
        _disconnectBtn.Enabled = false;

        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = TextMuted;
        _statusLabel.Margin = new Padding(8, 6, 4, 0);
        _statusLabel.Text = "Disconnected";

        sessionPanel.Controls.Add(MakeField("Mode", _modeBox));
        sessionPanel.Controls.Add(MakeField("Code / auto", _roomBox));
        sessionPanel.Controls.Add(MakeField("Bots", _botsNum));
        sessionPanel.Controls.Add(MakeField("Region", _regionBox));
        sessionPanel.Controls.Add(MakeField("Profile", _profileBox));
        sessionPanel.Controls.Add(MakeField("Matchmaker", _matchmakerBox));
        sessionPanel.Controls.Add(MakeField("Vault Key", _vaultKeyBox));
        sessionPanel.Controls.Add(_publicCheck);
        sessionPanel.Controls.Add(_connectBtn);
        sessionPanel.Controls.Add(_disconnectBtn);
        sessionPanel.Controls.Add(_statusLabel);

        // 2. Center split: Players list (left) + Bots list (right)
        var centerSplit = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BgWindow,
            Margin = new Padding(0, 0, 0, 8),
        };
        centerSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52f));
        centerSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48f));

        var playersGroup = MakeSectionPanel("Players in Room (click to select)");
        ConfigureListView(_playersList);
        _playersList.Columns.Add("Player", 170);
        _playersList.Columns.Add("Color", 85);
        _playersList.Columns.Add("Role / State", 130);
        _playersList.Columns.Add("Type", 70);
        playersGroup.Controls.Add(_playersList, 0, 1);

        var botsGroup = MakeSectionPanel("Bots");
        ConfigureListView(_botsList);
        _botsList.Columns.Add("Bot", 110);
        _botsList.Columns.Add("Account", 72);
        _botsList.Columns.Add("State", 90);
        _botsList.Columns.Add("Following", 130);
        botsGroup.Controls.Add(_botsList, 0, 1);

        centerSplit.Controls.Add(playersGroup, 0, 0);
        centerSplit.Controls.Add(botsGroup, 1, 0);

        // 3. Action bar
        var actionsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = BgPanel,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 8),
        };

        var playerActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            BackColor = BgPanel,
        };

        _botTargetBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _botTargetBox.Items.Add("all");
        _botTargetBox.SelectedIndex = 0;
        _botTargetBox.Width = 110;

        _followBtn.Text = "Follow Selected";
        _followBtn.AutoSize = true;
        _followBtn.BackColor = Accent;

        _killHoldCheck.Text = "Kill on reach";
        _killHoldCheck.AutoSize = true;
        _killHoldCheck.Margin = new Padding(4, 6, 8, 0);

        _stopFollowBtn.Text = "Stop Follow";
        _stopFollowBtn.AutoSize = true;

        _voteBtn.Text = "Vote Selected";
        _voteBtn.AutoSize = true;

        _skipVoteBtn.Text = "Skip Vote";
        _skipVoteBtn.AutoSize = true;

        _killBtn.Text = "Kill Selected";
        _killBtn.AutoSize = true;
        _killBtn.BackColor = Danger;

        _tasksOnBtn.Text = "Tasks On";
        _tasksOnBtn.AutoSize = true;

        _tasksOffBtn.Text = "Tasks Off";
        _tasksOffBtn.AutoSize = true;

        playerActions.Controls.Add(MakeInlineLabel("Target Bot:"));
        playerActions.Controls.Add(_botTargetBox);
        playerActions.Controls.Add(_followBtn);
        playerActions.Controls.Add(_killHoldCheck);
        playerActions.Controls.Add(_stopFollowBtn);
        playerActions.Controls.Add(_voteBtn);
        playerActions.Controls.Add(_skipVoteBtn);
        playerActions.Controls.Add(_killBtn);
        playerActions.Controls.Add(_tasksOnBtn);
        playerActions.Controls.Add(_tasksOffBtn);

        var chatRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            BackColor = BgPanel,
            Margin = new Padding(0, 6, 0, 0),
        };

        _sayBox.Width = 320;
        _sayBox.PlaceholderText = "Chat message (Quick Chat / Free Chat)";
        _sayBtn.Text = "Say";
        _sayBtn.Width = 72;

        _cmdBox.Width = 300;
        _cmdBox.PlaceholderText = "Command (e.g. vent 0, follow all Player, status)";
        _cmdBtn.Text = "Run";
        _cmdBtn.Width = 72;

        chatRow.Controls.Add(MakeInlineLabel("Chat:"));
        chatRow.Controls.Add(_sayBox);
        chatRow.Controls.Add(_sayBtn);
        chatRow.Controls.Add(MakeInlineLabel("Cmd:"));
        chatRow.Controls.Add(_cmdBox);
        chatRow.Controls.Add(_cmdBtn);

        actionsPanel.Controls.Add(playerActions, 0, 0);
        actionsPanel.Controls.Add(chatRow, 0, 1);

        // 4. Log output
        var logPanel = MakeSectionPanel("Log");
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Dock = DockStyle.Fill;
        _logBox.BorderStyle = BorderStyle.FixedSingle;
        _logBox.Font = new Font("Consolas", 9f);
        logPanel.Controls.Add(_logBox, 0, 1);

        root.Controls.Add(sessionPanel, 0, 0);
        root.Controls.Add(centerSplit, 0, 1);
        root.Controls.Add(actionsPanel, 0, 2);
        root.Controls.Add(logPanel, 0, 3);

        Controls.Add(root);
        SetSessionControlsEnabled(connected: false, busy: false);
    }

    private static Control MakeField(string caption, Control input)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, 0, 8, 2),
        };
        var label = new Label
        {
            Text = caption,
            AutoSize = true,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 8f),
            Margin = new Padding(0, 0, 0, 2),
        };
        panel.Controls.Add(label);
        panel.Controls.Add(input);
        return panel;
    }

    private static Label MakeInlineLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = TextMuted,
        Margin = new Padding(2, 6, 4, 0),
    };

    private static TableLayoutPanel MakeSectionPanel(string title)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = BgPanel,
            Padding = new Padding(6),
            Margin = new Padding(2),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        panel.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4),
        }, 0, 0);
        return panel;
    }

    private static void ConfigureListView(ListView listView)
    {
        listView.Dock = DockStyle.Fill;
        listView.View = View.Details;
        listView.FullRowSelect = true;
        listView.MultiSelect = false;
        listView.HideSelection = false;
        listView.BorderStyle = BorderStyle.FixedSingle;
        listView.HeaderStyle = ColumnHeaderStyle.Nonclickable;
    }

    private void ApplyDarkTheme(Control control)
    {
        foreach (Control child in control.Controls)
        {
            switch (child)
            {
                case Button btn:
                    btn.FlatStyle = FlatStyle.Flat;
                    btn.FlatAppearance.BorderColor = BorderColor;
                    btn.FlatAppearance.MouseOverBackColor = BgHover;
                    btn.ForeColor = TextPrimary;
                    if (btn.BackColor != Accent && btn.BackColor != Danger)
                    {
                        btn.BackColor = BgInput;
                    }
                    break;
                case TextBox tb:
                    tb.BackColor = BgInput;
                    tb.ForeColor = TextPrimary;
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ComboBox cb:
                    cb.BackColor = BgInput;
                    cb.ForeColor = TextPrimary;
                    cb.FlatStyle = FlatStyle.Flat;
                    break;
                case NumericUpDown nud:
                    nud.BackColor = BgInput;
                    nud.ForeColor = TextPrimary;
                    break;
                case ListView lv:
                    lv.BackColor = BgWindow;
                    lv.ForeColor = TextPrimary;
                    break;
                case CheckBox chk:
                    chk.ForeColor = TextPrimary;
                    break;
            }

            if (child.HasChildren)
            {
                ApplyDarkTheme(child);
            }
        }
    }

    private void WireEvents()
    {
        _modeBox.SelectedIndexChanged += (_, _) =>
        {
            var isHost = _modeBox.SelectedIndex == 1;
            _roomBox.Enabled = !isHost;
            _publicCheck.Visible = isHost;
            if (isHost)
            {
                _botsNum.Minimum = 0;
                _botsNum.Maximum = Roster.MaxJoiners;
                if (_botsNum.Value > Roster.MaxJoiners) _botsNum.Value = Roster.MaxJoiners;
            }
            else
            {
                _botsNum.Minimum = 1;
                _botsNum.Maximum = Roster.MaxTotalSeats;
                if (_botsNum.Value < 1) _botsNum.Value = 1;
            }
        };

        _profileBox.SelectedIndexChanged += (_, _) =>
        {
            var mode = _profileBox.SelectedItem?.ToString();
            _matchmakerBox.Visible = string.Equals(mode, "private", StringComparison.OrdinalIgnoreCase);
            _regionBox.Enabled = string.Equals(mode, "live", StringComparison.OrdinalIgnoreCase);
        };

        _playersList.SelectedIndexChanged += (_, _) =>
        {
            if (_playersList.SelectedItems.Count > 0 && _playersList.SelectedItems[0].Tag is PlayerListEntry p)
            {
                _selectedPlayerClientId = p.ClientId;
            }
        };

        _botsList.SelectedIndexChanged += (_, _) =>
        {
            if (_botsList.SelectedItems.Count > 0 && _botsList.SelectedItems[0].Tag is BotStatusSnapshot b)
            {
                var idx = _botTargetBox.Items.IndexOf(b.AccountId);
                if (idx >= 0) _botTargetBox.SelectedIndex = idx;
            }
        };

        _connectBtn.Click += async (_, _) => await StartSessionAsync();
        _disconnectBtn.Click += async (_, _) => await DisconnectSessionAsync();

        _followBtn.Click += async (_, _) =>
        {
            var player = GetSelectedPlayer();
            if (player is null) { AppendLog("Select a player in the Players list first."); return; }
            var target = SelectedBotTarget();
            var suffix = _killHoldCheck.Checked ? " kill" : string.Empty;
            await ExecuteCommandAsync($"follow {target} \"{player.Name}\"{suffix}");
        };

        _stopFollowBtn.Click += async (_, _) =>
            await ExecuteCommandAsync($"follow {SelectedBotTarget()} off");

        _voteBtn.Click += async (_, _) =>
        {
            var player = GetSelectedPlayer();
            if (player is null) { AppendLog("Select a player in the Players list first."); return; }
            await ExecuteCommandAsync($"vote {SelectedBotTarget()} \"{player.Name}\"");
        };

        _skipVoteBtn.Click += async (_, _) =>
            await ExecuteCommandAsync($"vote {SelectedBotTarget()} skip");

        _killBtn.Click += async (_, _) =>
        {
            var player = GetSelectedPlayer();
            if (player is null) { AppendLog("Select a player in the Players list first."); return; }
            var botId = SelectedSingleBotId();
            if (botId is null) { AppendLog("Select a bot from Target Bot or the Bots list to kill."); return; }
            await ExecuteCommandAsync($"kill {botId} \"{player.Name}\"");
        };

        _tasksOnBtn.Click += async (_, _) =>
            await ExecuteCommandAsync($"tasks {SelectedBotTarget()} on");

        _tasksOffBtn.Click += async (_, _) =>
            await ExecuteCommandAsync($"tasks {SelectedBotTarget()} off");

        _sayBtn.Click += async (_, _) => await SendSayAsync();
        _sayBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await SendSayAsync();
            }
        };

        _cmdBtn.Click += async (_, _) => await SendCustomCommandAsync();
        _cmdBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await SendCustomCommandAsync();
            }
        };
    }

    private PlayerListEntry? GetSelectedPlayer()
    {
        if (_playersList.SelectedItems.Count > 0 && _playersList.SelectedItems[0].Tag is PlayerListEntry p)
        {
            return p;
        }

        return null;
    }

    private string SelectedBotTarget()
    {
        if (_singleBot is not null)
        {
            return string.Empty;
        }

        return _botTargetBox.SelectedItem?.ToString() ?? "all";
    }

    private string? SelectedSingleBotId()
    {
        if (_singleBot is not null)
        {
            return string.Empty;
        }

        var selected = _botTargetBox.SelectedItem?.ToString();
        if (!string.IsNullOrWhiteSpace(selected) && !selected.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return selected;
        }

        return _surface?.GetBotsSnapshot().FirstOrDefault(b => b.IsConnected)?.AccountId;
    }

    private static string ResolveAppDataRoot()
    {
        var candidates = new List<string>();
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(exeDir))
        {
            candidates.Add(exeDir);
        }

        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var cwd = Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(cwd) &&
            (string.IsNullOrWhiteSpace(winDir) || !cwd.StartsWith(winDir, StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(cwd);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "BeanBot"));
        }

        // Prefer a candidate that already has data/accounts.json.age if writable
        foreach (var dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(dir, Vault.DefaultVaultPath)) && IsDirectoryWritable(dir))
            {
                return dir;
            }
        }

        foreach (var dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsDirectoryWritable(dir))
            {
                return dir;
            }
        }

        return exeDir ?? AppContext.BaseDirectory;
    }

    private static bool IsDirectoryWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "data"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task StartSessionAsync()
    {
        var appRoot = ResolveAppDataRoot();
        try
        {
            Environment.CurrentDirectory = appRoot;
        }
        catch
        {
        }

        var vaultPath = Path.Combine(appRoot, Vault.DefaultVaultPath);
        var replaysDir = Path.Combine(appRoot, "data", "replays");
        var locksDir = Path.Combine(appRoot, "data", "locks");
        var certsDir = Path.Combine(appRoot, "certs");

        var vaultKey = _vaultKeyBox.Text;
        if (string.IsNullOrWhiteSpace(vaultKey))
        {
            AppendLog($"Enter a Vault Key (or set {Vault.VaultKeyEnvVar}).");
            return;
        }

        var isHost = _modeBox.SelectedIndex == 1;
        var roomCode = _roomBox.Text.Trim();
        var botCount = (int)_botsNum.Value;
        var region = _regionBox.SelectedItem?.ToString() ?? "na";
        var profileMode = _profileBox.SelectedItem?.ToString() ?? "live";

        ServerProfile profile;
        try
        {
            profile = profileMode.ToLowerInvariant() switch
            {
                "local" => ServerProfile.Local(),
                "private" => ServerProfile.Private(string.IsNullOrWhiteSpace(_matchmakerBox.Text) ? "http://127.0.0.1:22023" : _matchmakerBox.Text.Trim(), certsDirectory: certsDir),
                _ => ServerProfile.Live(region, certsDirectory: certsDir),
            };
        }
        catch (Exception ex)
        {
            AppendLog($"Profile error: {ex.Message}");
            return;
        }

        if (!isHost &&
            !string.IsNullOrWhiteSpace(roomCode) &&
            !roomCode.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            GameCode.From(roomCode).IsInvalid)
        {
            AppendLog($"Invalid room code '{roomCode}'. Expected a 4- or 6-letter code or 'auto'.");
            return;
        }

        if (profile.UseDeviceAuth && EosClientConfig.FromEnvironment() is null)
        {
            if (!PromptForEosConfig())
            {
                AppendLog("EOS credentials required for live profile.");
                return;
            }
        }

        SetSessionControlsEnabled(connected: false, busy: true);
        _statusLabel.Text = "Connecting...";
        _sessionCts = new CancellationTokenSource();
        var ct = _sessionCts.Token;

        try
        {
            var loggerFactory = new UiLoggerFactory(AppendLog);

            var vault = new Vault(vaultPath, vaultKey);
            _http = new HttpClient();
            var auth = new DeviceAuthSource(vault, _http, logger: loggerFactory.CreateLogger<DeviceAuthSource>());
            var matchmaker = new MatchmakerClient(_http, logger: loggerFactory.CreateLogger<MatchmakerClient>());
            var timing = profile.Mode == ServerProfileMode.Live ? OrchestratorTimingConfig.LiveDefault : OrchestratorTimingConfig.FastLocal;

            if (isHost || botCount > 1)
            {
                var roster = isHost ? Roster.CreateDefault(botCount) : Roster.CreateJoiners(botCount);
                var hostOptions = new HostLobbyOptions
                {
                    Map = MapTypes.Skeld,
                    ExpectedJoiners = isHost ? botCount : roster.JoinerSeats.Count,
                    PublicWhenSeated = _publicCheck.Checked,
                    AutoStartWhenReady = false,
                    StartGateDwell = timing.StartGateDwell,
                };

                _fleet = new Fleet(
                    profile,
                    vault,
                    auth,
                    matchmaker,
                    roster: roster,
                    hostOptions: hostOptions,
                    timing: timing,
                    replayDirectory: replaysDir,
                    locksDirectory: locksDir,
                    loggerFactory: loggerFactory);
                _surface = new CommandSurface(_fleet, vault);
                _controlServer = TryStartControl(_surface, _fleet);

                string lobbyCode;
                if (isHost)
                {
                    AppendLog($"Hosting lobby ({botCount} joiners, profile={profile.Mode}, region={profile.Region})...");
                    lobbyCode = await _fleet.StartHostLobbyAsync(ct: ct);
                }
                else
                {
                    var target = string.IsNullOrWhiteSpace(roomCode) ? "auto" : roomCode;
                    AppendLog($"Joining '{target}' with {botCount} bots (profile={profile.Mode}, region={profile.Region})...");
                    lobbyCode = await _fleet.StartJoinLobbyAsync(target, ct: ct);
                }

                var snap = _fleet.GetStatusSnapshot();
                _statusLabel.Text = $"Lobby {lobbyCode} | Bots {snap.ConnectedCount}/{snap.TargetSeats}";
                AppendLog($"Connected to lobby {lobbyCode} ({snap.ConnectedCount}/{snap.TargetSeats} bots).");
            }
            else
            {
                _singleBot = new BotClient(profile, vault, auth, matchmaker, loggerFactory: loggerFactory)
                {
                    MakePublicWhenHosting = _publicCheck.Checked,
                };
                _surface = new CommandSurface(_singleBot, vault);
                _controlServer = TryStartControl(_surface, _singleBot);

                var target = string.IsNullOrWhiteSpace(roomCode) ? "auto" : roomCode;
                AppendLog($"Joining '{target}' as bot-00 (profile={profile.Mode}, region={profile.Region})...");
                await _singleBot.JoinAsync(target, "bot-00", "BeanBot", replayDirectory: replaysDir, locksDirectory: locksDir, ct: ct);
                var status = _singleBot.GetStatus();
                _statusLabel.Text = $"Lobby {status.LobbyCode} | Bot 1/1";
                AppendLog($"Joined lobby {status.LobbyCode} as {status.DisplayName} (clientId={status.LocalClientId}).");
            }

            PopulateBotTargetSelector();
            RefreshLiveLists();
            SetSessionControlsEnabled(connected: true, busy: false);
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            _statusLabel.Text = "Disconnected";
            await CleanupSessionAsync();
            SetSessionControlsEnabled(connected: false, busy: false);
        }
    }

    private bool PromptForEosConfig()
    {
        using var dlg = new Form
        {
            Text = "EOS Credentials",
            Width = 420,
            Height = 250,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = BgPanel,
            ForeColor = TextPrimary,
        };

        var idBox = new TextBox { Left = 16, Top = 30, Width = 370 };
        var secretBox = new TextBox { Left = 16, Top = 82, Width = 370, UseSystemPasswordChar = true };
        var depBox = new TextBox { Left = 16, Top = 134, Width = 370 };
        var okBtn = new Button { Text = "Save", Left = 222, Top = 172, Width = 80, DialogResult = DialogResult.OK, BackColor = Accent };
        var cancelBtn = new Button { Text = "Cancel", Left = 306, Top = 172, Width = 80, DialogResult = DialogResult.Cancel };

        dlg.Controls.AddRange(
        [
            new Label { Text = "BEANBOT_EOS_CLIENT_ID", Left = 16, Top = 10, AutoSize = true, ForeColor = TextMuted },
            idBox,
            new Label { Text = "BEANBOT_EOS_CLIENT_SECRET", Left = 16, Top = 62, AutoSize = true, ForeColor = TextMuted },
            secretBox,
            new Label { Text = "BEANBOT_EOS_DEPLOYMENT_ID", Left = 16, Top = 114, AutoSize = true, ForeColor = TextMuted },
            depBox,
            okBtn,
            cancelBtn,
        ]);
        dlg.AcceptButton = okBtn;
        dlg.CancelButton = cancelBtn;
        ApplyDarkTheme(dlg);

        if (dlg.ShowDialog(this) != DialogResult.OK ||
            string.IsNullOrWhiteSpace(idBox.Text) ||
            string.IsNullOrWhiteSpace(secretBox.Text) ||
            string.IsNullOrWhiteSpace(depBox.Text))
        {
            return false;
        }

        Environment.SetEnvironmentVariable("BEANBOT_EOS_CLIENT_ID", idBox.Text.Trim());
        Environment.SetEnvironmentVariable("BEANBOT_EOS_CLIENT_SECRET", secretBox.Text.Trim());
        Environment.SetEnvironmentVariable("BEANBOT_EOS_DEPLOYMENT_ID", depBox.Text.Trim());
        return true;
    }

    private HttpControlServer? TryStartControl(CommandSurface surface, object target)
    {
        try
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            var server = target switch
            {
                IFleetControlTarget f => new HttpControlServer(surface, f, port),
                BotClient b => new HttpControlServer(surface, b, port),
                _ => null,
            };
            server?.Start();
            return server;
        }
        catch
        {
            return null;
        }
    }

    private async Task DisconnectSessionAsync()
    {
        SetSessionControlsEnabled(connected: false, busy: true);
        _statusLabel.Text = "Disconnecting...";
        await CleanupSessionAsync();
        _playersList.Items.Clear();
        _botsList.Items.Clear();
        _botTargetBox.Items.Clear();
        _botTargetBox.Items.Add("all");
        _botTargetBox.SelectedIndex = 0;
        _statusLabel.Text = "Disconnected";
        AppendLog("Disconnected.");
        SetSessionControlsEnabled(connected: false, busy: false);
    }

    private async Task CleanupSessionAsync()
    {
        try { _sessionCts?.Cancel(); } catch { }
        _sessionCts?.Dispose();
        _sessionCts = null;

        if (_controlServer is not null)
        {
            try { await _controlServer.DisposeAsync(); } catch { }
            _controlServer = null;
        }

        if (_fleet is not null)
        {
            try { await _fleet.DisposeAsync(); } catch { }
            _fleet = null;
        }

        if (_singleBot is not null)
        {
            try { await _singleBot.DisposeAsync(); } catch { }
            _singleBot = null;
        }

        _surface = null;
        _http?.Dispose();
        _http = null;
    }

    private void PopulateBotTargetSelector()
    {
        _botTargetBox.Items.Clear();
        _botTargetBox.Items.Add("all");
        if (_surface is not null)
        {
            foreach (var b in _surface.GetBotsSnapshot())
            {
                _botTargetBox.Items.Add(b.AccountId);
            }
        }

        _botTargetBox.SelectedIndex = 0;
    }

    private void RefreshLiveLists()
    {
        if (_surface is null)
        {
            return;
        }

        var players = _surface.GetPlayersSnapshot();
        _playersList.BeginUpdate();
        try
        {
            _playersList.Items.Clear();
            foreach (var p in players)
            {
                var item = new ListViewItem(p.Name) { Tag = p };
                item.SubItems.Add(string.IsNullOrWhiteSpace(p.ColorName) ? "-" : p.ColorName);
                var stateText = p.IsAlive ? p.Role.ToString() : $"{p.Role} (Dead)";
                item.SubItems.Add(stateText);
                item.SubItems.Add(p.IsBot ? "Bot" : p.IsHost ? "Host" : "Player");
                item.Selected = _selectedPlayerClientId == p.ClientId;
                _playersList.Items.Add(item);
            }
        }
        finally
        {
            _playersList.EndUpdate();
        }

        var bots = _surface.GetBotsSnapshot();
        var prevBot = _botsList.SelectedItems.Count > 0 && _botsList.SelectedItems[0].Tag is BotStatusSnapshot sb
            ? sb.AccountId
            : null;
        _botsList.BeginUpdate();
        try
        {
            _botsList.Items.Clear();
            foreach (var b in bots)
            {
                var item = new ListViewItem(b.DisplayName) { Tag = b };
                item.SubItems.Add(b.AccountId);
                item.SubItems.Add(b.IsConnected ? b.Phase.ToString() : "Offline");
                item.SubItems.Add(string.IsNullOrWhiteSpace(b.FollowTarget) ? "-" : b.FollowTarget);
                if (prevBot == b.AccountId)
                {
                    item.Selected = true;
                }

                _botsList.Items.Add(item);
            }
        }
        finally
        {
            _botsList.EndUpdate();
        }

        var connectedCount = bots.Count(b => b.IsConnected);
        var lobby = bots.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.LobbyCode))?.LobbyCode ?? "-";
        if (_connectBtn.Enabled == false && _disconnectBtn.Enabled)
        {
            _statusLabel.Text = $"Lobby {lobby} | Bots {connectedCount}/{bots.Count}";
        }
    }

    private async Task SendSayAsync()
    {
        var msg = _sayBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(msg))
        {
            return;
        }

        _sayBox.Clear();
        await ExecuteCommandAsync($"say {SelectedBotTarget()} {msg}");
    }

    private async Task SendCustomCommandAsync()
    {
        var cmd = _cmdBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(cmd))
        {
            return;
        }

        _cmdBox.Clear();
        await ExecuteCommandAsync(cmd);
    }

    private async Task ExecuteCommandAsync(string command)
    {
        if (_surface is null)
        {
            AppendLog("Not connected.");
            return;
        }

        var trimmed = command.Trim();
        var res = await _surface.ExecuteAsync(trimmed);
        AppendLog($"> {trimmed}");
        if (!string.IsNullOrWhiteSpace(res.Message))
        {
            AppendLog(res.Message);
        }

        RefreshLiveLists();
    }

    private void SetSessionControlsEnabled(bool connected, bool busy)
    {
        _modeBox.Enabled = !connected && !busy;
        _roomBox.Enabled = !connected && !busy && _modeBox.SelectedIndex == 0;
        _botsNum.Enabled = !connected && !busy;
        _profileBox.Enabled = !connected && !busy;
        _regionBox.Enabled = !connected && !busy && _profileBox.SelectedIndex == 0;
        _matchmakerBox.Enabled = !connected && !busy;
        _vaultKeyBox.Enabled = !connected && !busy;
        _publicCheck.Enabled = !connected && !busy;
        _connectBtn.Enabled = !connected && !busy;
        _disconnectBtn.Enabled = connected && !busy;

        var actionsEnabled = connected && !busy;
        _botTargetBox.Enabled = actionsEnabled;
        _followBtn.Enabled = actionsEnabled;
        _killHoldCheck.Enabled = actionsEnabled;
        _stopFollowBtn.Enabled = actionsEnabled;
        _voteBtn.Enabled = actionsEnabled;
        _skipVoteBtn.Enabled = actionsEnabled;
        _killBtn.Enabled = actionsEnabled;
        _tasksOnBtn.Enabled = actionsEnabled;
        _tasksOffBtn.Enabled = actionsEnabled;
        _sayBox.Enabled = actionsEnabled;
        _sayBtn.Enabled = actionsEnabled;
        _cmdBox.Enabled = actionsEnabled;
        _cmdBtn.Enabled = actionsEnabled;
    }

    private void AppendLog(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try { BeginInvoke(() => AppendLog(message)); } catch { }
            return;
        }

        if (_logBox.TextLength > 32_000)
        {
            _logBox.Text = _logBox.Text[^16_000..];
        }

        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        _refreshTimer.Stop();
        await CleanupSessionAsync();
        base.OnFormClosing(e);
    }

    private sealed class UiLoggerFactory(Action<string> write) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new UiLogger(categoryName, write);
        public void Dispose() { }

        private sealed class UiLogger(string category, Action<string> write) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                var msg = formatter(state, exception);
                if (exception is not null) msg = $"{msg} | {exception.Message}";
                var shortCat = category.Split('.').LastOrDefault() ?? category;
                write($"[{logLevel} {shortCat}] {msg}");
            }
        }
    }
}
