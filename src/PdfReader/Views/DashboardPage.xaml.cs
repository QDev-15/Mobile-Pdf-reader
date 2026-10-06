using PdfReader.Models;
using PdfReader.Services;

namespace PdfReader.Views;

public partial class DashboardPage : ContentPage
{
	private readonly RecentPdfStore _store;

	public DashboardPage(RecentPdfStore store)
	{
		InitializeComponent();
		_store = store;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		PendingPdfOpen.Changed += OnPendingChanged;
		await ReloadListAsync();

		// Cold start via "Open with" / "Share": MainActivity.OnCreate already stashed the Uri before
		// this page existed.
		string? pending = PendingPdfOpen.Consume();
		if (pending != null) await OpenAsync(pending);
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		PendingPdfOpen.Changed -= OnPendingChanged;
	}

	// LaunchMode.SingleTop: app already running, Dashboard already the visible page -- OnAppearing will
	// not fire again, so handle the hand-off here instead.
	private void OnPendingChanged() =>
		MainThread.BeginInvokeOnMainThread(async () =>
		{
			string? pending = PendingPdfOpen.Consume();
			if (pending != null) await OpenAsync(pending);
		});

	private async void OnOpenPdfClicked(object? sender, EventArgs e)
	{
		try
		{
			FileResult? result = await FilePicker.Default.PickAsync(new PickOptions
			{
				PickerTitle = "Chọn PDF",
				FileTypes = FilePickerFileType.Pdf,
			});
			if (result != null) await OpenAsync(result.FullPath, result.FileName);
		}
		catch (Exception ex)
		{
			await DisplayAlert("Không mở được PDF", ex.Message, "Đóng");
		}
	}

	private async void OnItemSelected(object? sender, SelectionChangedEventArgs e)
	{
		RecentListItem? item = e.CurrentSelection.FirstOrDefault() as RecentListItem;
		RecentList.SelectedItem = null;
		if (item != null) await OpenAsync(item.Uri, item.DisplayName);
	}

	private async Task OpenAsync(string uriOrPath, string? displayName = null)
	{
		try
		{
			int pageCount;
			using (PdfPages pages = PdfPages.Open(uriOrPath)) pageCount = pages.Count;

			RecentPdfRecord record = await _store.RecordOpenedAsync(uriOrPath, displayName ?? GuessDisplayName(uriOrPath), pageCount);
			await Shell.Current.GoToAsync(nameof(ReaderPage), new Dictionary<string, object>
			{
				["uri"] = record.Uri,
				["displayName"] = record.DisplayName,
			});
		}
		catch (Exception ex)
		{
			await DisplayAlert("Không mở được PDF", ex.Message, "Đóng");
		}
	}

	private static string GuessDisplayName(string uriOrPath)
	{
		string last = uriOrPath.TrimEnd('/').Split('/').LastOrDefault() ?? uriOrPath;
		return Uri.UnescapeDataString(last);
	}

	private async Task ReloadListAsync()
	{
		IReadOnlyList<RecentPdfRecord> records = await _store.ListAsync();
		CountLabel.Text = $"{records.Count} tài liệu";
		EmptyLabel.IsVisible = records.Count == 0;
		RecentList.ItemsSource = records.Select(r => new RecentListItem(r)).ToList();
	}

	private sealed class RecentListItem(RecentPdfRecord record)
	{
		public string Uri => record.Uri;
		public string DisplayName => record.DisplayName;
		public string? ThumbnailPath => record.ThumbnailPath;
		public double ProgressFraction => Math.Clamp(record.ReadProgress, 0, 1);
		public string MetaText => $"{record.PageCount} trang · {FormatWhen(record.LastOpenedUtc)} · {(int)Math.Round(record.ReadProgress * 100)}%";

		private static string FormatWhen(DateTimeOffset whenUtc)
		{
			TimeSpan age = DateTimeOffset.UtcNow - whenUtc;
			if (age < TimeSpan.FromMinutes(1)) return "vừa xong";
			if (age < TimeSpan.FromHours(1)) return $"mở {(int)age.TotalMinutes} phút trước";
			if (age < TimeSpan.FromDays(1)) return $"mở {(int)age.TotalHours} giờ trước";
			if (age < TimeSpan.FromDays(2)) return "mở hôm qua";
			return $"mở {(int)age.TotalDays} ngày trước";
		}
	}
}
