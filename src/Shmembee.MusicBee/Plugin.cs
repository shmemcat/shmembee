using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    /// <summary>
    /// Minimal host contract used to prove discovery and lifecycle behavior.
    /// </summary>
    public partial class Plugin
    {
        private readonly PluginInfo about = new PluginInfo();
        private MusicBeeApiInterface mbApiInterface;
        private string? logPath;
        private string? pluginStoragePath;

        private const string ProofPlaylistName = "Shmembee Missing Track Proof";
        private const string ProofCanonicalUrlFileName = "missing-track-proof.url";

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);

            about.PluginInfoVersion = PluginInfoVersion;
            about.Type = PluginType.General;
            about.Name = "Shmembee";
            about.Description = "Safe MusicBee and GoneMAD playlist reconciliation.";
            about.Author = "shmemcat";
            about.TargetApplication = string.Empty;
            about.VersionMajor = 0;
            about.VersionMinor = 1;
            about.Revision = 0;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = ReceiveNotificationFlags.StartupOnly;
            about.ConfigurationPanelHeight = 0;

            string storagePath = mbApiInterface.Setting_GetPersistentStoragePath == null
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MusicBee")
                : mbApiInterface.Setting_GetPersistentStoragePath();
            pluginStoragePath = Path.Combine(storagePath, "Shmembee");
            Directory.CreateDirectory(pluginStoragePath);
            logPath = Path.Combine(pluginStoragePath, "lifecycle.log");
            WriteLifecycleEvent("Initialise", "API revision " + mbApiInterface.ApiRevision);
            if (mbApiInterface.MB_AddMenuItem != null)
            {
                mbApiInterface.MB_AddMenuItem(
                    "mnuTools/Shmembee: Create missing-track proof M3U",
                    string.Empty,
                    CreateMissingTrackProof);
                mbApiInterface.MB_AddMenuItem(
                    "mnuTools/Shmembee: Repair missing-track proof playlist",
                    string.Empty,
                    RepairMissingTrackProof);
                mbApiInterface.MB_AddMenuItem(
                    "mnuTools/Shmembee: Open Phase 3 test harness",
                    string.Empty,
                    OpenPhase3Harness);
            }

            return about;
        }

        public bool Configure(IntPtr panelHandle)
        {
            WriteLifecycleEvent("Configure", "Preferences opened");
            return false;
        }

        public void SaveSettings()
        {
            WriteLifecycleEvent("SaveSettings", "No settings available");
        }

        public void Close(PluginCloseReason reason)
        {
            WriteLifecycleEvent("Close", reason.ToString());
        }

        public void Uninstall()
        {
            WriteLifecycleEvent("Uninstall", "Plugin disabled or removed");
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            WriteLifecycleEvent("Notification", type.ToString());
        }

        private void CreateMissingTrackProof(object sender, EventArgs eventArgs)
        {
            string canonicalUrl = mbApiInterface.NowPlaying_GetFileUrl();
            if (string.IsNullOrWhiteSpace(canonicalUrl) || !File.Exists(canonicalUrl))
            {
                MessageBox.Show(
                    "Play a local library track, then run this command again.",
                    "Shmembee proof",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (string.IsNullOrEmpty(pluginStoragePath))
            {
                ShowProofError("MusicBee did not provide a persistent storage path.");
                return;
            }

            string fixturePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                ProofPlaylistName + ".m3u8");
            string deliberatelyMissingPath = "Music/ShmembeeFixture/" + Path.GetFileName(canonicalUrl);
            File.WriteAllText(
                fixturePath,
                "#EXTM3U" + Environment.NewLine + deliberatelyMissingPath + Environment.NewLine,
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(pluginStoragePath, ProofCanonicalUrlFileName),
                canonicalUrl,
                new UTF8Encoding(false));

            WriteLifecycleEvent("ProofCreated", deliberatelyMissingPath + " => " + canonicalUrl);
            MessageBox.Show(
                "Created:\n" + fixturePath + "\n\n"
                    + "Import this M3U8 into MusicBee and confirm its only entry is missing. "
                    + "Then run Tools > Shmembee: Repair missing-track proof playlist.",
                "Shmembee proof",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void RepairMissingTrackProof(object sender, EventArgs eventArgs)
        {
            if (string.IsNullOrEmpty(pluginStoragePath))
            {
                ShowProofError("MusicBee did not provide a persistent storage path.");
                return;
            }

            string canonicalUrlPath = Path.Combine(pluginStoragePath, ProofCanonicalUrlFileName);
            if (!File.Exists(canonicalUrlPath))
            {
                ShowProofError("Create the proof M3U before attempting repair.");
                return;
            }

            string canonicalUrl = File.ReadAllText(canonicalUrlPath).Trim();
            if (!File.Exists(canonicalUrl))
            {
                ShowProofError("The remembered canonical library track no longer exists.");
                return;
            }

            string? proofPlaylistUrl = FindPlaylistUrl(ProofPlaylistName);
            if (proofPlaylistUrl == null)
            {
                ShowProofError(
                    "MusicBee has no playlist named \"" + ProofPlaylistName
                        + "\". Import the generated M3U8 first.");
                return;
            }

            if (!mbApiInterface.Playlist_SetFiles(proofPlaylistUrl, new[] { canonicalUrl }))
            {
                ShowProofError("MusicBee rejected Playlist_SetFiles for the proof playlist.");
                return;
            }

            string[] verifiedFiles;
            bool verified = mbApiInterface.Playlist_QueryFilesEx(proofPlaylistUrl, out verifiedFiles)
                && verifiedFiles.Length == 1
                && string.Equals(verifiedFiles[0], canonicalUrl, StringComparison.OrdinalIgnoreCase);

            WriteLifecycleEvent(
                "ProofRepaired",
                verified ? proofPlaylistUrl + " => " + canonicalUrl : "Verification failed");
            MessageBox.Show(
                verified
                    ? "MusicBee accepted the canonical library URL. Open the proof playlist and "
                        + "confirm the track is recognized and playable."
                    : "MusicBee accepted the write, but re-reading the playlist did not return "
                        + "the expected canonical URL.",
                "Shmembee proof",
                MessageBoxButtons.OK,
                verified ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void OpenPhase3Harness(object sender, EventArgs eventArgs)
        {
            if (string.IsNullOrEmpty(pluginStoragePath))
            {
                ShowProofError("MusicBee did not provide a persistent storage path.");
                return;
            }

            try
            {
                string storagePath = pluginStoragePath
                    ?? throw new InvalidOperationException(
                        "MusicBee did not provide a persistent storage path.");
                using (var form = new Phase3HarnessForm(
                    new Phase3HarnessController(
                        mbApiInterface,
                        storagePath)))
                {
                    form.ShowDialog();
                }
            }
            catch (Exception exception)
            {
                ShowProofError(exception.Message);
            }
        }

        private string? FindPlaylistUrl(string playlistName)
        {
            if (!mbApiInterface.Playlist_QueryPlaylists())
            {
                return null;
            }

            string playlistUrl;
            while (!string.IsNullOrEmpty(playlistUrl = mbApiInterface.Playlist_QueryGetNextPlaylist()))
            {
                if (string.Equals(
                    mbApiInterface.Playlist_GetName(playlistUrl),
                    playlistName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return playlistUrl;
                }
            }

            return null;
        }

        private static void ShowProofError(string message)
        {
            MessageBox.Show(
                message,
                "Shmembee proof",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        private void WriteLifecycleEvent(string eventName, string detail)
        {
            if (string.IsNullOrEmpty(logPath))
            {
                return;
            }

            try
            {
                File.AppendAllText(
                    logPath,
                    string.Format(
                        "{0:O}\t{1}\t{2}{3}",
                        DateTimeOffset.UtcNow,
                        eventName,
                        detail,
                        Environment.NewLine));
            }
            catch (IOException)
            {
                // Diagnostics must never prevent MusicBee from loading or closing.
            }
            catch (UnauthorizedAccessException)
            {
                // Diagnostics must never prevent MusicBee from loading or closing.
            }
        }
    }
}
