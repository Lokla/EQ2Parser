using EQ2Parser.App.Localization;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.Services;

/// <summary>
/// Trigger shares seen in chat, for non-Windows shells: the same dedupe
/// across mirrored logs (every log in the raid sees the same paste) and the
/// same "already have it" notification as the WPF prompter; a share that
/// is new is handed to the shell through <see cref="Offered"/> instead of
/// opening a WPF window. Raised on log-pump threads — the shell marshals.
/// </summary>
public sealed class SharedTriggerPrompter(SourceManager manager)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _recentMs = new(StringComparer.Ordinal);

    private const long DedupeWindowMs = 10_000;

    /// <summary>A share the user does not have yet (log-pump thread).</summary>
    public event Action<SharedTrigger>? Offered;

    /// <summary>Log-pump entry point (wired per source).</summary>
    public void OnShared(SharedTrigger share)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (_recentMs.TryGetValue(share.Trigger.Key, out var last) && now - last < DedupeWindowMs)
                return; // a mirrored log already surfaced this paste
            if (_recentMs.Count > 64)
            {
                foreach (var stale in _recentMs.Where(kv => now - kv.Value >= DedupeWindowMs).Select(kv => kv.Key).ToList())
                    _recentMs.Remove(stale);
            }
            _recentMs[share.Trigger.Key] = now;
        }
        if (manager.Triggers.Definitions.Any(t => t.Key == share.Trigger.Key))
        {
            manager.AnnounceNotification(Loc.Format("Share_AlreadyHave", share.Sharer));
            return;
        }
        Offered?.Invoke(share);
    }
}
