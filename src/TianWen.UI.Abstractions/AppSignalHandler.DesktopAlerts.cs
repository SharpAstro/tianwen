using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    // AppSignalHandler.DesktopAlerts.cs -- the desktop alert level a person chooses on the Notifications tab (#2507).
    public partial class AppSignalHandler
    {
        /// <summary>Wires the desktop alert level: set on <see cref="DesktopAlerts"/>, then saved off the render thread.</summary>
        private void SubscribeDesktopAlerts(SignalBus bus)
        {
            bus.Subscribe<SetDesktopAlertLevelSignal>(sig =>
            {
                if (_appState.DesktopAlerts is not { } alerts)
                {
                    return;
                }
                // The level takes effect at once (the tab redraws with it); the save runs off the render thread.
                alerts.Level = sig.Level;
                _appState.NeedsRedraw = true;
                RunTracked("SaveDesktopAlertLevel", "Could not save the desktop alert setting",
                    ct => DesktopAlertSettings.ChooseAsync(alerts, sig.Level, _external, ct));
            });
        }
    }
}
