using CitizenFX.Core.UI;

namespace MapEditor.Platform
{
    /// <summary>
    /// The SP build's Compat.Notify. CitizenFX keeps the SHVDN 2 shape (Screen.ShowNotification) rather
    /// than SHVDN 3's Notification.Show, so the editor's calls route through here.
    /// </summary>
    public static class Notifier
    {
        public static void Notify(string message)
        {
            Screen.ShowNotification(message);
        }

        public static void Subtitle(string message, int durationMs)
        {
            Screen.ShowSubtitle(message, durationMs);
        }
    }
}
