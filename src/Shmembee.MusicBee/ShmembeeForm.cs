using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Shmembee.Application.Desktop;
using Shmembee.Application.Reconciliation;
using Shmembee.Application.Synchronization;
using Shmembee.Core.Reconciliation;
using Shmembee.Infrastructure.Diagnostics;
using Shmembee.Infrastructure.Persistence;
using Shmembee.Infrastructure.Playlists;
using Shmembee.Infrastructure.Settings;

namespace MusicBeePlugin
{
    internal class ShmembeeForm : Form
    {
        private readonly PlaylistSyncController controller;
        private readonly MusicBeeTheme theme;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly TabControl tabs = new TabControl();
        private readonly Label summaryTitle = new Label();
        private readonly Label summaryDetail = new Label();
        private readonly Label activityLabel = new Label();
        private readonly Label outcomeValue = new Label();
        private readonly Label musicBeeCountValue = new Label();
        private readonly Label phoneCountValue = new Label();
        private readonly Label warningLabel = new Label();
        private readonly ListBox musicBeeList = new ListBox();
        private readonly ListBox phoneList = new ListBox();
        private readonly ListBox proposalList = new ListBox();
        private readonly ListBox historyList = new ListBox();
        private readonly ListBox importList = new ListBox();
        private readonly ListBox diagnosticsList = new ListBox();
        private readonly Label importSummary = new Label();
        private readonly TextBox deviceNameText = new TextBox();
        private readonly TextBox storageNameText = new TextBox();
        private readonly TextBox playlistFolderText = new TextBox();
        private readonly TextBox postSyncBackupPathText = new TextBox();
        private readonly string storagePath;
        private readonly ReviewedPlaylistDraftStore? reviewDraftStore;
        private readonly Plugin.MusicBeeApiInterface? api;
        private readonly Button refreshButton = new Button();
        private readonly Button baselineButton = new Button();
        private readonly Button applyButton = new Button();
        private readonly Button cancelButton = new Button();
        private readonly Panel workspace = new Panel();
        private readonly Panel landingPage = new Panel();
        private readonly Panel membershipPage = new Panel();
        private readonly Panel orderPage = new Panel();
        private readonly DataGridView playlistGrid = new DataGridView();
        private readonly DataGridView membershipGrid = new DataGridView();
        private readonly TextBox playlistSearch = new TextBox();
        private readonly CheckBox showMatchingTracks = new CheckBox();
        private readonly RadioButton musicBeeOrder = new RadioButton();
        private readonly RadioButton phoneOrder = new RadioButton();
        private readonly Label modernTitle = new Label();
        private readonly Label modernSubtitle = new Label();
        private readonly Button applyAllButton = new Button();
        private readonly Button detailBackButton = new Button();
        private readonly Button orderBackButton = new Button();
        private readonly Button continueButton = new Button();
        private readonly Button confirmDraftButton = new Button();
        private readonly Dictionary<string, PlaylistReviewDraft> reviewDrafts =
            new Dictionary<string, PlaylistReviewDraft>(StringComparer.Ordinal);
        private IReadOnlyList<HarnessPlaylistRow> playlistRows =
            Array.Empty<HarnessPlaylistRow>();
        private HarnessPlaylistRow? selectedPlaylistRow;
        private HarnessPreview? preview;
        private CancellationTokenSource? operation;
        private bool busy;

        public ShmembeeForm(PlaylistSyncController controller)
            : this(
                controller,
                MusicBeeTheme.CreateDefault(),
                null,
                string.Empty)
        {
        }

        public ShmembeeForm(
            PlaylistSyncController controller,
            MusicBeeTheme theme,
            Plugin.MusicBeeApiInterface? api = null,
            string? storagePath = null)
        {
            this.controller = controller
                ?? throw new ArgumentNullException(nameof(controller));
            this.theme = theme ?? throw new ArgumentNullException(nameof(theme));
            this.api = api;
            this.storagePath = storagePath ?? string.Empty;
            reviewDraftStore = string.IsNullOrWhiteSpace(this.storagePath)
                ? null
                : new ReviewedPlaylistDraftStore(
                    System.IO.Path.Combine(this.storagePath, "reviewed-drafts.json"));
            LoadReviewDrafts();
            Text = "Shmembee";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(960, 640);
            Size = new Size(1180, 760);
            FormBorderStyle = FormBorderStyle.Sizable;
            BuildModernLayout();
            controller.AttachUiDispatcher(this);
            this.theme.Apply(this);
            ApplyThemeDetails();
            Shown += async (_, _) => await RefreshAsync();
            FormClosing += OnFormClosing;
        }

        public event EventHandler? RefreshCompleted;

        public event EventHandler? ApplyCompleted;

        public event EventHandler? SettingsSaveRequested;

        public bool IsBusy => busy;

        public Task RefreshFromHostAsync() => RefreshAsync();

        public string GetSettingValue(string settingName)
        {
            TextBox? field = Controls.Find(settingName, true)
                .OfType<TextBox>()
                .FirstOrDefault();
            return field?.Text ?? string.Empty;
        }

        public void SetSettingValue(string settingName, string value)
        {
            TextBox? field = Controls.Find(settingName, true)
                .OfType<TextBox>()
                .FirstOrDefault();
            if (field != null)
            {
                field.Text = value ?? string.Empty;
            }
        }

        public void SelectPage(string pageName)
        {
            TabPage? page = tabs.TabPages
                .Cast<TabPage>()
                .FirstOrDefault(item => string.Equals(
                    item.Text,
                    pageName,
                    StringComparison.OrdinalIgnoreCase));
            if (page != null)
            {
                tabs.SelectedTab = page;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            theme.ApplyDarkTitleBar(this);
        }

        private void BuildLayout()
        {
            SuspendLayout();
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(18)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildWarning(), 0, 1);
            root.Controls.Add(BuildTabs(), 0, 2);
            root.Controls.Add(BuildFooter(), 0, 3);
            Controls.Add(root);
            ResumeLayout();
        }

        private void BuildModernLayout()
        {
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.Dpi;
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(8)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(BuildModernToolbar(), 0, 0);
            workspace.Dock = DockStyle.Fill;
            workspace.Controls.Add(BuildLandingPage());
            workspace.Controls.Add(BuildMembershipPage());
            workspace.Controls.Add(BuildOrderPage());
            root.Controls.Add(workspace, 0, 1);
            root.Controls.Add(BuildModernFooter(), 0, 2);
            Controls.Add(root);
            ShowWorkspacePage(landingPage);
            ResumeLayout();
        }

        private Control BuildModernToolbar()
        {
            var toolbar = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 5,
                Padding = new Padding(0, 0, 0, 6)
            };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            modernTitle.Text = "PLAYLIST DIFF";
            modernTitle.AutoSize = true;
            modernTitle.Font = new Font(theme.Font, FontStyle.Bold);
            modernTitle.Margin = new Padding(2, 8, 18, 0);
            modernSubtitle.Text = "MusicBee  ↔  Phone";
            modernSubtitle.AutoSize = true;
            modernSubtitle.Margin = new Padding(0, 8, 8, 0);
            playlistSearch.AccessibleName = "Search playlists";
            playlistSearch.Dock = DockStyle.Fill;
            playlistSearch.Margin = new Padding(4);
            playlistSearch.TextChanged += (_, _) => RenderPlaylistRows();
            refreshButton.Text = "Refresh";
            refreshButton.AutoSize = true;
            refreshButton.Click += async (_, _) => await RefreshAsync();
            var moreButton = new Button { Text = "More ▾", AutoSize = true };
            var more = new ContextMenuStrip();
            more.Items.Add("Import", null, (_, _) => ShowLegacyPage("Import"));
            more.Items.Add("History", null, (_, _) => ShowLegacyPage("History"));
            more.Items.Add("Setup", null, (_, _) => ShowLegacyPage("Setup"));
            more.Items.Add("Settings", null, (_, _) => ShowLegacyPage("Settings"));
            moreButton.Click += (_, _) => more.Show(moreButton, 0, moreButton.Height);
            toolbar.Controls.Add(modernTitle, 0, 0);
            toolbar.Controls.Add(modernSubtitle, 1, 0);
            toolbar.Controls.Add(playlistSearch, 2, 0);
            toolbar.Controls.Add(refreshButton, 3, 0);
            toolbar.Controls.Add(moreButton, 4, 0);
            return toolbar;
        }

        private Control BuildLandingPage()
        {
            landingPage.Dock = DockStyle.Fill;
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            var headings = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 3,
                Padding = new Padding(0, 4, 0, 4)
            };
            headings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
            headings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            headings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
            ConfigurePlaylistGrid();
            playlistGrid.CellContentClick += PlaylistGridCellContentClick;
            playlistGrid.CellDoubleClick += PlaylistGridCellDoubleClick;
            playlistGrid.CellFormatting += PlaylistGridCellFormatting;
            root.Controls.Add(headings, 0, 0);
            root.Controls.Add(playlistGrid, 0, 1);
            landingPage.Controls.Add(root);
            return landingPage;
        }

        private void ConfigurePlaylistGrid()
        {
            playlistGrid.Dock = DockStyle.Fill;
            playlistGrid.AutoGenerateColumns = false;
            playlistGrid.AllowUserToAddRows = false;
            playlistGrid.AllowUserToDeleteRows = false;
            playlistGrid.AllowUserToResizeRows = false;
            playlistGrid.RowHeadersVisible = false;
            playlistGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            playlistGrid.MultiSelect = false;
            playlistGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            playlistGrid.Columns.Add(new DataGridViewCheckBoxColumn
            { Name = "TakeMusicBee", HeaderText = "", FillWeight = 7 });
            playlistGrid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "MusicBee", HeaderText = "MusicBee", FillWeight = 25, ReadOnly = true });
            playlistGrid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Status", HeaderText = "Status", FillWeight = 36, ReadOnly = true });
            playlistGrid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Phone", HeaderText = "Phone", FillWeight = 25, ReadOnly = true });
            playlistGrid.Columns.Add(new DataGridViewCheckBoxColumn
            { Name = "TakePhone", HeaderText = "", FillWeight = 7 });
        }

        private Control BuildMembershipPage()
        {
            membershipPage.Dock = DockStyle.Fill;
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                WrapContents = false
            };
            detailBackButton.Text = "← Playlists";
            detailBackButton.AutoSize = true;
            detailBackButton.Click += (_, _) => SaveDetailAndReturn();
            showMatchingTracks.Text = "Show matching tracks";
            showMatchingTracks.AutoSize = true;
            showMatchingTracks.Margin = new Padding(18, 8, 0, 0);
            showMatchingTracks.CheckedChanged += (_, _) => RenderMembershipRows();
            top.Controls.Add(detailBackButton);
            top.Controls.Add(showMatchingTracks);
            ConfigureMembershipGrid();
            continueButton.Text = "Continue to order →";
            continueButton.AutoSize = true;
            continueButton.Anchor = AnchorStyles.Right;
            continueButton.Click += (_, _) => ContinueToOrder();
            root.Controls.Add(top, 0, 0);
            root.Controls.Add(membershipGrid, 0, 1);
            root.Controls.Add(continueButton, 0, 2);
            membershipPage.Controls.Add(root);
            return membershipPage;
        }

        private void ConfigureMembershipGrid()
        {
            membershipGrid.Dock = DockStyle.Fill;
            membershipGrid.AutoGenerateColumns = false;
            membershipGrid.AllowUserToAddRows = false;
            membershipGrid.AllowUserToDeleteRows = false;
            membershipGrid.RowHeadersVisible = false;
            membershipGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            membershipGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            membershipGrid.Columns.Add(new DataGridViewCheckBoxColumn
            { Name = "Include", HeaderText = "Keep", FillWeight = 8 });
            membershipGrid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "MusicBeeTrack", HeaderText = "MusicBee track", FillWeight = 42, ReadOnly = true });
            membershipGrid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Change", HeaderText = "", FillWeight = 8, ReadOnly = true });
            membershipGrid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "PhoneTrack", HeaderText = "Phone track", FillWeight = 42, ReadOnly = true });
            membershipGrid.CellFormatting += MembershipGridCellFormatting;
        }

        private Control BuildOrderPage()
        {
            orderPage.Dock = DockStyle.Fill;
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(24)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            orderBackButton.Text = "← Track choices";
            orderBackButton.AutoSize = true;
            orderBackButton.Click += (_, _) => ShowWorkspacePage(membershipPage);
            var choices = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(20)
            };
            var heading = new Label
            {
                Text = "Choose the final playlist order",
                AutoSize = true,
                Font = new Font(theme.Font.FontFamily, theme.Font.Size + 3F, FontStyle.Bold)
            };
            musicBeeOrder.Text = "Preserve MusicBee order";
            musicBeeOrder.AutoSize = true;
            musicBeeOrder.Checked = true;
            musicBeeOrder.Margin = new Padding(0, 24, 0, 8);
            phoneOrder.Text = "Preserve phone order";
            phoneOrder.AutoSize = true;
            choices.Controls.Add(heading);
            choices.Controls.Add(musicBeeOrder);
            choices.Controls.Add(phoneOrder);
            confirmDraftButton.Text = "Confirm changes";
            confirmDraftButton.AutoSize = true;
            confirmDraftButton.Anchor = AnchorStyles.Right;
            confirmDraftButton.Click += (_, _) => ConfirmDraft();
            root.Controls.Add(orderBackButton, 0, 0);
            root.Controls.Add(choices, 0, 1);
            root.Controls.Add(confirmDraftButton, 0, 2);
            orderPage.Controls.Add(root);
            return orderPage;
        }

        private Control BuildModernFooter()
        {
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                ColumnCount = 3,
                Padding = new Padding(0, 6, 0, 0)
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            activityLabel.Text = "Ready";
            activityLabel.AutoSize = true;
            activityLabel.Margin = new Padding(2, 9, 8, 0);
            cancelButton.Text = "Cancel";
            cancelButton.AutoSize = true;
            cancelButton.Enabled = false;
            cancelButton.Click += (_, _) => operation?.Cancel();
            applyAllButton.Text = "Apply All Changes";
            applyAllButton.AutoSize = true;
            applyAllButton.Enabled = false;
            applyAllButton.Click += async (_, _) => await ApplyAllAsync();
            footer.Controls.Add(activityLabel, 0, 0);
            footer.Controls.Add(cancelButton, 1, 0);
            footer.Controls.Add(applyAllButton, 2, 0);
            return footer;
        }

        private Label SectionLabel(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = theme.Muted,
            Font = new Font(theme.Font, FontStyle.Bold)
        };

        private void ShowWorkspacePage(Control page)
        {
            landingPage.Visible = page == landingPage;
            membershipPage.Visible = page == membershipPage;
            orderPage.Visible = page == orderPage;
            page.BringToFront();
            playlistSearch.Enabled = page == landingPage;
            applyAllButton.Visible = page == landingPage;
        }

        private void ShowLegacyPage(string pageName)
        {
            MessageBox.Show(
                pageName + " remains available through the existing Shmembee workflow "
                    + "while playlist review is open.",
                "Shmembee " + pageName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private Control BuildHeader()
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Padding = new Padding(2, 0, 2, 14)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var copy = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };
            summaryTitle.Text = "Playlist synchronization";
            summaryTitle.AutoSize = true;
            summaryTitle.Font = new Font(theme.Font.FontFamily, 18F, FontStyle.Bold);
            summaryDetail.Text = "Review MusicBee and phone changes before anything is written.";
            summaryDetail.AutoSize = true;
            summaryDetail.Margin = new Padding(0, 5, 0, 0);
            copy.Controls.Add(summaryTitle);
            copy.Controls.Add(summaryDetail);
            refreshButton.Text = "Refresh";
            refreshButton.AutoSize = true;
            refreshButton.Padding = new Padding(12, 6, 12, 6);
            refreshButton.Click += async (_, _) => await RefreshAsync();
            header.Controls.Add(copy, 0, 0);
            header.Controls.Add(refreshButton, 1, 0);
            return header;
        }

        private Control BuildWarning()
        {
            warningLabel.AutoSize = true;
            warningLabel.Dock = DockStyle.Fill;
            warningLabel.Padding = new Padding(12, 10, 12, 10);
            warningLabel.Margin = new Padding(0, 0, 0, 14);
            warningLabel.Text =
                "MusicBee playlist sync warning  •  Shmembee can replace the selected "
                + "MusicBee playlist when you apply. Review the proposal and keep a backup.";
            return warningLabel;
        }

        private Control BuildTabs()
        {
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(14, 6);
            tabs.TabPages.Add(BuildPlaylistsPage());
            tabs.TabPages.Add(BuildImportPage());
            tabs.TabPages.Add(BuildHistoryPage());
            tabs.TabPages.Add(BuildSetupPage());
            tabs.TabPages.Add(BuildSettingsPage());
            return tabs;
        }

        private TabPage BuildPlaylistsPage()
        {
            var page = CreatePage("Playlists");
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 54F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 46F));
            root.Controls.Add(BuildStatusCards(), 0, 0);
            var sources = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(0, 12, 0, 6)
            };
            sources.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            sources.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            sources.Controls.Add(BuildReviewList(
                "MusicBee playlist",
                "Canonical library tracks",
                musicBeeList), 0, 0);
            sources.Controls.Add(BuildReviewList(
                "Phone playlist",
                "Resolved device tracks",
                phoneList), 1, 0);
            root.Controls.Add(sources, 0, 1);
            root.Controls.Add(BuildReviewList(
                "Proposed synchronized playlist",
                "The ordered result that will be written after confirmation",
                proposalList), 0, 2);
            page.Controls.Add(root);
            return page;
        }

        private Control BuildStatusCards()
        {
            var cards = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 3
            };
            for (int index = 0; index < 3; index++)
            {
                cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            }

            cards.Controls.Add(CreateStatusCard("SYNC STATUS", outcomeValue), 0, 0);
            cards.Controls.Add(CreateStatusCard("MUSICBEE TRACKS", musicBeeCountValue), 1, 0);
            cards.Controls.Add(CreateStatusCard("PHONE TRACKS", phoneCountValue), 2, 0);
            return cards;
        }

        private Control CreateStatusCard(string caption, Label value)
        {
            var card = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(12),
                Margin = new Padding(4)
            };
            var heading = new Label
            {
                Text = caption,
                AutoSize = true
            };
            value.Text = "—";
            value.AutoSize = true;
            value.Font = new Font(theme.Font.FontFamily, 14F, FontStyle.Bold);
            value.Margin = new Padding(0, 5, 0, 0);
            card.Controls.Add(heading);
            card.Controls.Add(value);
            card.Tag = "card";
            return card;
        }

        private static Control BuildReviewList(
            string title,
            string detail,
            ListBox list)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(4)
            };
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            panel.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold)
            }, 0, 0);
            panel.Controls.Add(new Label
            {
                Text = detail,
                AutoSize = true,
                Padding = new Padding(0, 2, 0, 7)
            }, 0, 1);
            list.Dock = DockStyle.Fill;
            list.HorizontalScrollbar = true;
            list.IntegralHeight = false;
            panel.Controls.Add(list, 0, 2);
            return panel;
        }

        private TabPage BuildImportPage()
        {
            var page = CreatePage("Import");
            var panel = BuildSection(
                "Preview an M3U playlist",
                "Shmembee resolves every entry without changing MusicBee.");
            var choose = new Button
            {
                Text = "Choose M3U or M3U8…",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Padding = new Padding(10, 5, 10, 5)
            };
            choose.Click += async (_, _) => await PreviewImportAsync();
            importSummary.AutoSize = true;
            importSummary.Padding = new Padding(0, 8, 0, 8);
            importList.Dock = DockStyle.Fill;
            importList.IntegralHeight = false;
            panel.Controls.Add(choose, 0, 2);
            panel.Controls.Add(importSummary, 0, 3);
            panel.Controls.Add(importList, 0, 4);
            page.Controls.Add(panel);
            return page;
        }

        private TabPage BuildHistoryPage()
        {
            var page = CreatePage("History");
            var panel = BuildSection(
                "Synchronization history",
                "Completed operations and important results from this session.");
            historyList.Dock = DockStyle.Fill;
            historyList.IntegralHeight = false;
            panel.Controls.Add(historyList, 0, 2);
            page.Controls.Add(panel);
            return page;
        }

        private TabPage BuildSetupPage()
        {
            var page = CreatePage("Setup");
            var panel = BuildSection(
                "Connection setup",
                "Check local storage, helper deployment, MusicBee, and phone access.");
            var run = new Button
            {
                Text = "Run diagnostics",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Padding = new Padding(10, 5, 10, 5)
            };
            run.Click += async (_, _) => await RunDiagnosticsAsync();
            diagnosticsList.Dock = DockStyle.Fill;
            diagnosticsList.IntegralHeight = false;
            panel.Controls.Add(run, 0, 2);
            panel.Controls.Add(diagnosticsList, 0, 3);
            page.Controls.Add(panel);
            return page;
        }

        private TabPage BuildSettingsPage()
        {
            var page = CreatePage("Settings");
            var panel = BuildSection(
                "Synchronization settings",
                "These fields are ready for the persistent settings backend.");
            InitializeSettingText(
                deviceNameText,
                "DeviceName",
                controller.Settings.DeviceName);
            InitializeSettingText(
                storageNameText,
                "StorageName",
                controller.Settings.StorageName);
            InitializeSettingText(
                playlistFolderText,
                "PlaylistFolder",
                controller.Settings.PlaylistFolder);
            InitializeSettingText(
                postSyncBackupPathText,
                "PostSyncBackupPath",
                controller.Settings.PostSyncBackupPath);
            panel.Controls.Add(CreateField("Device name", deviceNameText), 0, 2);
            panel.Controls.Add(CreateField("Storage", storageNameText), 0, 3);
            panel.Controls.Add(CreateField("Playlist folder", playlistFolderText), 0, 4);
            panel.Controls.Add(
                CreateField("Post-sync M3U backup folder", postSyncBackupPathText),
                0,
                5);
            var save = new Button
            {
                Text = "Save settings",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Padding = new Padding(10, 5, 10, 5),
                Margin = new Padding(0, 14, 0, 0)
            };
            save.Click += (_, _) => SaveSettings();
            panel.Controls.Add(save, 0, 6);
            page.Controls.Add(panel);
            return page;
        }

        private TabPage BuildPlaceholderPage(
            string tab,
            string title,
            string detail)
        {
            var page = CreatePage(tab);
            page.Controls.Add(BuildSection(title, detail));
            return page;
        }

        private static TableLayoutPanel BuildSection(string title, string detail)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                AutoScroll = true,
                Padding = new Padding(22)
            };
            panel.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 15F, FontStyle.Bold)
            }, 0, 0);
            panel.Controls.Add(new Label
            {
                Text = detail,
                AutoSize = true,
                Padding = new Padding(0, 5, 0, 14)
            }, 0, 1);
            return panel;
        }

        private static Control CreateReadOnlyField(string label, string value)
        {
            var text = new TextBox
            {
                Text = value,
                ReadOnly = true,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 3, 0, 10)
            };
            return CreateField(label, text);
        }

        private static Control CreateSettingField(
            string label,
            string value,
            string settingName)
        {
            var text = new TextBox
            {
                Text = value,
                Dock = DockStyle.Top,
                Name = settingName,
                Margin = new Padding(0, 3, 0, 10)
            };
            return CreateField(label, text);
        }

        private static void InitializeSettingText(
            TextBox text,
            string name,
            string value)
        {
            text.Text = value;
            text.Name = name;
            text.Dock = DockStyle.Top;
            text.Margin = new Padding(0, 3, 0, 10);
        }

        private async Task PreviewImportAsync()
        {
            using (var dialog = new OpenFileDialog
            {
                Filter = "M3U playlists (*.m3u;*.m3u8)|*.m3u;*.m3u8",
                CheckFileExists = true,
                Multiselect = false,
                Title = "Preview playlist import"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                await RunOperationAsync(
                    "Parsing and resolving playlist…",
                    token => Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        var library = new MusicBeeLibraryReader(
                            api ?? throw new InvalidOperationException(
                                "MusicBee library access is unavailable."))
                            .ReadLibrary()
                            .Select(track => new Shmembee.Core.Resolution.LibraryTrack(
                                track.Url,
                                track.Url,
                                track.Artist,
                                track.Title,
                                track.DurationSeconds));
                        return new M3uImportPreviewService().Preview(
                            dialog.FileName,
                            library);
                    }, token),
                    RenderImportPreview);
            }
        }

        private void RenderImportPreview(M3uImportPreview result)
        {
            importSummary.Text = result.Entries.Count
                + " entries  •  "
                + result.MatchedCount
                + " matched  •  "
                + result.AmbiguousCount
                + " ambiguous  •  "
                + result.UnmatchedCount
                + " unmatched  •  "
                + result.DuplicateCount
                + " duplicates";
            importList.BeginUpdate();
            try
            {
                importList.Items.Clear();
                foreach (M3uImportPreviewEntry entry in result.Entries)
                {
                    importList.Items.Add(
                        entry.Parsed.LineNumber
                            + "  "
                            + entry.Resolution.Status
                            + "  "
                            + entry.Parsed.NormalizedPhonePath
                            + (entry.Resolution.Match == null
                                ? string.Empty
                                : "  →  " + entry.Resolution.Match.Url));
                }
            }
            finally
            {
                importList.EndUpdate();
            }
        }

        private async Task RunDiagnosticsAsync()
        {
            if (string.IsNullOrWhiteSpace(storagePath))
            {
                MessageBox.Show(
                    this,
                    "MusicBee did not provide a persistent storage path.",
                    "Shmembee diagnostics",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            await RunOperationAsync(
                "Running setup diagnostics…",
                token => Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    string sidecar = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "Plugins",
                        "Shmembee.WpdSidecar",
                        "Shmembee.WpdSidecar.exe");
                    DesktopSettings settings = controller.Settings;
                    var transport = new Shmembee.Windows.WpdSidecarPlaylistTransport(
                        sidecar,
                        settings.DeviceName,
                        settings.StorageName,
                        settings.PlaylistFolder,
                        TimeSpan.FromSeconds(settings.TimeoutSeconds));
                    return new SetupDiagnosticService(
                        storagePath,
                        string.IsNullOrWhiteSpace(settings.DatabasePath)
                            ? System.IO.Path.Combine(storagePath, "shmembee.db")
                            : settings.DatabasePath,
                        string.IsNullOrWhiteSpace(settings.BackupPath)
                            ? System.IO.Path.Combine(storagePath, "backups")
                            : settings.BackupPath,
                        sidecar,
                        () =>
                        {
                            var response = transport.Probe();
                            return new SetupDiagnosticCheckResult(
                                "phone",
                                response.Success
                                    ? SetupDiagnosticStatus.Passed
                                    : SetupDiagnosticStatus.Failed,
                                settings.DeviceName
                                    + " • "
                                    + response.EnumeratePlaylistNames().Count
                                    + " playlists");
                        }).Run();
                }, token),
                RenderDiagnostics);
        }

        private void RenderDiagnostics(SetupDiagnosticResult result)
        {
            diagnosticsList.Items.Clear();
            foreach (SetupDiagnosticCheckResult check in result.Checks)
            {
                diagnosticsList.Items.Add(
                    (check.Status == SetupDiagnosticStatus.Passed ? "PASS  " : "FAIL  ")
                        + check.Name
                        + "  —  "
                        + check.Details);
            }
        }

        private void SaveSettings()
        {
            if (string.IsNullOrWhiteSpace(storagePath))
            {
                return;
            }

            DesktopSettings settings = controller.Settings;
            settings.DeviceName = deviceNameText.Text;
            settings.StorageName = storageNameText.Text;
            settings.PlaylistFolder = playlistFolderText.Text;
            settings.PostSyncBackupPath = postSyncBackupPathText.Text;
            new DesktopSettingsStore(
                System.IO.Path.Combine(storagePath, "settings.json")).Save(settings);
            SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
            MessageBox.Show(
                this,
                "Settings saved. Reopen Shmembee to reconnect with the new values.",
                "Shmembee settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private static Control CreateField(string label, TextBox text)
        {
            var panel = new TableLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                ColumnCount = 1
            };
            panel.Controls.Add(new Label { Text = label, AutoSize = true }, 0, 0);
            panel.Controls.Add(text, 0, 1);
            return panel;
        }

        private Control BuildFooter()
        {
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                ColumnCount = 2,
                Padding = new Padding(2, 13, 2, 0)
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            activityLabel.Text = "Ready";
            activityLabel.AutoSize = true;
            activityLabel.Anchor = AnchorStyles.Left;
            var actions = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            baselineButton.Text = "Accept matching baseline";
            applyButton.Text = "Apply synchronization";
            cancelButton.Text = "Cancel";
            foreach (Button button in new[] { baselineButton, applyButton, cancelButton })
            {
                button.AutoSize = true;
                button.Padding = new Padding(10, 5, 10, 5);
            }

            baselineButton.Click += async (_, _) => await EstablishBaselineAsync();
            applyButton.Click += async (_, _) => await ApplyAsync();
            cancelButton.Click += (_, _) => operation?.Cancel();
            actions.Controls.Add(baselineButton);
            actions.Controls.Add(applyButton);
            actions.Controls.Add(cancelButton);
            footer.Controls.Add(activityLabel, 0, 0);
            footer.Controls.Add(actions, 1, 0);
            SetActionState();
            return footer;
        }

        private static TabPage CreatePage(string text)
        {
            return new TabPage(text) { Padding = new Padding(0) };
        }

        private async Task RefreshAsync()
        {
            if (busy)
            {
                return;
            }

            await RunOperationAsync(
                "Reading MusicBee and phone playlists…",
                token => Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    return controller.RefreshPlaylistRows();
                }, token),
                result =>
                {
                    playlistRows = result;
                    UpdateDraftFreshness();
                    RenderPlaylistRows();
                    LoadHistory();
                    RefreshCompleted?.Invoke(this, EventArgs.Empty);
                });
        }

        private void RenderPlaylistRows()
        {
            string filter = playlistSearch.Text.Trim();
            string? selectedRowId = playlistGrid.CurrentRow?.Tag is HarnessPlaylistRow selected
                ? selected.RowId
                : null;
            string? currentColumnName = playlistGrid.CurrentCell == null
                ? null
                : playlistGrid.Columns[playlistGrid.CurrentCell.ColumnIndex].Name;
            string? firstVisibleRowId = null;
            int horizontalOffset = playlistGrid.HorizontalScrollingOffset;
            if (playlistGrid.FirstDisplayedScrollingRowIndex >= 0)
            {
                firstVisibleRowId =
                    playlistGrid.Rows[playlistGrid.FirstDisplayedScrollingRowIndex].Tag
                        is HarnessPlaylistRow firstVisible
                    ? firstVisible.RowId
                    : null;
            }

            playlistGrid.Rows.Clear();
            foreach (HarnessPlaylistRow row in playlistRows.Where(item =>
                filter.Length == 0
                || item.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                PlaylistReviewDraft? draft = reviewDrafts.TryGetValue(
                    row.RowId,
                    out PlaylistReviewDraft value)
                    ? value
                    : null;
                int index = playlistGrid.Rows.Add(
                    draft?.Action == PlaylistLandingAction.TakeMusicBee && !draft.IsStale,
                    row.MusicBeeName ?? "(blank — delete from phone)",
                    draft?.IsStale == true
                        ? "REVIEW STALE"
                        : draft?.Action == PlaylistLandingAction.Custom
                        ? "CUSTOM"
                        : row.StatusText,
                    row.PhoneName ?? "(blank — delete from MusicBee)",
                    draft?.Action == PlaylistLandingAction.TakePhone && !draft.IsStale);
                playlistGrid.Rows[index].Tag = row;
            }

            DataGridViewRow? restoredSelection = playlistGrid.Rows
                .Cast<DataGridViewRow>()
                .FirstOrDefault(item =>
                    item.Tag is HarnessPlaylistRow row
                    && string.Equals(row.RowId, selectedRowId, StringComparison.Ordinal));
            if (restoredSelection != null)
            {
                int columnIndex = currentColumnName == null
                    ? 0
                    : playlistGrid.Columns[currentColumnName]?.Index ?? 0;
                playlistGrid.CurrentCell = restoredSelection.Cells[columnIndex];
                restoredSelection.Selected = true;
            }
            else
            {
                playlistGrid.ClearSelection();
                playlistGrid.CurrentCell = null;
            }

            DataGridViewRow? restoredFirstVisible = playlistGrid.Rows
                .Cast<DataGridViewRow>()
                .FirstOrDefault(item =>
                    item.Tag is HarnessPlaylistRow row
                    && string.Equals(row.RowId, firstVisibleRowId, StringComparison.Ordinal));
            if (restoredFirstVisible != null)
            {
                playlistGrid.FirstDisplayedScrollingRowIndex = restoredFirstVisible.Index;
            }

            playlistGrid.HorizontalScrollingOffset = horizontalOffset;
            applyAllButton.Enabled = !busy
                && reviewDrafts.Values.Any(item =>
                    item.Action != PlaylistLandingAction.None
                    && !item.IsStale);
            activityLabel.Text = playlistRows.Count + " playlist pairs";
        }

        private void PlaylistGridCellContentClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0)
            {
                return;
            }

            HarnessPlaylistRow? row = playlistGrid.Rows[e.RowIndex].Tag as HarnessPlaylistRow;
            if (row == null)
            {
                return;
            }

            if (playlistGrid.Columns[e.ColumnIndex].Name == "TakeMusicBee")
            {
                SetLandingAction(row, PlaylistLandingAction.TakeMusicBee);
            }
            else if (playlistGrid.Columns[e.ColumnIndex].Name == "TakePhone")
            {
                SetLandingAction(row, PlaylistLandingAction.TakePhone);
            }
        }

        private void SetLandingAction(HarnessPlaylistRow row, PlaylistLandingAction action)
        {
            PlaylistReviewDraft draft = GetOrCreateDraft(row);
            draft.Action = draft.Action == action ? PlaylistLandingAction.None : action;
            draft.IsConfirmed = draft.Action != PlaylistLandingAction.Custom;
            draft.IsDeletion = draft.Action != PlaylistLandingAction.None
                && !row.IsPaired
                && ((row.MusicBeePlaylistId != null
                        && draft.Action == PlaylistLandingAction.TakePhone)
                    || (row.MusicBeePlaylistId == null
                        && draft.Action == PlaylistLandingAction.TakeMusicBee));
            SaveReviewDrafts();
            RenderPlaylistRows();
        }

        private void PlaylistGridCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0)
            {
                return;
            }

            HarnessPlaylistRow? row = playlistGrid.Rows[e.RowIndex].Tag as HarnessPlaylistRow;
            if (row == null || !row.IsPaired || row.Diff == null)
            {
                return;
            }

            selectedPlaylistRow = row;
            GetOrCreateDraft(row);
            RenderMembershipRows();
            ShowWorkspacePage(membershipPage);
        }

        private PlaylistReviewDraft GetOrCreateDraft(HarnessPlaylistRow row)
        {
            if (reviewDrafts.TryGetValue(row.RowId, out PlaylistReviewDraft draft)
                && string.Equals(draft.MusicBeeChecksum, row.MusicBeeChecksum, StringComparison.Ordinal)
                && string.Equals(draft.PhoneChecksum, row.PhoneChecksum, StringComparison.Ordinal))
            {
                draft.IsStale = false;
                return draft;
            }

            draft = PlaylistReviewDraft.Create(row);
            reviewDrafts[row.RowId] = draft;
            SaveReviewDrafts();
            return draft;
        }

        private void LoadReviewDrafts()
        {
            if (reviewDraftStore == null)
            {
                return;
            }

            foreach (PersistedPlaylistReviewDraft persisted in reviewDraftStore.Load())
            {
                PlaylistReviewDraft? draft = PlaylistReviewDraft.FromPersisted(persisted);
                if (draft != null)
                {
                    reviewDrafts[draft.RowId] = draft;
                }
            }
        }

        private void UpdateDraftFreshness()
        {
            foreach (PlaylistReviewDraft draft in reviewDrafts.Values)
            {
                HarnessPlaylistRow? row = playlistRows.FirstOrDefault(item =>
                    string.Equals(item.RowId, draft.RowId, StringComparison.Ordinal));
                draft.IsStale = row == null
                    || !string.Equals(
                        draft.MusicBeeChecksum,
                        row.MusicBeeChecksum,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        draft.PhoneChecksum,
                        row.PhoneChecksum,
                        StringComparison.Ordinal);
            }
        }

        private void SaveReviewDrafts()
        {
            reviewDraftStore?.Save(reviewDrafts.Values.Select(item => item.ToPersisted()));
        }

        private void RenderMembershipRows()
        {
            HarnessPlaylistRow? row = selectedPlaylistRow;
            if (row?.Diff == null)
            {
                return;
            }

            PlaylistReviewDraft draft = GetOrCreateDraft(row);
            membershipGrid.Rows.Clear();
            foreach (PlaylistOccurrence occurrence in row.Diff.Occurrences)
            {
                if (!showMatchingTracks.Checked
                    && occurrence.Membership == OccurrenceMembership.Both)
                {
                    continue;
                }

                bool include = draft.IncludedOccurrenceKeys.Contains(occurrence.Key);
                string marker = occurrence.Membership == OccurrenceMembership.Both
                    ? "="
                    : occurrence.Membership == OccurrenceMembership.MusicBeeOnly
                        ? "+"
                        : "−";
                int index = membershipGrid.Rows.Add(
                    include,
                    occurrence.MusicBeeEntry?.SourceValue ?? string.Empty,
                    marker,
                    occurrence.PhoneEntry?.SourceValue ?? string.Empty);
                membershipGrid.Rows[index].Tag = occurrence;
                bool blocked = occurrence.IsChoiceBlocked(
                    OccurrenceChoice.Include,
                    out string? reason);
                membershipGrid.Rows[index].Cells["Include"].ReadOnly = blocked;
                membershipGrid.Rows[index].Cells["Include"].ToolTipText = reason ?? string.Empty;
            }

            membershipGrid.CellValueChanged -= MembershipGridCellValueChanged;
            membershipGrid.CellValueChanged += MembershipGridCellValueChanged;
            membershipGrid.CurrentCellDirtyStateChanged -= MembershipGridDirtyStateChanged;
            membershipGrid.CurrentCellDirtyStateChanged += MembershipGridDirtyStateChanged;
        }

        private void MembershipGridDirtyStateChanged(object? sender, EventArgs e)
        {
            if (membershipGrid.IsCurrentCellDirty)
            {
                membershipGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void MembershipGridCellValueChanged(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0
                || membershipGrid.Columns[e.ColumnIndex].Name != "Include"
                || selectedPlaylistRow == null)
            {
                return;
            }

            PlaylistOccurrence? occurrence =
                membershipGrid.Rows[e.RowIndex].Tag as PlaylistOccurrence;
            if (occurrence == null)
            {
                return;
            }

            PlaylistReviewDraft draft = GetOrCreateDraft(selectedPlaylistRow);
            bool include = Convert.ToBoolean(
                membershipGrid.Rows[e.RowIndex].Cells["Include"].Value);
            if (include)
            {
                draft.IncludedOccurrenceKeys.Add(occurrence.Key);
            }
            else
            {
                draft.IncludedOccurrenceKeys.Remove(occurrence.Key);
            }

            draft.Action = PlaylistLandingAction.Custom;
            draft.IsConfirmed = false;
            SaveReviewDrafts();
        }

        private void SaveDetailAndReturn()
        {
            ShowWorkspacePage(landingPage);
            RenderPlaylistRows();
        }

        private void ContinueToOrder()
        {
            membershipGrid.EndEdit();
            ShowWorkspacePage(orderPage);
        }

        private void ConfirmDraft()
        {
            if (selectedPlaylistRow == null)
            {
                return;
            }

            PlaylistReviewDraft draft = GetOrCreateDraft(selectedPlaylistRow);
            draft.OrderSide = musicBeeOrder.Checked
                ? PlaylistSide.MusicBee
                : PlaylistSide.Phone;
            draft.Action = PlaylistLandingAction.Custom;
            draft.IsConfirmed = true;
            SaveReviewDrafts();
            ShowWorkspacePage(landingPage);
            RenderPlaylistRows();
        }

        private async Task ApplyAllAsync()
        {
            List<PlaylistReviewDraft> selected = reviewDrafts.Values
                .Where(item => item.Action != PlaylistLandingAction.None
                    && item.IsConfirmed
                    && !item.IsStale)
                .ToList();
            if (selected.Count == 0)
            {
                return;
            }

            int deletes = selected.Count(item => item.IsDeletion);
            DialogResult answer = MessageBox.Show(
                this,
                "Apply " + selected.Count + " reviewed playlist change(s)?"
                    + (deletes == 0
                        ? string.Empty
                        : "\r\n\r\n" + deletes + " playlist deletion(s) are included.")
                    + "\r\n\r\nEach playlist is backed up and verified independently. "
                    + "If a later item fails, earlier successful changes remain applied.",
                "Apply all reviewed changes",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.OK)
            {
                return;
            }

            await RunOperationAsync(
                "Applying reviewed playlist changes…",
                token => Task.Run(() => controller.ApplyAll(selected, token), token),
                result =>
                {
                    MessageBox.Show(
                        this,
                        result.Summary,
                        result.FailedCount == 0
                            ? "Playlist changes complete"
                            : "Playlist changes need attention",
                        MessageBoxButtons.OK,
                        result.FailedCount == 0
                            ? MessageBoxIcon.Information
                            : MessageBoxIcon.Warning);
                    foreach (string rowId in result.SucceededRowIds)
                    {
                        reviewDrafts.Remove(rowId);
                    }

                    SaveReviewDrafts();
                    ApplyCompleted?.Invoke(this, EventArgs.Empty);
                    BeginInvoke(new Action(async () => await RefreshAsync()));
                });
        }

        private void PlaylistGridCellFormatting(
            object? sender,
            DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0)
            {
                return;
            }

            HarnessPlaylistRow? row = playlistGrid.Rows[e.RowIndex].Tag as HarnessPlaylistRow;
            if (row == null)
            {
                return;
            }

            Color tint = row.VisualState == HarnessPlaylistVisualState.Changed
                ? MusicBeeTheme.BlendColours(theme.Surface, theme.Danger, 0.18F)
                : row.VisualState == HarnessPlaylistVisualState.OneSided
                    ? MusicBeeTheme.BlendColours(theme.Surface, theme.Success, 0.17F)
                    : row.VisualState == HarnessPlaylistVisualState.OrderOnly
                        ? MusicBeeTheme.BlendColours(theme.Surface, theme.Accent, 0.14F)
                        : row.VisualState == HarnessPlaylistVisualState.Attention
                            ? MusicBeeTheme.BlendColours(theme.Surface, theme.Danger, 0.18F)
                        : theme.Surface;
            e.CellStyle.BackColor = tint;
        }

        private void MembershipGridCellFormatting(
            object? sender,
            DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0)
            {
                return;
            }

            PlaylistOccurrence? occurrence =
                membershipGrid.Rows[e.RowIndex].Tag as PlaylistOccurrence;
            if (occurrence == null)
            {
                return;
            }

            e.CellStyle.BackColor = occurrence.Membership == OccurrenceMembership.Both
                ? theme.Surface
                : MusicBeeTheme.BlendColours(theme.Surface, theme.Success, 0.15F);
        }

        private async Task EstablishBaselineAsync()
        {
            HarnessPreview? current = preview;
            if (current == null)
            {
                return;
            }

            DialogResult answer = MessageBox.Show(
                this,
                "Accept the displayed matching playlists as the synchronization baseline? "
                    + "This writes only Shmembee's local state.",
                "Accept synchronization baseline",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.OK)
            {
                return;
            }

            await RunOperationAsync(
                "Saving synchronization baseline…",
                token => Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    controller.EstablishBaseline(current);
                    return true;
                }, token),
                ignored =>
                {
                    AddHistory("Baseline accepted");
                    BeginInvoke(new Action(async () => await RefreshAsync()));
                });
        }

        private async Task ApplyAsync()
        {
            HarnessPreview? current = preview;
            if (current?.Reconciliation == null)
            {
                return;
            }

            DialogResult answer = MessageBox.Show(
                this,
                "Apply the reviewed " + DescribeOutcome(current.Reconciliation.Outcome)
                    + " proposal to both playlists?\r\n\r\n"
                    + "Shmembee will re-read, back up, write, and verify both destinations.",
                "Apply synchronization",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.OK)
            {
                return;
            }

            await RunOperationAsync(
                "Applying and verifying synchronization…",
                token => Task.Run(() => controller.Apply(current, token), token),
                result =>
                {
                    AddHistory(result.Status + " — " + result.Details);
                    ApplyCompleted?.Invoke(this, EventArgs.Empty);
                    MessageBox.Show(
                        this,
                        result.Details,
                        result.Status == SynchronizationApplyStatus.Succeeded
                            ? "Synchronization complete"
                            : "Synchronization needs attention",
                        MessageBoxButtons.OK,
                        result.Status == SynchronizationApplyStatus.Succeeded
                            ? MessageBoxIcon.Information
                            : MessageBoxIcon.Warning);
                    BeginInvoke(new Action(async () => await RefreshAsync()));
                });
        }

        private async Task RunOperationAsync<T>(
            string activity,
            Func<CancellationToken, Task<T>> work,
            Action<T> completed)
        {
            operation?.Dispose();
            operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            CancellationToken token = operation.Token;
            SetBusy(true, activity);
            try
            {
                T result = await work(token);
                if (!token.IsCancellationRequested && !IsDisposed)
                {
                    completed(result);
                }
            }
            catch (OperationCanceledException)
            {
                activityLabel.Text = "Operation cancelled";
            }
            catch (Exception exception)
            {
                preview = null;
                summaryDetail.Text = "Shmembee could not complete the operation.";
                AddHistory("Error — " + exception.Message);
                MessageBox.Show(
                    this,
                    exception.Message,
                    "Shmembee",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                if (!IsDisposed)
                {
                    SetBusy(false, "Ready");
                    SetActionState();
                }
            }
        }

        private void RenderPreview(HarnessPreview result)
        {
            Populate(musicBeeList, result.MusicBeeTracks, track => track.MusicBeeUrl);
            Populate(
                phoneList,
                result.PhoneTracks,
                track => track.PhonePath + "  →  " + track.MusicBeeUrl);
            Populate(
                proposalList,
                result.ProposedTracks,
                track => track.MusicBeeUrl + "  →  " + track.PhonePath);
            musicBeeCountValue.Text = result.MusicBeeTracks.Count.ToString();
            phoneCountValue.Text = result.PhoneTracks.Count.ToString();
            if (result.Baseline == null)
            {
                outcomeValue.Text = "Setup required";
                summaryDetail.Text =
                    "Both playlists are connected. Confirm they match, then accept the baseline.";
            }
            else
            {
                ReconciliationResult reconciliation = result.Reconciliation
                    ?? throw new InvalidOperationException(
                        "Synchronization comparison was unavailable.");
                outcomeValue.Text = reconciliation.RequiresReview
                    ? "Review required"
                    : DescribeOutcome(reconciliation.Outcome);
                summaryDetail.Text = reconciliation.RequiresReview
                    ? "Both playlists changed differently. Applying is blocked until reviewed."
                    : reconciliation.Outcome == ReconciliationOutcome.Unchanged
                        ? "MusicBee and phone match the accepted baseline."
                        : "A reviewed synchronization proposal is ready.";
            }

            activityLabel.Text = "Last refreshed " + DateTime.Now.ToString("t");
            SetActionState();
        }

        private static string DescribeOutcome(ReconciliationOutcome outcome)
        {
            switch (outcome)
            {
                case ReconciliationOutcome.MusicBeeOnly:
                    return "MusicBee changes";
                case ReconciliationOutcome.PhoneOnly:
                    return "Phone changes";
                case ReconciliationOutcome.SameChange:
                    return "Matching changes";
                case ReconciliationOutcome.Unchanged:
                    return "Up to date";
                default:
                    return "Review required";
            }
        }

        private void SetBusy(bool value, string activity)
        {
            busy = value;
            activityLabel.Text = activity;
            refreshButton.Enabled = !value;
            playlistGrid.Enabled = !value;
            membershipGrid.Enabled = !value;
            continueButton.Enabled = !value;
            confirmDraftButton.Enabled = !value;
            cancelButton.Enabled = value;
            UseWaitCursor = value;
            SetActionState();
        }

        private void SetActionState()
        {
            baselineButton.Enabled = !busy && preview != null && preview.Baseline == null;
            applyButton.Enabled = !busy
                && preview?.Reconciliation != null
                && PlaylistSyncController.RealApplyEnabled
                && !preview.Reconciliation.RequiresReview
                && preview.Reconciliation.Outcome != ReconciliationOutcome.Unchanged;
            applyAllButton.Enabled = !busy
                && reviewDrafts.Values.Any(item =>
                    item.Action != PlaylistLandingAction.None
                    && item.IsConfirmed
                    && !item.IsStale);
            cancelButton.Enabled = busy;
        }

        private void AddHistory(string entry)
        {
            historyList.Items.Insert(0, DateTime.Now.ToString("g") + "  " + entry);
        }

        private void LoadHistory()
        {
            historyList.Items.Clear();
            foreach (SynchronizationHistoryListItem item in controller.ReadHistory())
            {
                historyList.Items.Add(
                    item.StartedUtc.ToLocalTime().ToString("g")
                        + "  "
                        + item.Status
                        + "  •  "
                        + (item.PlaylistId ?? "unknown playlist")
                        + (string.IsNullOrWhiteSpace(item.Details)
                            ? string.Empty
                            : "  —  " + item.Details));
            }
        }

        private void ApplyThemeDetails()
        {
            BackColor = theme.Background;
            ForeColor = theme.Foreground;
            warningLabel.BackColor = MusicBeeTheme.BlendColours(
                theme.Background,
                theme.Warning,
                0.18F);
            warningLabel.ForeColor = theme.Foreground;
            foreach (Control control in FindTaggedControls(this, "card"))
            {
                control.BackColor = theme.RaisedSurface;
            }

            theme.ApplyDarkTitleBar(this);
        }

        private static IEnumerable<Control> FindTaggedControls(
            Control root,
            string tag)
        {
            foreach (Control child in root.Controls)
            {
                if (string.Equals(child.Tag as string, tag, StringComparison.Ordinal))
                {
                    yield return child;
                }

                foreach (Control descendant in FindTaggedControls(child, tag))
                {
                    yield return descendant;
                }
            }
        }

        private static void Populate(
            ListBox list,
            IReadOnlyList<ResolvedHarnessTrack> tracks,
            Func<ResolvedHarnessTrack, string> display)
        {
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                var totals = tracks
                    .GroupBy(track => track.TrackId)
                    .ToDictionary(group => group.Key, group => group.Count());
                var seen = new Dictionary<string, int>();
                for (int index = 0; index < tracks.Count; index++)
                {
                    ResolvedHarnessTrack track = tracks[index];
                    int occurrence = seen.TryGetValue(track.TrackId, out int count)
                        ? count + 1
                        : 1;
                    seen[track.TrackId] = occurrence;
                    string duplicate = totals[track.TrackId] > 1
                        ? "  • duplicate " + occurrence + "/" + totals[track.TrackId]
                        : string.Empty;
                    list.Items.Add(
                        (index + 1).ToString("D2")
                            + "   "
                            + display(track)
                            + duplicate);
                }
            }
            finally
            {
                list.EndUpdate();
            }
        }

        private void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            SaveReviewDrafts();
            lifetime.Cancel();
            operation?.Cancel();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                operation?.Dispose();
                lifetime.Dispose();
                theme.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    internal enum PlaylistLandingAction
    {
        None,
        TakeMusicBee,
        TakePhone,
        Custom
    }

    internal sealed class PlaylistReviewDraft
    {
        private PlaylistReviewDraft(HarnessPlaylistRow row)
        {
            RowId = row.RowId;
            MusicBeeChecksum = row.MusicBeeChecksum;
            PhoneChecksum = row.PhoneChecksum;
            IncludedOccurrenceKeys = new HashSet<string>(
                row.Diff?.Occurrences
                    .Where(item => item.Membership == OccurrenceMembership.Both)
                    .Select(item => item.Key)
                ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);
            OrderSide = PlaylistSide.MusicBee;
        }

        private PlaylistReviewDraft(PersistedPlaylistReviewDraft persisted)
        {
            RowId = persisted.RowId;
            MusicBeeChecksum = persisted.MusicBeeChecksum;
            PhoneChecksum = persisted.PhoneChecksum;
            IncludedOccurrenceKeys = new HashSet<string>(
                persisted.IncludedOccurrenceKeys,
                StringComparer.Ordinal);
            Action = Enum.TryParse(
                persisted.Action,
                ignoreCase: false,
                out PlaylistLandingAction action)
                ? action
                : PlaylistLandingAction.None;
            OrderSide = Enum.TryParse(
                persisted.OrderSide,
                ignoreCase: false,
                out PlaylistSide orderSide)
                ? orderSide
                : PlaylistSide.MusicBee;
            IsConfirmed = persisted.IsConfirmed;
            IsDeletion = persisted.IsDeletion;
        }

        public string RowId { get; }

        public string MusicBeeChecksum { get; }

        public string PhoneChecksum { get; }

        public PlaylistLandingAction Action { get; set; }

        public HashSet<string> IncludedOccurrenceKeys { get; }

        public PlaylistSide OrderSide { get; set; }

        public bool IsConfirmed { get; set; }

        public bool IsStale { get; set; }

        public bool IsDeletion { get; set; }

        public static PlaylistReviewDraft Create(HarnessPlaylistRow row) =>
            new PlaylistReviewDraft(row);

        public static PlaylistReviewDraft? FromPersisted(
            PersistedPlaylistReviewDraft persisted)
        {
            if (persisted == null
                || string.IsNullOrWhiteSpace(persisted.RowId)
                || string.IsNullOrWhiteSpace(persisted.MusicBeeChecksum)
                || string.IsNullOrWhiteSpace(persisted.PhoneChecksum))
            {
                return null;
            }

            return new PlaylistReviewDraft(persisted);
        }

        public PersistedPlaylistReviewDraft ToPersisted() =>
            new PersistedPlaylistReviewDraft
            {
                RowId = RowId,
                MusicBeePlaylistId = RowId,
                PhonePlaylistId = RowId,
                MusicBeeChecksum = MusicBeeChecksum,
                PhoneChecksum = PhoneChecksum,
                Action = Action.ToString(),
                IncludedOccurrenceKeys = IncludedOccurrenceKeys.ToList(),
                OrderSide = OrderSide.ToString(),
                IsConfirmed = IsConfirmed,
                IsDeletion = IsDeletion
            };
    }
}
