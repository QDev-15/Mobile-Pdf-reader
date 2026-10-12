namespace AdsService;

/// <summary>Base for a provider's connection details (ad unit IDs, keys, test-mode flag...). Each provider
/// project (AdsService.AdMob / AdsService.AppLovin / AdsService.LevelPlay) defines its own sealed record
/// deriving from this -- AdMobOptions / AppLovinOptions / LevelPlayOptions -- and a matching UseXxxAds
/// extension that is the only thing a consuming app calls. Switching ad networks means referencing a
/// different provider project and calling its UseXxxAds instead; nothing else in the app changes.</summary>
public abstract record AdsProviderOptions
{
    /// <summary>MASTER switch for ads in this build -- true by default, applies to every build configuration (Debug,
    /// Release, APK, AAB). When false, the matching UseXxxAds registers a no-op provider instead of the real one: the
    /// ad SDK is never initialised, nothing is loaded or shown, and the banner never appears. Distinct from the runtime
    /// <see cref="IAdsClient.AreAdsEnabled"/> (the app's own ad-free condition, e.g. Pro); both must be true for ads to
    /// show. Set it with an object initialiser: <c>new LevelPlayOptions(...) { IsAdsEnabled = false }</c>.</summary>
    public bool IsAdsEnabled { get; init; } = true;
}
