namespace Skua.Core.Headless.Net;

/// <summary>How a bank load got (or did not get) the bank.</summary>
public enum BankLoadMethod
{
    /// <summary>The game's way: <c>POST api/char/bank</c> with the login token (<see cref="AqwWebApi.GetBankAsync"/>).</summary>
    Http,

    /// <summary>The socket request <c>loadBank All</c> (World.sendLoadBankRequest), after the HTTP load failed.</summary>
    Socket,

    /// <summary>Already loaded on this connection; nothing was requested (the game's <c>myAvatar.bank != null</c>).</summary>
    AlreadyLoaded,

    /// <summary>Nothing loaded it.</summary>
    None,
}

/// <summary>The outcome of <see cref="AqwSession.LoadBankAsync"/>. Carries no token and no reply body.</summary>
/// <param name="Method">What loaded the bank (<see cref="BankLoadMethod.None"/> when nothing did).</param>
/// <param name="Loaded">The bank is loaded on this connection.</param>
/// <param name="ItemCount">Items in the bank afterwards.</param>
/// <param name="HttpStatus">The HTTP status of the <c>api/char/bank</c> reply, when one arrived.</param>
/// <param name="Elapsed">The whole load, fallback included.</param>
/// <param name="Failure">Why the HTTP load (and the fallback, if tried) failed; null on an HTTP success.</param>
public sealed record BankLoadResult(BankLoadMethod Method, bool Loaded, int ItemCount, int? HttpStatus, TimeSpan Elapsed, string? Failure);
