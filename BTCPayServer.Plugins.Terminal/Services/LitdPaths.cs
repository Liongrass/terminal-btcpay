namespace BTCPayServer.Plugins.Terminal.Services;

/// <summary>
/// Resolves the files inside litd's data directory that this plugin reads: the TLS certificate and
/// macaroon it needs to open a gRPC connection, and the log file it offers for download.
/// </summary>
/// <remarks>
/// Resolution is re-done on every call rather than cached at startup. litd creates its network
/// sub-directory, macaroon and certificate the first time it runs, so a plugin that resolved these
/// once at boot would keep reporting "not installed" for the entire lifetime of a BTCPay process
/// that happened to start before litd did.
/// </remarks>
public class LitdPaths(TerminalOptions options)
{
    /// <summary>
    /// True when litd's data directory is mounted into this container at all, which is this
    /// plugin's primary "is litd installed here" signal. It can only be true if the generated
    /// fragment is in the deployment's compose file and the stack has been brought up with it.
    /// </summary>
    public bool DataDirectoryMounted => Directory.Exists(options.LitDataDirectory);

    /// <summary>litd's self-signed TLS certificate, or null before litd has ever started.</summary>
    public string? TlsCertificateFile => ExistingFile(Path.Combine(options.LitDataDirectory, "tls.cert"));

    /// <summary>
    /// litd's base macaroon, or null before litd has ever started. Status queries do not need it,
    /// but every session call does.
    /// </summary>
    public string? MacaroonFile =>
        NetworkDirectories().Select(d => ExistingFile(Path.Combine(d, "lit.macaroon"))).FirstOrDefault(f => f is not null);

    /// <summary>
    /// litd's current log file, or null if it has not written one yet. Rotated siblings
    /// (litd.log.1.gz and friends) are deliberately not offered - the current log is what an
    /// operator pasting into a bug report needs.
    /// </summary>
    public string? LogFile =>
        NetworkDirectories(Path.Combine(options.LitDataDirectory, "logs"))
            .Select(d => ExistingFile(Path.Combine(d, "litd.log")))
            .FirstOrDefault(f => f is not null);

    /// <summary>
    /// Candidate <c>&lt;network&gt;</c> sub-directories of <paramref name="root"/>, most likely first.
    /// The configured network is tried first; any other network directory is a fallback for the case
    /// where BTCPay and litd disagree on a network's name, which is better than reporting nothing.
    /// </summary>
    private IEnumerable<string> NetworkDirectories(string? root = null)
    {
        root ??= options.LitDataDirectory;
        if (!Directory.Exists(root))
            yield break;

        var preferred = Path.Combine(root, options.Network);
        if (Directory.Exists(preferred))
            yield return preferred;

        string[] others;
        try
        {
            others = Directory.GetDirectories(root);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var directory in others.Order(StringComparer.Ordinal))
        {
            if (!string.Equals(directory, preferred, StringComparison.Ordinal))
                yield return directory;
        }
    }

    private static string? ExistingFile(string path) => File.Exists(path) ? path : null;
}
