#if SCREENSHOTS
using ConstructDpr.Mobile.Pages;
using ConstructDpr.Mobile.Services;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.Mobile;

/// <summary>
/// Test builds only (compiled with SCREENSHOTS, never in the released app): signs in to a test server, loads the demo
/// project, opens each screen and saves a PNG of it, so the build pipeline can publish a preview of the Android app.
/// </summary>
public static class ScreenshotMode
{
    public sealed record Settings(string Server, string Email, string Password, string Folder);

    public static Settings? Options { get; set; }

    private static int _started;

    public static async Task RunAsync(Window window, Settings o)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return; // Android may create the window twice; run once
        var dir = Path.Combine(o.Folder, "screens");
        Directory.CreateDirectory(dir);
        try
        {
            await AppState.Session.SetServerAsync(o.Server);
            try { await AppState.Session.RegisterAsync(o.Email, o.Password, "Site Engineer"); }
            catch (Exception ex) when (ex is WorkflowException or ConflictException) { /* already registered */ }
            await AppState.Session.SignInAsync(o.Email, o.Password);
            await AppState.StartAsync();
            if (AppState.Projects.Count == 0)
            {
                var p = await AppState.Api.CreateDemoProjectAsync();
                await AppState.RefreshProjectsAsync();
                await AppState.OpenProjectAsync(p.Id);
            }
            var shell = new AppShell();
            window.Page = shell;
            async Task Shot(string name)
            {
                await Task.Delay(3500);
                var shot = await Screenshot.Default.CaptureAsync();
                await using var src = await shot.OpenReadAsync(ScreenshotFormat.Png);
                await using var dst = File.Create(Path.Combine(dir, name + ".png"));
                await src.CopyToAsync(dst);
            }
            await Shot("android-01-login-done-home");
            var dprs = await AppState.Api.DprsAsync(AppState.Pid);
            await Shell.Current.GoToAsync("//reports");
            await Shot("android-02-reports");
            if (dprs.FirstOrDefault(d => d.Status == DprStatus.Submitted) is { } pending)
            {
                await Shell.Current.GoToAsync("//approve");
                await Shot("android-03-approvals");
                await Shell.Current.Navigation.PushAsync(new DprPage(pending.Id));
                await Shot("android-04-report-review");
                await Shell.Current.Navigation.PopAsync();
            }
            var draft = await AppState.Api.CreateDprAsync(AppState.Pid, AppState.Today, Shift.Night);
            await Shell.Current.GoToAsync("//reports");
            await Shell.Current.Navigation.PushAsync(new DprPage(draft.Id));
            await Shot("android-05-new-report");
            await Shell.Current.Navigation.PopAsync();
            await Shell.Current.GoToAsync("//schedule");
            await Shot("android-06-schedule");
            await Shell.Current.GoToAsync("//more");
            await Shot("android-07-more");
            await File.WriteAllTextAsync(Path.Combine(dir, "done.txt"), "ok");
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "error.txt"), ex.ToString());
        }
    }
}
#endif
