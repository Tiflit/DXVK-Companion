using System.Windows.Forms;

namespace DXVKCompanion.UI
{
    public class UpdateNotification
    {
        public static void Show(NotifyIcon tray, string version)
        {
            tray.ShowBalloonTip(
                4000,
                "DXVK Update Available",
                $"A new DXVK version ({version}) is available.",
                ToolTipIcon.Info
            );
        }
    }
}
