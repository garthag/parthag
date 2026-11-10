using SiteNotes.Core.Sync;

namespace SiteNotes.App.Services;

/// <summary>Relays sync status changes (from any thread) to the UI.</summary>
public static class SyncBadge
{
    public static event Action? Changed;

    private static bool _attached;

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;
        void Hook()
        {
            if (AppServices.Sync == null) return;
            AppServices.Sync.StatusChanged += () => MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
            Changed?.Invoke();
        }
        Hook();
        AppServices.SyncReplaced += Hook;
    }

    public static string Text(SyncState s) => s switch
    {
        SyncState.Online => "● Live",
        SyncState.Syncing => "⟳ Syncing",
        SyncState.Connecting => "⟳ Connecting",
        SyncState.Offline => "○ Offline",
        SyncState.Error => "⚠ Sync error",
        _ => "○ Local only",
    };
}
