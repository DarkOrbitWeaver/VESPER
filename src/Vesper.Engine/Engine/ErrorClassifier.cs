using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Vesper.Engine;

/// <summary>Maps exceptions to the three questions the engine asks: retry? wait for network? local problem?</summary>
internal static class ErrorClassifier
{
    private const int ErrorHandleDiskFull = 0x27;
    private const int ErrorDiskFull = 0x70;

    public static bool IsDiskFull(Exception ex) =>
        ex is IOException io && ((io.HResult & 0xFFFF) is ErrorHandleDiskFull or ErrorDiskFull);

    public static bool IsBlocked(Exception ex) => Chain(ex).Any(e => e is BlockedAddressException);

    /// <summary>True for failures where waiting for connectivity makes more sense than backing off.</summary>
    public static bool IsNetworkDown(Exception ex)
    {
        foreach (Exception e in Chain(ex))
        {
            if (e is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NetworkUnreachable
                or SocketError.NetworkDown or SocketError.HostUnreachable or SocketError.TryAgain or SocketError.NoData })
                return true;
        }
        return !NetworkInterface.GetIsNetworkAvailable();
    }

    /// <summary>
    /// Network-level failures that a later attempt may cure. Call only for exceptions that did
    /// NOT come from local storage; <see cref="LocalStorageException"/> is handled first by callers.
    /// </summary>
    public static bool IsTransient(Exception ex) =>
        !IsBlocked(ex) && !Chain(ex).Any(e => e is RedirectException)
        && Chain(ex).Any(e => e is HttpRequestException or IOException or SocketException or TransientNetworkException or TimeoutException);

    public static string Describe(Exception ex)
    {
        Exception root = Chain(ex).Last();
        return root is SocketException s ? $"{s.SocketErrorCode}" : root.Message;
    }

    private static IEnumerable<Exception> Chain(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException) yield return e;
    }
}
