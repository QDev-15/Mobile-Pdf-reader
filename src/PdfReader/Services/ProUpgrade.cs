using PdfReader.Core.Licensing;
using PdfReader.Views;

namespace PdfReader.Services;

/// <summary>The "buy Pro" prompt behind the gold crown button on the dashboard: what Pro gives, the price once Play has
/// answered, and the purchase itself, all in one dialog (no extra screen). Success switches ads off right away through
/// <see cref="ILicenseService.Changed"/> (the ads library suspends itself, see <c>AdsService</c>).</summary>
public sealed class ProUpgrade(ILicenseService license)
{
	public async Task ShowAsync(Page host)
	{
		if (license.State.IsPro)
		{
			await host.AlertAsync("PDF Reader Pro", "Bạn đã dùng bản Pro. Cảm ơn bạn!", "OK");
			return;
		}

		string price = license.ProPriceText is { } p ? $"Mua Pro · {p}" : "Mua Pro";
		bool buy = await host.AlertAsync("Nâng cấp PDF Reader Pro",
			"• Không còn quảng cáo (banner và quảng cáo xen kẽ)\n• Mua một lần, dùng vĩnh viễn\n• Ủng hộ tác giả tiếp tục phát triển ứng dụng",
			price, "Để sau");
		if (!buy) return;

		// Same hard rule as the Settings button: never wait on Google Play for more than a minute.
		(bool completed, PurchaseOutcome outcome) = await license.PurchaseProAsync().WaitOrTimeoutAsync();
		if (!completed)
		{
			await host.AlertAsync("Quá thời gian chờ", "Google Play không phản hồi sau 1 phút. Hãy thử lại.", "Đóng");
			return;
		}
		switch (outcome)
		{
			case PurchaseOutcome.Purchased or PurchaseOutcome.AlreadyOwned:
				await host.AlertAsync("Cảm ơn bạn!", "Đã nâng cấp PDF Reader Pro. Quảng cáo đã được tắt.", "OK");
				break;
			case PurchaseOutcome.Error:
				await host.AlertAsync("Chưa mua được", license.LastError ?? "Không kết nối được Google Play. Hãy thử lại sau.", "Đóng");
				break;
		}
	}
}
