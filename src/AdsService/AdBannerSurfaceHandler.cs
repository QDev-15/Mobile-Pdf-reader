using AdsService.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Handlers;
using View = Android.Views.View;
using ViewGroup = Android.Views.ViewGroup;
using FrameLayout = Android.Widget.FrameLayout;
using GravityFlags = Android.Views.GravityFlags;

namespace AdsService;

/// <summary>Maps <see cref="AdBannerSurface"/> to a small stable container that the ONE shared native ad view
/// (whichever provider is configured) gets moved into -- ported from PdfReader's own original handler
/// (2026-10-08), which fixed a case the simpler "handler IS the native ad view" approach (DocScanner's version)
/// does not cover: a page whose Handler is NOT recreated on every visit (cached/reused by the nav stack) never
/// gets a fresh <see cref="CreatePlatformView"/> call, so the shared banner stays behind in whichever page last
/// created one. Giving every page a stable container instead, and explicitly moving the shared view into it via
/// <see cref="AdBannerSurface.Reattach"/> from that page's <c>OnAppearing</c>, covers that case too; a page
/// whose handler IS always freshly created does not need to call it, since <see cref="CreatePlatformView"/>
/// already attaches on creation.</summary>
internal sealed class AdBannerSurfaceHandler : ViewHandler<AdBannerSurface, FrameLayout>
{
    public static readonly IPropertyMapper<AdBannerSurface, AdBannerSurfaceHandler> Mapper =
        new PropertyMapper<AdBannerSurface, AdBannerSurfaceHandler>(ViewHandler.ViewMapper);

    public AdBannerSurfaceHandler() : base(Mapper)
    {
    }

    protected override FrameLayout CreatePlatformView()
    {
        var container = new FrameLayout(Context);
        Attach(container);
        return container;
    }

    /// <summary>Moves the shared native banner into this page's container (it may currently sit in another
    /// page's). No-op if the container does not exist yet (platform view not created); the caller tries again
    /// a moment later, same pattern as any other just-navigated-to native lookup.</summary>
    public void Reattach()
    {
        if (PlatformView is { } container) Attach(container);
    }

    private void Attach(FrameLayout container)
    {
        IAdProvider provider = IPlatformApplication.Current?.Services.GetService<IAdProvider>()
            ?? throw new InvalidOperationException("AdsService: MauiAppBuilder.UseAdsService(...) was never called.");
        View banner = provider.CreateBannerView(Context);
        if (banner.Parent == container) return;
        (banner.Parent as ViewGroup)?.RemoveView(banner);
        container.AddView(banner, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.CenterHorizontal,
        });
    }

    /// <summary>Never tears down the shared native banner just because the page hosting it right now is going
    /// away -- <see cref="Reattach"/> (or the next page's <see cref="CreatePlatformView"/>) moves it into
    /// whichever container is current. Only drops it out of THIS now-dead container so it is free to be
    /// reparented, same defensive cleanup as the original PdfReader handler.</summary>
    protected override void DisconnectHandler(FrameLayout platformView)
    {
        // The container only ever holds the single shared banner (if attached at all) -- just detach whatever
        // is there. Does not ask the provider for the view here: that would create one if none existed yet.
        platformView.RemoveAllViews();
    }
}
