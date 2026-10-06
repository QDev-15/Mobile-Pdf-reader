using PdfReader.Core.Licensing;
using Plugin.InAppBilling;

namespace PdfReader.Services;

/// <summary>
/// <see cref="ILicenseService"/> backed by Google Play Billing (managed, non-consumable product), via
/// the <c>Plugin.InAppBilling</c> library (MIT). Adapted from DocScanner's LicenseService
/// (Mobile-doc-scanner repo): same Play Billing pattern, but no free-use trial counter -- PdfReader's
/// Pro unlock is just "removes ads" (plan section 6), so there is nothing to spend down.
/// </summary>
public sealed class LicenseService : ILicenseService
{
    /// <summary>Product id of the "remove ads" unlock. TODO: replace with the real product id once
    /// created in Play Console &gt; Monetize &gt; Products &gt; In-app products.</summary>
    public const string ProProductId = "pdfreader_pro_remove_ads";

    private const string CachedIsProKey = "license_is_pro_cached";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LicenseService()
    {
        State = new LicenseState(Preferences.Default.Get(CachedIsProKey, false));
    }

    public LicenseState State { get; private set; }
    public string? ProPriceText { get; private set; }
    public string? LastError { get; private set; }
    public event Action? Changed;

    public async Task RefreshAsync()
    {
        await LockedRefreshAsync();
        Changed?.Invoke();
    }

    public async Task<PurchaseOutcome> PurchaseProAsync()
    {
        IInAppBilling billing = CrossInAppBilling.Current;
        try
        {
            if (!await billing.ConnectAsync())
            {
                LastError = "Không kết nối được với Google Play. Kiểm tra mạng rồi thử lại.";
                Changed?.Invoke();
                return PurchaseOutcome.Error;
            }

            InAppBillingPurchase? purchase;
            try
            {
                purchase = await billing.PurchaseAsync(ProProductId, ItemType.InAppPurchase);
            }
            finally
            {
                await billing.DisconnectAsync();
            }

            if (purchase == null) return PurchaseOutcome.Cancelled;
            await LockedRefreshAsync();
            Changed?.Invoke();
            return PurchaseOutcome.Purchased;
        }
        catch (InAppBillingPurchaseException ex) when (ex.PurchaseError == PurchaseError.UserCancelled)
        {
            return PurchaseOutcome.Cancelled;
        }
        catch (InAppBillingPurchaseException ex) when (ex.PurchaseError == PurchaseError.AlreadyOwned)
        {
            await LockedRefreshAsync();
            Changed?.Invoke();
            return PurchaseOutcome.AlreadyOwned;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Changed?.Invoke();
            return PurchaseOutcome.Error;
        }
    }

    public async Task<PurchaseOutcome> RestoreAsync()
    {
        bool wasPro = State.IsPro;
        await RefreshAsync();
        if (LastError != null) return PurchaseOutcome.Error;
        return State.IsPro ? (wasPro ? PurchaseOutcome.AlreadyOwned : PurchaseOutcome.Purchased) : PurchaseOutcome.Cancelled;
    }

    private async Task LockedRefreshAsync()
    {
        await _gate.WaitAsync();
        try { await RefreshLockedAsync(); }
        finally { _gate.Release(); }
    }

    private async Task RefreshLockedAsync()
    {
        IInAppBilling billing = CrossInAppBilling.Current;
        try
        {
            if (!await billing.ConnectAsync())
            {
                LastError = "Không kết nối được với Google Play.";
                return;
            }
            try
            {
                IEnumerable<InAppBillingPurchase>? purchases = await billing.GetPurchasesAsync(ItemType.InAppPurchase);
                InAppBillingPurchase? owned = purchases?.FirstOrDefault(p => p.ProductId == ProProductId && p.State == PurchaseState.Purchased);
                if (owned is { IsAcknowledged: false })
                {
                    try { await billing.FinalizePurchaseOfProductAsync([ProProductId]); }
                    catch (Exception) { /* still counts as owned; retried next refresh */ }
                }
                Preferences.Default.Set(CachedIsProKey, owned != null);
                State = new LicenseState(owned != null);

                IEnumerable<InAppBillingProduct>? products = await billing.GetProductInfoAsync(ItemType.InAppPurchase, [ProProductId]);
                ProPriceText = products?.FirstOrDefault(p => p.ProductId == ProProductId)?.LocalizedPrice;
                LastError = null;
            }
            finally
            {
                await billing.DisconnectAsync();
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }
}
