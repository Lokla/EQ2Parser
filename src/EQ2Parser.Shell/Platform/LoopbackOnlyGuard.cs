using System.Net;

namespace EQ2Parser.App.Services;

/// <summary>
/// A hard stop for test runs: with <c>EQ2PARSER_LOOPBACK_ONLY=1</c> in the
/// environment, every HTTP request this process makes to a host other than
/// 127.0.0.1 / ::1 / localhost is refused before anything leaves the
/// machine — uploads, the token check, and the trigger-pack and boss-list
/// downloads alike — so a test can only ever talk to a local copy of the
/// site, whatever its settings say.
///
/// How: <see cref="HttpClient.DefaultProxy"/> becomes a proxy that lets
/// loopback through and sends everything else to a closed local port
/// (127.0.0.1:9), where the connection is refused. Every HttpClient built
/// without an explicit proxy reads it — all of the app's do — so the
/// linked upstream services need no change. A refused host is journalled
/// by name (never the path or query).
/// </summary>
public static class LoopbackOnlyGuard
{
    public const string EnvironmentVariable = "EQ2PARSER_LOOPBACK_ONLY";

    public static bool IsRequested =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is "1" or "true";

    /// <summary>Install when requested. Call first thing in Main, before
    /// any HttpClient sends.</summary>
    public static bool InstallIfRequested()
    {
        if (!IsRequested)
            return false;
        Install();
        return true;
    }

    public static void Install()
    {
        HttpClient.DefaultProxy = new LoopbackOnlyProxy();
        SessionJournal.Mark($"{EnvironmentVariable}: requests to anything but loopback are refused");
    }

    internal static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    private sealed class LoopbackOnlyProxy : IWebProxy
    {
        /// <summary>The discard port: nothing listens, so the connection
        /// fails at once and no byte goes further.</summary>
        private static readonly Uri Nowhere = new("http://127.0.0.1:9");

        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination)
        {
            SessionJournal.Mark($"{EnvironmentVariable}: refused a request to {destination.Host}");
            return Nowhere;
        }

        public bool IsBypassed(Uri host) => IsLoopback(host);
    }
}
