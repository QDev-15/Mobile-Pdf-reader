namespace AdsService.Internal;

/// <summary>The one <see cref="IAdsClient"/> implementation, registered as a singleton by
/// <c>UseAdsService</c>. Pure forwarding to whichever <see cref="IAdProvider"/> was built for the
/// <see cref="AdsProviderOptions"/> passed in -- see that method for how the provider is chosen.</summary>
internal sealed class AdsClient(IAdProvider provider) : IAdsClient
{
    public bool AreAdsEnabled { get; set; } = true;

    public bool IsBannerLoaded => provider.IsBannerLoaded;

    public event Action? BannerStateChanged
    {
        add => provider.BannerStateChanged += value;
        remove => provider.BannerStateChanged -= value;
    }

    public bool IsInterstitialReady => provider.IsInterstitialReady;

    public void PrepareInterstitial() => provider.PrepareInterstitial();

    public void ShowInterstitialIfReady()
    {
        if (AreAdsEnabled) provider.ShowInterstitialIfReady();
    }
}
