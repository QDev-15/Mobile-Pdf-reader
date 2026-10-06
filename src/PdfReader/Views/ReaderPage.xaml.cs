using Android.Graphics;
using PdfReader.Services;

namespace PdfReader.Views;

/// <summary>
/// M0 scope only (plan roadmap): one page at a time, rendered on demand via Android's PdfRenderer
/// (Services/PdfPages.cs). Continuous lazy-scroll and pinch zoom/pan are M1 ("Đọc mượt") -- not here yet.
/// </summary>
[QueryProperty(nameof(Uri), "uri")]
[QueryProperty(nameof(DisplayName), "displayName")]
public partial class ReaderPage : ContentPage
{
	private readonly RecentPdfStore _store;
	private PdfPages? _pages;
	private int _index;

	public string? Uri { get; set; }
	public string? DisplayName { get; set; }

	public ReaderPage(RecentPdfStore store)
	{
		InitializeComponent();
		_store = store;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (Uri == null) return;
		TitleLabel.Text = DisplayName ?? "PDF";
		try
		{
			LoadingIndicator.IsRunning = true;
			_pages = PdfPages.Open(Uri);
			_index = 0;
			await RenderCurrentPageAsync();
		}
		catch (Exception ex)
		{
			await DisplayAlert("Không đọc được PDF", ex.Message, "Đóng");
			await Shell.Current.GoToAsync("..");
		}
		finally
		{
			LoadingIndicator.IsRunning = false;
		}
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_pages?.Dispose();
		_pages = null;
	}

	private async void OnPrevClicked(object? sender, EventArgs e)
	{
		if (_index <= 0) return;
		_index--;
		await RenderCurrentPageAsync();
	}

	private async void OnNextClicked(object? sender, EventArgs e)
	{
		if (_pages == null || _index >= _pages.Count - 1) return;
		_index++;
		await RenderCurrentPageAsync();
	}

	private async void OnBackClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");

	private async Task RenderCurrentPageAsync()
	{
		if (_pages == null || Uri == null) return;
		PageIndicatorLabel.Text = $"Trang {_index + 1} / {_pages.Count}";
		PrevButton.IsEnabled = _index > 0;
		NextButton.IsEnabled = _index < _pages.Count - 1;

		int index = _index;
		byte[] png = await Task.Run(() =>
		{
			using Bitmap bitmap = _pages.Render(index, longEdge: 1600);
			using var stream = new MemoryStream();
			bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream); // Compress takes a plain System.IO.Stream
			return stream.ToArray();
		});
		PageImage.Source = ImageSource.FromStream(() => new MemoryStream(png));

		if (_pages.Count > 0) await _store.UpdateProgressAsync(Uri, (double)(_index + 1) / _pages.Count);
	}
}
