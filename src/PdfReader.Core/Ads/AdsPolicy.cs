namespace PdfReader.Core.Ads;

/// <summary>When the last interstitial was shown (UTC), persisted the same way as DocScanner's export
/// counter: a plain value, no wall-clock arithmetic trusted beyond a single comparison, so it cannot be
/// gamed by anything short of changing the phone's clock backwards.</summary>
public sealed record AdsState(DateTimeOffset? LastShownUtc)
{
    public static readonly AdsState Initial = new((DateTimeOffset?)null);
}

/// <summary>
/// The interstitial rule for PdfReader, adjusted from the owner's original idea ("mỗi 1 tiếng sử dụng" --
/// a hard timer that can interrupt mid-read, risky under AdMob policy) to: at most once per hour, and only
/// actually shown at a natural screen-transition point (closing a file / opening a new one) -- never while
/// a document is open on screen. Pure data + pure logic (no AdMob), mirrors DocScanner.Core.Ads.AdsPolicy;
/// only actually loading/showing an ad talks to Android (AdsService).
/// </summary>
public static class AdsPolicy
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(1);

    /// <summary>A PDF is opened every Nth time straight from another app (a file manager, a chat attachment)
    /// instead of through the Dashboard.</summary>
    public const int ExternalOpensPerInterstitial = 6;

    /// <summary>Counts one PDF opened from another app (the app launches directly into the reader, so there
    /// is no natural transition before it): the first 5 opens are free, the 6th shows an interstitial and
    /// starts the count again. Returns the count to persist. Never shows for Pro.</summary>
    public static (int NextCount, bool ShowInterstitial) AtExternalOpen(int openCount, bool isPro)
    {
        if (isPro) return (openCount, false);
        int next = openCount + 1;
        return next >= ExternalOpensPerInterstitial ? (0, true) : (next, false);
    }

    /// <summary>Call at a natural screen transition (a file is closed, or a new one is about to open).
    /// Returns the state to persist and whether an interstitial should be shown now. Never true when
    /// <paramref name="isPro"/> -- callers do not need to check separately.</summary>
    public static (AdsState Next, bool ShowInterstitial) AtScreenTransition(AdsState state, bool isPro, DateTimeOffset nowUtc)
    {
        if (isPro) return (state, false);
        bool due = state.LastShownUtc is null || nowUtc - state.LastShownUtc.Value >= MinInterval;
        return due ? (new AdsState(nowUtc), true) : (state, false);
    }
}
