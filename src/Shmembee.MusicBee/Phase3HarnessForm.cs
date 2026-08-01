namespace MusicBeePlugin
{
    // Compatibility shim for the current plugin entry point. The production
    // surface is ShmembeeForm; Plugin.cs can move to it without a flag day.
    internal sealed class Phase3HarnessForm : ShmembeeForm
    {
        public Phase3HarnessForm(PlaylistSyncController controller)
            : base(controller)
        {
        }
    }
}
