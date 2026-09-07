using System.Windows;
using NdiMerger.Core.Models;
using NdiMerger.Sources;

namespace NdiMerger.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var session = SessionStore.TryLoad();
        NdiAdapterBinding.ApplyFromSession(session?.Layout.NdiAdapterId, session?.Layout.NdiAdapterIp);
        base.OnStartup(e);
    }
}
