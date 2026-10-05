using System;
using System.Runtime.InteropServices;
using PPTimer.Core;

namespace PPTimer.Windows
{
    /// <summary>
    /// COM entry point PowerPoint loads (registered by install.ps1 under
    /// HKCU\Software\Microsoft\Office\PowerPoint\Addins\PPTimer.Connect).
    /// Keep the GUID in sync with scripts/install.ps1.
    /// </summary>
    [ComVisible(true)]
    [Guid("39674B3C-941D-4E54-9410-B0F926196B0B")]
    [ProgId("PPTimer.Connect")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class Connect : IDTExtensibility2
    {
        AddinHost host;

        public void OnConnection(object application, int connectMode, object addInInst, ref Array custom)
        {
            try
            {
                host = new AddinHost();
                host.Start(application);
            }
            catch (Exception ex)
            {
                // Never let an exception escape into PowerPoint, or Office disables the add-in.
                Log.Error("Startup failed", ex);
            }
        }

        public void OnDisconnection(int removeMode, ref Array custom)
        {
            try
            {
                host?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("Shutdown failed", ex);
            }
            host = null;
        }

        public void OnAddInsUpdate(ref Array custom) { }

        public void OnStartupComplete(ref Array custom) { }

        public void OnBeginShutdown(ref Array custom) { }
    }
}
