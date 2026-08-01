using System;
using System.IO;
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
        private ShmembeeForm? shmembeeForm;

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
                    "mnuTools/Shmembee: Open playlist sync",
                    string.Empty,
                    OpenShmembee);
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
            if (shmembeeForm != null && !shmembeeForm.IsDisposed)
            {
                shmembeeForm.Close();
                shmembeeForm.Dispose();
            }

            shmembeeForm = null;
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

        private void OpenShmembee(object sender, EventArgs eventArgs)
        {
            if (string.IsNullOrEmpty(pluginStoragePath))
            {
                ShowError("MusicBee did not provide a persistent storage path.");
                return;
            }

            try
            {
                if (shmembeeForm != null && !shmembeeForm.IsDisposed)
                {
                    if (shmembeeForm.WindowState == FormWindowState.Minimized)
                    {
                        shmembeeForm.WindowState = FormWindowState.Normal;
                    }

                    shmembeeForm.Show();
                    shmembeeForm.Activate();
                    shmembeeForm.BringToFront();
                    return;
                }

                string storagePath = pluginStoragePath
                    ?? throw new InvalidOperationException(
                        "MusicBee did not provide a persistent storage path.");
                shmembeeForm = new ShmembeeForm(
                    new PlaylistSyncController(
                        mbApiInterface,
                        storagePath),
                    MusicBeeTheme.FromApi(mbApiInterface),
                    mbApiInterface,
                    storagePath);
                shmembeeForm.FormClosed += (_, _) =>
                {
                    shmembeeForm?.Dispose();
                    shmembeeForm = null;
                };
                IntPtr ownerHandle = mbApiInterface.MB_GetWindowHandle == null
                    ? IntPtr.Zero
                    : mbApiInterface.MB_GetWindowHandle();
                if (ownerHandle == IntPtr.Zero)
                {
                    shmembeeForm.Show();
                }
                else
                {
                    shmembeeForm.Show(new NativeWindowOwner(ownerHandle));
                }
            }
            catch (Exception exception)
            {
                ShowError(exception.Message);
            }
        }

        private sealed class NativeWindowOwner : IWin32Window
        {
            public NativeWindowOwner(IntPtr handle)
            {
                Handle = handle;
            }

            public IntPtr Handle { get; }
        }

        private static void ShowError(string message)
        {
            MessageBox.Show(
                message,
                "Shmembee",
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
