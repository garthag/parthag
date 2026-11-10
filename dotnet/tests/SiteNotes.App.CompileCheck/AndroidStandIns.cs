// The Android build generates a class called SiteNotes.App.Resource (app resources).
// Declaring a stand-in here makes this check fail the same way if app code ever uses a
// type name that clashes with it.
namespace SiteNotes.App
{
    internal static partial class Resource { }
}

// Android-only MAUI type that shares a common name; mirrors the clash the Android build would hit.
namespace Microsoft.Maui.ApplicationModel
{
    public enum ActivityState { Created, Resumed, Paused, Destroyed, SaveInstanceState, Started, Stopped }
}
