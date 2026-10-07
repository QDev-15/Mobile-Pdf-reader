using PdfReader.Models;
using PdfReader.Services;

namespace PdfReader.Views;

public partial class DashboardPage : ContentPage
{
	private readonly RecentPdfStore _store;
	private readonly PdfLibrary _library;
	private readonly PdfOpener _opener;
	private IReadOnlyList<RecentPdfRecord> _all = [];
	private DateTime _lastBackPress = DateTime.MinValue;

	public DashboardPage(RecentPdfStore store, PdfLibrary library, PdfOpener opener)
	{
		InitializeComponent();
		_store = store;
		_library = library;
		_opener = opener;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		Banner.Reattach();
		await ReloadListAsync();
	}

	/// <summary>Back on the Dashboard closes what is open first (pop-up, search), and only exits the app on a
	/// second press within two seconds, so one stray tap cannot throw the person out.</summary>
	protected override bool OnBackButtonPressed()
	{
		if (Dialogs.TryDismiss()) return true;
		if (SearchRow.IsVisible)
		{
			CloseSearch();
			return true;
		}
		if (DateTime.UtcNow - _lastBackPress < TimeSpan.FromSeconds(2)) return false; // let Android close the app
		_lastBackPress = DateTime.UtcNow;
		this.Toast("Nhấn Back lần nữa để thoát");
		return true;
	}

	private async void OnOpenPdfClicked(object? sender, EventArgs e)
	{
		try
		{
			FileResult? result = await FilePicker.Default.PickAsync(new PickOptions
			{
				PickerTitle = "Chọn PDF",
				FileTypes = FilePickerFileType.Pdf,
			});
			if (result != null) await _opener.OpenAsync(result.FullPath, result.FileName);
		}
		catch (Exception ex)
		{
			await this.AlertAsync("Không mở được PDF", ex.Message, "Đóng");
		}
	}

	private async void OnSettingsClicked(object? sender, EventArgs e) => await UiHost.Shell.GoToAsync(nameof(SettingsPage));

	private async void OnItemSelected(object? sender, SelectionChangedEventArgs e)
	{
		RecentListItem? item = e.CurrentSelection.FirstOrDefault() as RecentListItem;
		RecentList.SelectedItem = null;
		if (item != null) await _opener.OpenAsync(item.Uri, item.DisplayName);
	}

	private async void OnDeleteInvoked(object? sender, EventArgs e)
	{
		if ((sender as BindableObject)?.BindingContext is not RecentListItem item) return;
		bool ok = await this.AlertAsync("Xoá khỏi danh sách", $"Xoá \"{item.DisplayName}\" khỏi danh sách và xoá bản sao trong ứng dụng? (File gốc trong máy không bị ảnh hưởng.)", "Xoá", "Huỷ");
		if (!ok) return;
		await _store.RemoveAsync(item.Uri);
		if (_library.IsInLibrary(item.Uri)) _library.DeleteDocument(item.Uri);
		await ReloadListAsync();
	}

	// ------------------------------------------------------------------ search

	private void OnSearchClicked(object? sender, EventArgs e)
	{
		SearchRow.IsVisible = !SearchRow.IsVisible;
		if (SearchRow.IsVisible) SearchEntry.Focus();
		else CloseSearch();
	}

	private void OnSearchCloseClicked(object? sender, EventArgs e) => CloseSearch();

	// Strict focus <-> keyboard coupling (no exceptions): whatever makes the entry lose focus -- tapping
	// a list item, the close button, elsewhere on the page -- must also put the keyboard away.
	private void OnSearchEntryUnfocused(object? sender, FocusEventArgs e) => Dialogs.HideKeyboard();

	private void CloseSearch()
	{
		SearchEntry.Text = "";
		SearchEntry.Unfocus();
		SearchRow.IsVisible = false;
		ApplyFilter();
	}

	private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

	// ------------------------------------------------------------------ list

	private async Task ReloadListAsync()
	{
		_all = await _store.ListAsync();
		ApplyFilter();
	}

	private void ApplyFilter()
	{
		string query = SearchEntry.Text?.Trim() ?? "";
		IEnumerable<RecentPdfRecord> shown = _all;
		if (query.Length > 0)
		{
			string q = PdfReader.Core.Text.TextSearch.Fold(query, ignoreAccents: true);
			shown = _all.Where(r => PdfReader.Core.Text.TextSearch.Fold(r.DisplayName, ignoreAccents: true).Contains(q));
		}
		List<RecentListItem> items = shown.Select(r => new RecentListItem(r)).ToList();
		RecentList.ItemsSource = items;
		EmptyBox.IsVisible = items.Count == 0;
		EmptyLabel.Text = _all.Count == 0 ? "Chưa có PDF nào" : "Không có file nào khớp";
	}

	private sealed class RecentListItem(RecentPdfRecord record)
	{
		public string Uri => record.Uri;
		public string DisplayName => record.DisplayName;
		public string? ThumbnailPath => record.ThumbnailPath;
		public string MetaText => $"{record.PageCount} trang · {FormatWhen(record.LastOpenedUtc)}";

		private static string FormatWhen(DateTimeOffset whenUtc)
		{
			TimeSpan age = DateTimeOffset.UtcNow - whenUtc;
			if (age < TimeSpan.FromMinutes(1)) return "vừa xong";
			if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} phút trước";
			if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} giờ trước";
			if (age < TimeSpan.FromDays(2)) return "hôm qua";
			return $"{(int)age.TotalDays} ngày trước";
		}
	}
}
