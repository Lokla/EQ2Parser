namespace EQ2Parser.Core.Upload;

/// <summary>
/// Every status line <see cref="UploadQueue"/> shows the user, one method
/// per line. The queue lives in Core, which has no access to the app's
/// translated strings, so it takes its wording from an instance of this
/// class: the bodies here are the English text (the default, and what the
/// Core tests assert), and the app passes a subclass that reads each line
/// from its dictionaries (<c>UploadQ_*</c> keys — one key per method, same
/// arguments in the same order).
///
/// Arguments are inserted as given. A <c>title</c> is the fight's title; a
/// <c>detail</c> or <c>reason</c> is the site's own text about the request
/// (its <c>detail</c>) or the transport's, never translated here. The queue
/// never holds the API token or the signature, so no line can carry one.
/// An override must return a line for every input and must not throw: it
/// runs on the queue's drain.
/// </summary>
public class UploadQueueMessages
{
    /// <summary>The built-in English lines.</summary>
    public static UploadQueueMessages English { get; } = new();

    /// <summary>An upload the site accepted. <paramref name="serverStatus"/>
    /// is the site's status word ("inserted", "skipped", "quarantined").</summary>
    public virtual string Uploaded(string title, string serverStatus, int uploadedThisSession) =>
        $"{title} — Uploaded ({serverStatus}). {uploadedThisSession} uploaded this session.";

    /// <summary>A transient failure (network error or 5xx) about to be retried.</summary>
    public virtual string Retrying(string title, string reason) =>
        $"{title} upload retrying — {reason}";

    /// <summary>An upload given up on: a status that is not retried, or
    /// the attempts ran out.</summary>
    public virtual string Failed(string title, string reason) =>
        $"{title} upload failed: {reason}";

    /// <summary>A refusal the site marked as being about this one upload.</summary>
    public virtual string Refused(string title, string detail) =>
        $"{title} upload refused: {detail}";

    /// <summary>The queue paused on a 401: the token, or the signature made
    /// from it. The state itself is <see cref="UploadQueue.AuthPaused"/> —
    /// code reads that, never this text, which differs per language.</summary>
    public virtual string PausedTokenRejected(int statusCode) =>
        $"uploads paused — token rejected ({statusCode}). Update it and hit Test.";

    /// <summary>The queue paused on an unmarked 403 that carried no text.</summary>
    public virtual string PausedRefused(int statusCode) =>
        $"uploads paused — refused by the site ({statusCode}). Check the token and its account, then hit Test.";

    /// <summary>The queue paused on an unmarked 403; <paramref name="detail"/>
    /// is the site's text with any trailing full stop removed.</summary>
    public virtual string PausedRefusedWithDetail(int statusCode, string detail) =>
        $"uploads paused — refused by the site ({statusCode}): {detail}. Fix that, then hit Test.";

    /// <summary>A fight held back because the site already refused its
    /// server this session.</summary>
    public virtual string SkippedRefusedServer(string title, string server, int skippedThisSession) =>
        $"skipped {title} — the site does not accept uploads from {server} ({skippedThisSession} skipped this session)";

    /// <summary>The drain is waiting out the site's rate limit before the
    /// next attempt at this upload.</summary>
    public virtual string RateLimitWait(string title, int seconds, int attemptsUsed, int attemptsAllowed) =>
        $"{title} upload waiting {seconds} s — the site's upload rate limit was reached (attempt {attemptsUsed} of {attemptsAllowed} used).";

    /// <summary>The payload could not be built; <paramref name="problem"/> is
    /// the exception's message.</summary>
    public virtual string BuildFailed(string title, string problem) =>
        $"couldn't build the upload for {title} ({problem})";

    /// <summary>A queued fight dropped because there is nothing to send it
    /// with; <paramref name="reason"/> is the caller's why (no saved token,
    /// an address the client refuses) with any trailing full stop removed.</summary>
    public virtual string NotConfigured(string title, string reason, int droppedThisSession) =>
        $"{title} not uploaded — {reason} ({droppedThisSession} dropped this session)";
}
