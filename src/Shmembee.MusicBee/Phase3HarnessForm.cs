using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Shmembee.Application.Synchronization;
using Shmembee.Core.Reconciliation;

namespace MusicBeePlugin
{
    internal sealed class Phase3HarnessForm : Form
    {
        private readonly Phase3HarnessController controller;
        private readonly Label statusLabel = new Label();
        private readonly ListBox musicBeeList = new ListBox();
        private readonly ListBox phoneList = new ListBox();
        private readonly ListBox proposalList = new ListBox();
        private readonly Button refreshButton = new Button();
        private readonly Button baselineButton = new Button();
        private readonly Button applyButton = new Button();
        private HarnessPreview? preview;

        public Phase3HarnessForm(Phase3HarnessController controller)
        {
            this.controller = controller;
            Text = "Shmembee Phase 3 Test Harness";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(900, 560);
            Size = new Size(1100, 680);
            BuildLayout();
            RefreshPreview();
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 6,
                Padding = new Padding(12)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            statusLabel.AutoSize = true;
            statusLabel.Padding = new Padding(0, 0, 0, 8);
            root.Controls.Add(statusLabel, 0, 0);
            root.SetColumnSpan(statusLabel, 2);
            root.Controls.Add(new Label
            {
                Text = "MusicBee (canonical URLs)",
                AutoSize = true
            }, 0, 1);
            root.Controls.Add(new Label
            {
                Text = "GoneMAD phone M3U (resolved paths)",
                AutoSize = true
            }, 1, 1);

            musicBeeList.Dock = DockStyle.Fill;
            phoneList.Dock = DockStyle.Fill;
            musicBeeList.HorizontalScrollbar = true;
            phoneList.HorizontalScrollbar = true;
            root.Controls.Add(musicBeeList, 0, 2);
            root.Controls.Add(phoneList, 1, 2);
            root.Controls.Add(new Label
            {
                Text = "Reviewed proposed result (ordered canonical URLs)",
                AutoSize = true,
                Padding = new Padding(0, 8, 0, 0)
            }, 0, 3);
            root.SetColumnSpan(root.GetControlFromPosition(0, 3), 2);
            proposalList.Dock = DockStyle.Fill;
            proposalList.HorizontalScrollbar = true;
            root.Controls.Add(proposalList, 0, 4);
            root.SetColumnSpan(proposalList, 2);

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 8, 0, 0)
            };
            refreshButton.Text = "Refresh";
            baselineButton.Text = "Establish baseline";
            applyButton.Text = "Apply reviewed proposal";
            refreshButton.AutoSize = true;
            baselineButton.AutoSize = true;
            applyButton.AutoSize = true;
            refreshButton.Click += (_, _) => RefreshPreview();
            baselineButton.Click += (_, _) => EstablishBaseline();
            applyButton.Click += (_, _) => ApplyProposal();
            buttons.Controls.Add(applyButton);
            buttons.Controls.Add(baselineButton);
            buttons.Controls.Add(refreshButton);
            root.Controls.Add(buttons, 0, 5);
            root.SetColumnSpan(buttons, 2);
            Controls.Add(root);
        }

        private void RefreshPreview()
        {
            try
            {
                UseWaitCursor = true;
                preview = controller.Refresh();
                Populate(
                    musicBeeList,
                    preview.MusicBeeTracks,
                    track => track.MusicBeeUrl);
                Populate(
                    phoneList,
                    preview.PhoneTracks,
                    track => track.PhonePath + "  =>  " + track.MusicBeeUrl);
                Populate(
                    proposalList,
                    preview.ProposedTracks,
                    track => track.MusicBeeUrl + "  =>  " + track.PhonePath);
                if (preview.Baseline == null)
                {
                    statusLabel.Text =
                        "No accepted baseline. Verify both ordered lists are identical, "
                        + "then establish the disposable baseline.\nMusicBee checksum: "
                        + preview.MusicBeeState.Checksum
                        + "\nPhone checksum: "
                        + preview.PhoneState.Checksum;
                    baselineButton.Enabled = true;
                    applyButton.Enabled = false;
                }
                else
                {
                    ReconciliationResult result = preview.Reconciliation
                        ?? throw new InvalidOperationException(
                            "Baseline exists but reconciliation was unavailable.");
                    string outcomeDetails = result.RequiresReview
                        ? " — blocked: concurrent changes require review."
                        : result.Outcome == ReconciliationOutcome.Unchanged
                            ? " — both sides match the accepted baseline; no apply is needed."
                            : " — proposal is eligible for explicit apply.";
                    statusLabel.Text = "Outcome: "
                        + result.Outcome
                        + outcomeDetails
                        + "\nMusicBee checksum: "
                        + preview.MusicBeeState.Checksum
                        + "\nPhone checksum: "
                        + preview.PhoneState.Checksum;
                    baselineButton.Enabled = false;
                    applyButton.Enabled = Phase3HarnessController.RealApplyEnabled
                        && !result.RequiresReview
                        && result.Outcome != ReconciliationOutcome.Unchanged;
                    if (!Phase3HarnessController.RealApplyEnabled
                        && result.Outcome != ReconciliationOutcome.Unchanged)
                    {
                        statusLabel.Text +=
                            "\nReal apply disabled: Windows Shell MTP replacement "
                            + "failed device recovery testing.";
                    }
                }
            }
            catch (Exception exception)
            {
                preview = null;
                statusLabel.Text = "Refresh failed: " + exception.Message;
                baselineButton.Enabled = false;
                applyButton.Enabled = false;
                MessageBox.Show(
                    this,
                    exception.Message,
                    "Shmembee Phase 3 harness",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        private void EstablishBaseline()
        {
            if (preview == null)
            {
                return;
            }

            DialogResult confirmation = MessageBox.Show(
                this,
                "Establish the currently displayed identical sequences as the accepted "
                    + "common baseline? This writes only Shmembee's local SQLite state.",
                "Establish disposable baseline",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);
            if (confirmation != DialogResult.OK)
            {
                return;
            }

            try
            {
                controller.EstablishBaseline(preview);
                RefreshPreview();
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
        }

        private void ApplyProposal()
        {
            if (preview?.Reconciliation == null)
            {
                return;
            }

            DialogResult confirmation = MessageBox.Show(
                this,
                "Apply the " + preview.Reconciliation.Outcome + " proposal to BOTH the "
                    + "MusicBee playlist and MLE S24U\\Internal storage\\gmmp\\playlists\\"
                    + Phase3HarnessController.PhoneBackingName
                    + "?\n\nOnly the exact disposable playlist name is allowed. Both "
                    + "inputs will be re-read, backed up, written, and verified.",
                "Apply disposable synchronization",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);
            if (confirmation != DialogResult.OK)
            {
                return;
            }

            try
            {
                UseWaitCursor = true;
                var result = controller.Apply(preview, CancellationToken.None);
                MessageBox.Show(
                    this,
                    result.Status + ": " + result.Details,
                    "Shmembee Phase 3 harness",
                    MessageBoxButtons.OK,
                    result.Status == SynchronizationApplyStatus.Succeeded
                        ? MessageBoxIcon.Information
                        : MessageBoxIcon.Warning);
                RefreshPreview();
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
            finally
            {
                UseWaitCursor = false;
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
                        ? " [duplicate " + occurrence + "/" + totals[track.TrackId] + "]"
                        : string.Empty;
                    list.Items.Add(
                        (index + 1).ToString("D2")
                            + ". "
                            + display(track)
                            + duplicate);
                }
            }
            finally
            {
                list.EndUpdate();
            }
        }

        private void ShowError(Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Shmembee Phase 3 harness",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            RefreshPreview();
        }
    }
}
