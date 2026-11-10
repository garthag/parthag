// The Android build generates a class called SiteNotes.App.Resource (app resources).
// Declaring a stand-in here makes this check fail the same way if app code ever uses a
// type name that clashes with it.
namespace SiteNotes.App
{
    internal static partial class Resource { }
}
