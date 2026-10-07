using System.Collections.ObjectModel;
using System.ComponentModel;
using Android.Graphics;
using Color = Microsoft.Maui.Graphics.Color;
using PdfReader.Core.Pdf;
using PdfReader.Services;

namespace PdfReader.Views;

/// <summary>
/// The page-sorting screen: every page of the document as a thumbnail in a grid. Tap pages to select them,
/// then rotate, move earlier / later, or delete; or hold a page and drag it where it should go. "Lưu" writes
/// the result as a new PDF -- nothing is touched until then and the original is never modified. Every kept
/// page is copied exactly as it was (text, vectors, fonts), see <see cref="PdfStructureOps.Rebuild"/>.
/// </summary>
[QueryProperty(nameof(Source), "source")]
[QueryProperty(nameof(DisplayName), "displayName")]
public partial class PageManagerPage : ContentPage
{
	private readonly ResultService _results;
	private readonly PdfLibrary _library;
	private readonly ObservableCollection<PageItem> _items = [];
	private CancellationTokenSource? _thumbCts;

	public string? Source { get; set; }
	public string? DisplayName { get; set; }

	public sealed class PageItem(int sourceIndex) : INotifyPropertyChanged
	{
		private ImageSource? _thumb;
		private int _rotation;
		private bool _selected;
		private int _number;

		public int SourceIndex { get; } = sourceIndex;

		public ImageSource? Thumb
		{
			get => _thumb;
			set => Set(ref _thumb, value, nameof(Thumb));
		}

		/// <summary>Degrees clockwise added to the page's own rotation: 0, 90, 180 or 270.</summary>
		public int Rotation
		{
			get => _rotation;
			set => Set(ref _rotation, ((value % 360) + 360) % 360, nameof(Rotation));
		}

		public bool IsSelected
		{
			get => _selected;
			set
			{
				Set(ref _selected, value, nameof(IsSelected));
				Raise(nameof(OutlineColor));
				Raise(nameof(OutlineWidth));
			}
		}

		/// <summary>Position in the new document (1-based).</summary>
		public string Number
		{
			get => _number == SourceIndex + 1 ? $"{_number}" : $"{_number}  (gốc {SourceIndex + 1})";
		}

		public int Position
		{
			set
			{
				_number = value;
				Raise(nameof(Number));
			}
		}

		public Brush OutlineColor => new SolidColorBrush(IsSelected ? (Color)Application.Current!.Resources["Primary"] : (Color)Application.Current!.Resources["HairLine"]);

		public double OutlineWidth => IsSelected ? 2.5 : 1;

		public event PropertyChangedEventHandler? PropertyChanged;

		private void Set<T>(ref T field, T value, string name)
		{
			field = value;
			Raise(name);
		}

		private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}

	public PageManagerPage(ResultService results, PdfLibrary library)
	{
		InitializeComponent();
		_results = results;
		_library = library;
		PageGrid.ItemsSource = _items;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		Banner.Reattach();
		if (Source == null || _items.Count > 0) return;
		try
		{
			int count = await Task.Run(() =>
			{
				using PdfPages pages = PdfPages.Open(Source);
				return pages.Count;
			});
			for (int i = 0; i < count; i++) _items.Add(new PageItem(i));
			Renumber();
			_thumbCts = new CancellationTokenSource();
			_ = LoadThumbnailsAsync(Source, _items.ToList(), _thumbCts.Token);
		}
		catch (Exception ex)
		{
			await this.AlertAsync("Không đọc được PDF", ex.Message, "Đóng");
			await UiHost.Shell.GoToAsync("..");
		}
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_thumbCts?.Cancel();
	}

	protected override bool OnBackButtonPressed() => Dialogs.TryDismiss() || base.OnBackButtonPressed();

	private void Renumber()
	{
		for (int i = 0; i < _items.Count; i++) _items[i].Position = i + 1;
		int selected = _items.Count(p => p.IsSelected);
		SubtitleLabel.Text = selected > 0 ? $"{_items.Count} trang · đã chọn {selected}" : $"{_items.Count} trang · chạm để chọn, giữ và kéo để di chuyển";
	}

	private static async Task LoadThumbnailsAsync(string source, List<PageItem> items, CancellationToken ct)
	{
		await Task.Run(() =>
		{
			using PdfPages pages = PdfPages.Open(source);
			foreach (PageItem item in items)
			{
				if (ct.IsCancellationRequested) return;
				try
				{
					using Bitmap bitmap = pages.Render(item.SourceIndex, 260);
					using var stream = new MemoryStream();
					bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream);
					byte[] png = stream.ToArray();
					MainThread.BeginInvokeOnMainThread(() => item.Thumb = ImageSource.FromStream(() => new MemoryStream(png)));
				}
				catch (Exception ex)
				{
					Android.Util.Log.Warn("PdfReader", $"thumbnail {item.SourceIndex + 1} failed: {ex.Message}");
				}
			}
		}, ct);
	}

	// ------------------------------------------------------------------ selection and edits

	private void OnItemTapped(object? sender, TappedEventArgs e)
	{
		if ((sender as BindableObject)?.BindingContext is not PageItem item) return;
		item.IsSelected = !item.IsSelected;
		Renumber();
	}

	private void OnReorderCompleted(object? sender, EventArgs e) => Renumber();

	private void OnSelectAllClicked(object? sender, EventArgs e)
	{
		bool all = _items.All(p => p.IsSelected);
		foreach (PageItem p in _items) p.IsSelected = !all;
		Renumber();
	}

	private List<PageItem> Selected() => _items.Where(p => p.IsSelected).ToList();

	private async Task<List<PageItem>?> RequireSelectionAsync()
	{
		List<PageItem> selected = Selected();
		if (selected.Count > 0) return selected;
		await this.AlertAsync("Chọn trang", "Chạm vào các trang cần thao tác trước.", "OK");
		return null;
	}

	private async void OnRotateLeftClicked(object? sender, EventArgs e)
	{
		if (await RequireSelectionAsync() is { } selected) foreach (PageItem p in selected) p.Rotation -= 90;
	}

	private async void OnRotateRightClicked(object? sender, EventArgs e)
	{
		if (await RequireSelectionAsync() is { } selected) foreach (PageItem p in selected) p.Rotation += 90;
	}

	private async void OnEarlierClicked(object? sender, EventArgs e)
	{
		if (await RequireSelectionAsync() is not { } selected) return;
		foreach (PageItem p in selected.OrderBy(i => _items.IndexOf(i)))
		{
			int i = _items.IndexOf(p);
			if (i > 0 && !_items[i - 1].IsSelected) _items.Move(i, i - 1);
		}
		Renumber();
	}

	private async void OnLaterClicked(object? sender, EventArgs e)
	{
		if (await RequireSelectionAsync() is not { } selected) return;
		foreach (PageItem p in selected.OrderByDescending(i => _items.IndexOf(i)))
		{
			int i = _items.IndexOf(p);
			if (i < _items.Count - 1 && !_items[i + 1].IsSelected) _items.Move(i, i + 1);
		}
		Renumber();
	}

	private async void OnDeleteClicked(object? sender, EventArgs e)
	{
		if (await RequireSelectionAsync() is not { } selected) return;
		if (selected.Count >= _items.Count)
		{
			await this.AlertAsync("Xoá trang", "Tài liệu phải còn ít nhất 1 trang.", "Đóng");
			return;
		}
		foreach (PageItem p in selected) _items.Remove(p);
		Renumber();
	}

	private async void OnBackClicked(object? sender, EventArgs e) => await UiHost.Shell.GoToAsync("..");

	private async void OnSaveClicked(object? sender, EventArgs e)
	{
		if (Source == null || _items.Count == 0) return;
		string source = Source;
		List<PageEdit> edits = _items.Select(i => new PageEdit(i.SourceIndex, i.Rotation)).ToList();
		string output = _library.NewWorkFile();
		BusyLabel.Text = "Đang tạo file mới…";
		BusyPill.IsVisible = true;
		try
		{
			// Hard rule: the pill never stays up longer than 1 minute (TaskTimeoutExtensions) -- Rebuild
			// has no cancellation hook, so on timeout it is simply left running and this reports failure.
			bool completed = await Task.Run(() => PdfStructureOps.Rebuild(source, edits, output)).WaitOrTimeoutAsync();
			if (!completed)
			{
				await this.AlertAsync("Quá thời gian chờ", "Sắp xếp trang mất hơn 1 phút nên đã dừng chờ. Hãy thử lại.", "Đóng");
				return;
			}
		}
		catch (Exception ex)
		{
			await this.AlertAsync("Không tạo được file", ex.Message, "Đóng");
			return;
		}
		finally
		{
			BusyPill.IsVisible = false;
		}
		await _results.DeliverPdfAsync(output, $"{ResultService.BaseName(DisplayName ?? "PDF")}_sap-xep.pdf");
	}
}
