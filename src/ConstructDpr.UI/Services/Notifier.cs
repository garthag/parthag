using ConstructDpr.Shared.Scheduling;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.UI.Services;

public sealed record Toast(int Id, string Message, string Kind);

/// <summary>Short messages at the bottom of the screen, and one place that turns errors into plain words.</summary>
public sealed class Notifier
{
    private int _next;
    public List<Toast> Toasts { get; } = new();
    public event Action? Changed;
    /// <summary>Raised when the server no longer accepts the sign-in.</summary>
    public event Action? SignInExpired;

    public void Show(string message, string kind = "good")
    {
        var t = new Toast(++_next, message, kind);
        Toasts.Add(t);
        Changed?.Invoke();
        _ = Task.Delay(kind == "bad" ? 9000 : 3500).ContinueWith(_ => { Toasts.Remove(t); Changed?.Invoke(); });
    }

    public void Dismiss(Toast t)
    {
        Toasts.Remove(t);
        Changed?.Invoke();
    }

    /// <summary>Runs an action; rule violations, conflicts and connection problems become a message instead of a crash.</summary>
    public async Task<bool> TryAsync(Func<Task> work, string? success = null)
    {
        try
        {
            await work();
            if (success is not null) Show(success);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            Show(ex.Message, "bad");
            SignInExpired?.Invoke();
        }
        catch (Exception ex) when (ex is WorkflowException or ScheduleException or ConflictException or ForbiddenException or NotFoundException or HttpRequestException or ArgumentException)
        {
            Show(ex.Message, "bad");
        }
        return false;
    }
}
