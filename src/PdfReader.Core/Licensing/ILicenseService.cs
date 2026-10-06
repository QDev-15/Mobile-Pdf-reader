namespace PdfReader.Core.Licensing;

public enum PurchaseOutcome
{
    /// <summary>Play confirmed the purchase; <see cref="ILicenseService.State"/> already reflects it.</summary>
    Purchased,

    /// <summary>The user closed the Play purchase sheet without buying.</summary>
    Cancelled,

    /// <summary>Play reports it is already owned (e.g. bought on another device with the same account);
    /// entitlement was applied same as <see cref="Purchased"/>.</summary>
    AlreadyOwned,

    /// <summary>Billing is unreachable, or Play returned an error. <see cref="ILicenseService.LastError"/>
    /// has the detail for a dialog.</summary>
    Error,
}

/// <summary>
/// The single source of truth for "does this install show ads right now", and the one door through
/// which a purchase happens. Same split as DocScanner.Core.Licensing.ILicenseService: implemented once
/// against Google Play Billing; everything else asks this interface.
/// </summary>
public interface ILicenseService
{
    /// <summary>Current Pro status. Starts as a locally-cached guess (instant, no network) and is
    /// corrected by <see cref="RefreshAsync"/> once Play answers; <see cref="Changed"/> fires on every change.</summary>
    LicenseState State { get; }

    /// <summary>Price of the Pro unlock as Play formats it for the user's country, once known; null
    /// before the first successful <see cref="RefreshAsync"/> or if Play could not be reached.</summary>
    string? ProPriceText { get; }

    /// <summary>Detail of the last <see cref="PurchaseOutcome.Error"/>, for a dialog. Cleared on success.</summary>
    string? LastError { get; }

    /// <summary>Raised whenever <see cref="State"/>, <see cref="ProPriceText"/> or <see cref="LastError"/> changes.</summary>
    event Action? Changed;

    /// <summary>Connects to Play, re-reads owned purchases and the product's price. Safe to call often
    /// (e.g. on every app resume); a call already in flight is awaited rather than repeated.</summary>
    Task RefreshAsync();

    /// <summary>Shows Play's purchase sheet for the Pro unlock.</summary>
    Task<PurchaseOutcome> PurchaseProAsync();

    /// <summary>Re-checks Play for a purchase made elsewhere (another device, or before a reinstall).</summary>
    Task<PurchaseOutcome> RestoreAsync();
}
