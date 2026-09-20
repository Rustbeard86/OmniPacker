namespace SteamForge.Engine.Jobs;

/// <summary>
/// Live fan-out of job updates to the admin UI. The pipeline notifies on every
/// status/progress change; the queue page subscribes to refresh in real time.
/// </summary>
public sealed class JobNotifier
{
    /// <summary>Raised for every job update. Handlers must not throw.</summary>
    public event Action<JobSnapshot>? Updated;

    /// <summary>Raised when a job is removed; carries the removed job id.</summary>
    public event Action<string>? Removed;

    public void Notify(JobSnapshot snapshot)
    {
        foreach (var handler in Updated?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<JobSnapshot>)handler)(snapshot);
            }
            catch
            {
                // A subscriber must never break the fan-out.
            }
        }
    }

    public void NotifyRemoved(string jobId)
    {
        foreach (var handler in Removed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<string>)handler)(jobId);
            }
            catch
            {
                // A subscriber must never break the fan-out.
            }
        }
    }
}
