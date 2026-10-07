using PdfReader.Core;
using PdfReader.Core.Annotations;
using PdfReader.Core.Pdf;
using PdfReader.Core.Text;
using PdfReader.Models;
using PdfReader.Platforms.Android;
using PdfReader.Services;

namespace PdfReader.Views;

/// <summary>
/// Reads one PDF: a continuous, zoomable page strip (<see cref="PdfCanvasView"/>) with search, text
/// selection / copy / OCR, an edit dock under the document (pen with tip sizes and highlighter, text,
/// signature drawn or from a picture, undo / redo, export), highlight / underline / strikeout / redact on
/// selected text, page and PDF tools. The page is mostly menus and dialogs around the native view; the
/// heavy lifting is in <see cref="PdfCanvasView"/>, <see cref="PdfSession"/> and <see cref="PdfExporter"/>.
/// </summary>
[QueryProperty(nameof(Uri), "uri")]
[QueryProperty(nameof(DisplayName), "displayName")]
[QueryProperty(nameof(External), "external")]
public partial class ReaderPage : ContentPage
{
	private static readonly uint[] Palette = [0xFF1565C0, 0xFFD32F2F, 0xFF212121, 0xFF2E7D32, 0xFFEF6C00, 0xFFFFD600];
	private const uint HighlightColor = 0x88FFEB3B;
	private const uint LineMarkColor = 0xFFD32F2F;

	/// <summary>Pen tips from fine to bold, then highlighter widths. Width is a fraction of the page width;
	/// Diameter is how big the chip draws it.</summary>
	private sealed record Tip(bool Marker, float Width, double Diameter);

	private static readonly Tip[] Tips =
	[
		new(false, 0.0025f, 4), new(false, 0.005f, 7), new(false, 0.009f, 11), new(false, 0.014f, 16),
		new(true, 0.02f, 10), new(true, 0.035f, 18),
	];

	private readonly RecentPdfStore _store;
	private readonly PdfLibrary _library;
	private readonly OcrService _ocr;
	private readonly SignatureStore _signatures;
	private readonly SignatureImageStore _signatureImages;
	private readonly PdfExporter _exporter;
	private readonly ResultService _results;
	private readonly IAdsService _ads;
	private readonly PadDrawable _pad = new();
	private readonly List<Grid> _tipChips = [];
	private readonly List<(Button Chip, uint Color)> _colorChips = [];

	private PdfSession? _session;
	private PdfCanvasView? _engine;
	private IReadOnlyList<SearchHit> _hits = [];
	private int _hitIndex;
	private Guid? _activeSignature;
	private SavedSignature? _dragSignature;
	private string _textFont = "sans";
	private bool _textBold, _textItalic;
	private uint _penColor = Palette[0], _markerColor = Palette[5];
	private int _tipIndex = 1;
	private CancellationTokenSource? _progressSave;
	private CancellationTokenSource? _searchCts;
	private int _busyJobToken; // 0 = no RunJobAsync in flight; otherwise which call owns the busy pill right now
	private bool _closing;

	public string? Uri { get; set; }
	public string? DisplayName { get; set; }

	/// <summary>Opened straight from another app: closing the reader then closes the whole app.</summary>
	public bool External { get; set; }

	public ReaderPage(RecentPdfStore store, PdfLibrary library, OcrService ocr, SignatureStore signatures, SignatureImageStore signatureImages,
		PdfExporter exporter, ResultService results, IAdsService ads)
	{
		InitializeComponent();
		_store = store;
		_library = library;
		_ocr = ocr;
		_signatures = signatures;
		_signatureImages = signatureImages;
		_exporter = exporter;
		_results = results;
		_ads = ads;
		SignaturePad.Drawable = _pad;
		var drop = new DropGestureRecognizer { AllowDrop = true };
		drop.Drop += OnViewerDrop;
		Viewer.GestureRecognizers.Add(drop);
		BuildTipRow();
		BuildColorRow();
	}

	// ------------------------------------------------------------------ lifecycle

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		Banner.Reattach();
		if (Uri == null || _session != null) return;
		_closing = false;
		TitleLabel.Text = DisplayName ?? "PDF";
		try
		{
			RecentPdfRecord? record = await _store.GetAsync(Uri);
			string path = Uri, name = DisplayName ?? "PDF";
			_session = await Task.Run(() => PdfSession.Open(path, name, _library, _ocr));
			_session.Annotations.Changed += RefreshToolButtons;

			// The native view exists once the page is on screen; give the handler a moment if it is not yet.
			for (int i = 0; i < 20 && Viewer.Engine == null; i++) await Task.Delay(50);
			_engine = Viewer.Engine ?? throw new InvalidOperationException("Không khởi tạo được trình xem PDF.");
			Wire(_engine);
			_engine.Theme = (ReadTheme)Preferences.Default.Get(SettingsPage.ThemeKey, 0);
			_engine.Load(_session, Math.Clamp(record?.LastPage ?? 0, 0, Math.Max(0, _session.PageCount - 1)));
			UpdatePageIndicator(_engine.CurrentPage);
			ApplyToolState();
		}
		catch (Exception ex)
		{
			await this.AlertAsync("Không đọc được PDF", ex.Message, "Đóng");
			await LeaveAsync();
		}
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		Cleanup();
	}

	/// <summary>Saves and releases everything. Safe to call twice (the system may skip OnDisappearing when
	/// the whole app is closed from an external open, so closing calls it explicitly too).</summary>
	private void Cleanup()
	{
		SaveProgressNow();
		_searchCts?.Cancel();
		if (_engine != null)
		{
			Unwire(_engine);
			_engine.Unload();
			_engine = null;
		}
		if (_session != null)
		{
			_session.Annotations.Changed -= RefreshToolButtons;
			_session.Dispose();
			_session = null;
		}
	}

	/// <summary>Closes the reader. Opened from another app: that closes the whole app, with no Dashboard in
	/// between. Otherwise back to the Dashboard, which is a natural point for an interstitial.</summary>
	private async Task LeaveAsync()
	{
		if (_closing) return;
		_closing = true;
		if (External)
		{
			Cleanup();
			Platform.CurrentActivity?.Finish();
			return;
		}
		_ads.RegisterScreenTransition();
		await UiHost.Shell.GoToAsync("..");
	}

	protected override bool OnBackButtonPressed()
	{
		if (Dialogs.TryDismiss()) return true;
		if (SignaturePadOverlay.IsVisible) { HidePad(); return true; }
		if (OutlineOverlay.IsVisible) { OutlineOverlay.IsVisible = false; return true; }
		if (SearchRow.IsVisible) { CloseSearch(); return true; }
		if (_engine?.HasTextSelection == true) { _engine.ClearTextSelection(); return true; }
		if (_engine?.SelectedAnnotation != null) { _engine.DeselectAnnotation(); return true; }
		if (ToolDock.IsVisible)
		{
			if (_engine != null && _engine.Tool != ViewerTool.View) SetTool(ViewerTool.View);
			else CloseDock();
			return true;
		}
		_ = LeaveAsync();
		return true;
	}

	private void Wire(PdfCanvasView e)
	{
		e.PageChanged += OnPageChanged;
		e.Tapped += OnViewerTapped;
		e.PlaceRequested += OnPlaceRequested;
		e.TextSelectionChanged += OnTextSelectionChanged;
		e.AnnotationSelectionChanged += OnAnnotationSelectionChanged;
		e.Busy += OnEngineBusy;
		e.Message += OnEngineMessage;
	}

	private void Unwire(PdfCanvasView e)
	{
		e.PageChanged -= OnPageChanged;
		e.Tapped -= OnViewerTapped;
		e.PlaceRequested -= OnPlaceRequested;
		e.TextSelectionChanged -= OnTextSelectionChanged;
		e.AnnotationSelectionChanged -= OnAnnotationSelectionChanged;
		e.Busy -= OnEngineBusy;
		e.Message -= OnEngineMessage;
	}

	private void OnEngineMessage(string text) => this.Toast(text);

	// ------------------------------------------------------------------ small UI helpers

	private string BaseName => ResultService.BaseName(DisplayName ?? "PDF");

	private void UpdatePageIndicator(int page) =>
		PageIndicatorLabel.Text = _session == null ? "" : $"{page + 1} / {_session.PageCount}";

	private void OnPageChanged(int page)
	{
		UpdatePageIndicator(page);
		_progressSave?.Cancel();
		var cts = _progressSave = new CancellationTokenSource();
		_ = Task.Delay(700, cts.Token).ContinueWith(t =>
		{
			if (!t.IsCanceled) MainThread.BeginInvokeOnMainThread(SaveProgressNow);
		}, TaskScheduler.Default);
	}

	private void SaveProgressNow()
	{
		if (_session == null || Uri == null || _engine == null || _session.PageCount == 0) return;
		int page = _engine.CurrentPage;
		_ = _store.UpdateProgressAsync(Uri, (double)(page + 1) / _session.PageCount, page);
	}

	/// <summary>Always on the UI thread, even if the caller is not -- OCR (ML Kit) and some Play Services
	/// calls complete their continuation off the main thread, and a property set on a native Android view
	/// from the wrong thread can silently fail to redraw instead of throwing, which reads as "the spinner
	/// is stuck" (it is still spinning underneath; the pill just never got told to hide).</summary>
	private void OnUiThread(Action action)
	{
		if (MainThread.IsMainThread) action();
		else MainThread.BeginInvokeOnMainThread(action);
	}

	private void ShowBusy(string text, double? progress = null) => OnUiThread(() =>
	{
		BusyLabel.Text = text;
		BusyProgress.IsVisible = progress != null;
		if (progress != null) BusyProgress.Progress = Math.Clamp(progress.Value, 0, 1);
		BusyPill.IsVisible = true;
	});

	private void HideBusy() => OnUiThread(() => BusyPill.IsVisible = false);

	private void OnEngineBusy(string? message)
	{
		if (_busyJobToken != 0) return; // a RunJobAsync call owns the pill right now; let its own finally decide
		if (message == null) HideBusy();
		else ShowBusy(message);
	}

	/// <summary>Runs a long operation behind the busy pill, reporting failures instead of crashing. Each
	/// call gets its own token: if a second call starts before the first's "finally" runs (e.g. a search
	/// fired again while the previous one was still wrapping up), only the call that is still current when
	/// it finishes gets to hide the pill or report progress -- a superseded call's late finally must not
	/// hide the NEWER call's spinner, and must not go on nudging its progress bar either.
	///
	/// Hard rule: the pill never stays up longer than <see cref="TaskTimeoutExtensions.DefaultTimeoutMs"/>
	/// (1 minute), whatever the reason the work has not finished by then. The underlying work cannot
	/// always be cancelled (OCR, Play Services, ...), so on timeout it is simply abandoned -- its result,
	/// if it ever arrives, is unobserved -- and this reports failure so the caller treats it the same as
	/// any other "did not complete".</summary>
	private async Task<bool> RunJobAsync(string title, Func<IProgress<double>, Task> work)
	{
		int token = ++_busyJobToken;
		if (token == 0) token = ++_busyJobToken; // skip the sentinel "no job" value on wraparound
		ShowBusy(title, 0);
		try
		{
			Task workTask = work(new Progress<double>(v => { if (_busyJobToken == token) ShowBusy(title, v); }));
			bool completed = await workTask.WaitOrTimeoutAsync();
			if (!completed)
			{
				await this.AlertAsync("Quá thời gian chờ", $"\"{title.TrimEnd('…', ' ')}\" mất hơn 1 phút nên đã dừng chờ. Hãy thử lại.", "Đóng");
				return false;
			}
			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		catch (Exception ex)
		{
			await this.AlertAsync("Không hoàn tất được", ex.Message, "Đóng");
			return false;
		}
		finally
		{
			if (_busyJobToken == token)
			{
				_busyJobToken = 0;
				HideBusy();
			}
		}
	}

	private Task ShowToastAsync(string message)
	{
		this.Toast(message);
		return Task.CompletedTask;
	}

	private PdfQuality DefaultQuality() => PdfQuality.FromKey(Preferences.Default.Get(SettingsPage.QualityKey, PdfQuality.Medium.Key));

	private async Task<PdfQuality?> AskQualityAsync()
	{
		string? choice = await this.ChoiceAsync("Chất lượng", "Huỷ", null, PdfQuality.All.Select(q => q.Label).ToArray());
		return PdfQuality.All.FirstOrDefault(q => q.Label == choice);
	}

	// ------------------------------------------------------------------ top bar

	private async void OnBackClicked(object? sender, EventArgs e) => await LeaveAsync();

	private void OnViewerTapped()
	{
		// A tap on empty page toggles the footer toolbar, for a clean full-screen read.
		if (!ToolDock.IsVisible) ToolbarRow.IsVisible = !ToolbarRow.IsVisible;
	}

	private async void OnPageIndicatorTapped(object? sender, TappedEventArgs e)
	{
		if (_session == null || _engine == null) return;
		string? text = await this.PromptAsync("Đi tới trang", $"Nhập số trang (1–{_session.PageCount})", "Đi", "Huỷ", keyboard: Keyboard.Numeric, initialValue: $"{_engine.CurrentPage + 1}");
		if (int.TryParse(text, out int page) && page >= 1 && page <= _session.PageCount) _engine.GoToPage(page - 1);
	}

	private async void OnPagesClicked(object? sender, EventArgs e) => await OpenPageManagerAsync();

	private async void OnMenuClicked(object? sender, EventArgs e)
	{
		if (_session == null || _engine == null) return;
		string? choice = await this.ChoiceAsync(DisplayName ?? "PDF", "Đóng", null,
			"Đổi tên file", "Sắp xếp / xoay / xoá trang", "Mục lục", "Chế độ đọc…", "Chọn & sao chép chữ trang này", "Công cụ PDF…", "Chia sẻ file gốc");
		switch (choice)
		{
			case "Đổi tên file": await RenameAsync(); break;
			case "Sắp xếp / xoay / xoá trang": await OpenPageManagerAsync(); break;
			case "Mục lục": await ShowOutlineAsync(); break;
			case "Chế độ đọc…": await PickThemeAsync(); break;
			case "Chọn & sao chép chữ trang này": await _engine.SelectAllOnPageAsync(); break;
			case "Công cụ PDF…": await ShowPdfToolsAsync(); break;
			case "Chia sẻ file gốc":
				try { await _results.ShareAsync(_session.Path, $"{BaseName}.pdf", "application/pdf"); }
				catch (Exception ex) { await this.AlertAsync("Không chia sẻ được", ex.Message, "Đóng"); }
				break;
		}
	}

	/// <summary>Changes only the Dashboard / title-bar label -- the PDF bytes and its file name on disk are
	/// untouched (so re-exporting still bases its name on the ORIGINAL file name via <see cref="BaseName"/>
	/// at the time it was opened; renaming after the fact does not retroactively change already-queued
	/// export names, which is fine since each export asks for its own name anyway).</summary>
	private async Task RenameAsync()
	{
		if (Uri == null) return;
		string? input = await this.PromptAsync("Đổi tên file", "Tên mới cho tài liệu này", "Lưu", "Huỷ", initialValue: BaseName, maxLength: 120);
		if (string.IsNullOrWhiteSpace(input)) return;
		string name = ResultService.SafeName(input.Trim());
		if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
		RecentPdfRecord? updated = await _store.RenameAsync(Uri, name);
		if (updated == null) return;
		DisplayName = updated.DisplayName;
		TitleLabel.Text = DisplayName;
	}

	private async Task PickThemeAsync()
	{
		string? choice = await this.ChoiceAsync("Chế độ đọc", "Huỷ", null, "Sáng", "Tối", "Sepia (giấy vàng)");
		ReadTheme? theme = choice switch { "Sáng" => ReadTheme.Light, "Tối" => ReadTheme.Dark, "Sepia (giấy vàng)" => ReadTheme.Sepia, _ => null };
		if (theme == null || _engine == null) return;
		_engine.Theme = theme.Value;
		Preferences.Default.Set(SettingsPage.ThemeKey, (int)theme.Value);
	}

	private async Task ShowPdfToolsAsync()
	{
		string? choice = await this.ChoiceAsync("Công cụ PDF", "Huỷ", null,
			"Sắp xếp / xoay / xoá trang", "Tách PDF", "Nén PDF", "Tạo PDF tìm kiếm được (OCR)", "Chuyển trang thành ảnh",
			"Đặt mật khẩu", "Thêm số trang", "Thêm watermark", "Xoá tất cả chú thích");
		switch (choice)
		{
			case "Sắp xếp / xoay / xoá trang": await OpenPageManagerAsync(); break;
			case "Tách PDF": await SplitAsync(); break;
			case "Nén PDF": await CompressAsync(); break;
			case "Tạo PDF tìm kiếm được (OCR)": await MakeSearchableAsync(); break;
			case "Chuyển trang thành ảnh": await PagesToImagesAsync(); break;
			case "Đặt mật khẩu": await ProtectAsync(); break;
			case "Thêm số trang": AddPageNumbers(); break;
			case "Thêm watermark": await AddWatermarkAsync(); break;
			case "Xoá tất cả chú thích": await ClearAnnotationsAsync(); break;
		}
	}

	// ------------------------------------------------------------------ outline

	private sealed class OutlineItem(OutlineEntry entry)
	{
		public string Title => entry.Title;
		public int Page => entry.Page;
		public string PageText => $"{entry.Page + 1}";
		public Thickness Indent => new(Math.Min(entry.Level, 4) * 14, 0, 0, 0);
	}

	private async Task ShowOutlineAsync()
	{
		if (_session == null) return;
		IReadOnlyList<OutlineEntry> entries = await _session.Text.GetOutlineAsync();
		if (entries.Count == 0)
		{
			await this.AlertAsync("Mục lục", "Tài liệu này không có mục lục.", "Đóng");
			return;
		}
		OutlineList.ItemsSource = entries.Select(e => new OutlineItem(e)).ToList();
		OutlineOverlay.IsVisible = true;
	}

	private void OnOutlineSelected(object? sender, SelectionChangedEventArgs e)
	{
		if (e.CurrentSelection.FirstOrDefault() is OutlineItem item)
		{
			OutlineOverlay.IsVisible = false;
			_engine?.GoToPage(item.Page);
		}
		OutlineList.SelectedItem = null;
	}

	private void OnOutlineCloseClicked(object? sender, EventArgs e) => OutlineOverlay.IsVisible = false;

	// ------------------------------------------------------------------ search

	private void OnSearchClicked(object? sender, EventArgs e)
	{
		SearchRow.IsVisible = true;
		SearchEntry.Focus();
	}

	private void CloseSearch()
	{
		_searchCts?.Cancel();
		SearchRow.IsVisible = false;
		SearchEntry.Unfocus();
		_hits = [];
		SearchCountLabel.Text = "";
		_engine?.SetSearchHits([], 0);
	}

	private void OnSearchCloseClicked(object? sender, EventArgs e) => CloseSearch();

	// Strict focus <-> keyboard coupling (no exceptions): whatever makes the entry lose focus must also
	// put the keyboard away, instead of relying on Android to do it just because IsFocused flipped.
	private void OnSearchEntryUnfocused(object? sender, FocusEventArgs e) => Dialogs.HideKeyboard();

	private async void OnSearchCompleted(object? sender, EventArgs e)
	{
		string query = SearchEntry.Text?.Trim() ?? "";
		SearchEntry.Unfocus();
		if (query.Length == 0) return;
		await RunSearchAsync(query, ocrMissing: false);
	}

	private async Task RunSearchAsync(string query, bool ocrMissing)
	{
		if (_session == null || _engine == null) return;
		_searchCts?.Cancel();
		var cts = _searchCts = new CancellationTokenSource();
		IReadOnlyList<SearchHit> hits = [];
		int skipped = 0;
		string title = ocrMissing ? "Nhận dạng chữ & tìm…" : "Đang tìm…";
		bool ok = await RunJobAsync(title, async progress =>
		{
			var counter = new Progress<(int Done, int Total)>(t => progress.Report((double)t.Done / t.Total));
			(hits, skipped) = await _session.Text.SearchAsync(query, ocrMissing, counter, cts.Token);
		});
		if (!ok || cts.IsCancellationRequested) return;

		_hits = hits;
		_hitIndex = 0;
		_engine?.SetSearchHits(hits, 0);
		SearchCountLabel.Text = hits.Count == 0 ? "0" : $"1/{hits.Count}";

		if (skipped > 0 && !ocrMissing)
		{
			bool ocr = await this.AlertAsync("Có trang scan", $"{skipped} trang là ảnh scan nên chưa tìm được chữ trong đó. Nhận dạng chữ (OCR) các trang này để tìm tiếp? Có thể mất một lúc.", "Nhận dạng", "Bỏ qua");
			if (ocr) await RunSearchAsync(query, ocrMissing: true);
		}
		else if (hits.Count == 0)
		{
			await this.AlertAsync("Tìm kiếm", $"Không thấy \"{query}\".", "Đóng");
		}
	}

	private void OnSearchNextClicked(object? sender, EventArgs e) => StepHit(1);

	private void OnSearchPrevClicked(object? sender, EventArgs e) => StepHit(-1);

	private void StepHit(int delta)
	{
		if (_hits.Count == 0 || _engine == null) return;
		_hitIndex = (_hitIndex + delta + _hits.Count) % _hits.Count;
		_engine.SetCurrentHit(_hitIndex);
		SearchCountLabel.Text = $"{_hitIndex + 1}/{_hits.Count}";
	}

	// ------------------------------------------------------------------ text selection

	private void OnTextSelectionChanged(bool has) => SelectionBar.IsVisible = has;

	private async void OnCopyClicked(object? sender, EventArgs e)
	{
		if (_engine == null) return;
		string text = _engine.SelectedText;
		if (text.Length == 0) return;
		await Clipboard.Default.SetTextAsync(text);
		_engine.ClearTextSelection();
		await ShowToastAsync("Đã sao chép");
	}

	private void OnHighlightClicked(object? sender, EventArgs e) => _engine?.ApplyMarkup(MarkupKind.Highlight, HighlightColor);

	private void OnUnderlineClicked(object? sender, EventArgs e) => _engine?.ApplyMarkup(MarkupKind.Underline, LineMarkColor);

	private void OnStrikeClicked(object? sender, EventArgs e) => _engine?.ApplyMarkup(MarkupKind.Strikeout, LineMarkColor);

	private async void OnRedactClicked(object? sender, EventArgs e)
	{
		if (_engine == null) return;
		bool ok = await this.AlertAsync("Che nội dung", "Phần chữ được chọn sẽ bị che kín bằng màu đen. Khi xuất PDF, trang đó được chuyển thành ảnh nên chữ bên dưới biến mất hẳn, không thể khôi phục.", "Che", "Huỷ");
		if (ok) _engine.ApplyMarkup(MarkupKind.Redact, 0xFF000000);
	}

	private void OnSelectionCloseClicked(object? sender, EventArgs e) => _engine?.ClearTextSelection();

	// ------------------------------------------------------------------ edit dock

	private void BuildTipRow()
	{
		for (int i = 0; i < Tips.Length; i++)
		{
			int index = i;
			Tip tip = Tips[i];
			var mark = new Border
			{
				WidthRequest = tip.Marker ? 28 : tip.Diameter,
				HeightRequest = tip.Diameter,
				BackgroundColor = tip.Marker ? Color.FromArgb("#FFD600") : Color.FromArgb("#212121"),
				Stroke = Colors.Transparent,
				StrokeThickness = 0,
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center,
				StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = tip.Marker ? 4 : tip.Diameter / 2 },
			};
			var chip = new Grid { WidthRequest = tip.Marker ? 48 : 40, HeightRequest = 40 };
			chip.Add(mark);
			var tap = new TapGestureRecognizer();
			tap.Tapped += (_, _) => SelectTip(index);
			chip.GestureRecognizers.Add(tap);
			_tipChips.Add(chip);
			TipRow.Add(chip);
			if (i == 3) TipRow.Add(new BoxView { WidthRequest = 1, HeightRequest = 26, Color = Color.FromArgb("#D5D5D5"), VerticalOptions = LayoutOptions.Center, Margin = new Thickness(4, 0) });
		}
	}

	private void BuildColorRow()
	{
		foreach (uint color in Palette)
		{
			uint c = color;
			var chip = new Button
			{
				WidthRequest = 30,
				HeightRequest = 30,
				CornerRadius = 15,
				Padding = 0,
				MinimumWidthRequest = 0,
				MinimumHeightRequest = 0,
				BackgroundColor = Color.FromUint(c),
				BorderColor = Colors.White,
				BorderWidth = 2,
			};
			chip.Clicked += (_, _) => OnColorPicked(c);
			_colorChips.Add((chip, c));
			ColorRow.Add(chip);
		}
	}

	/// <summary>The colour chips colour whatever is selected (a text box or a drawn signature), else the pen /
	/// highlighter / next text.</summary>
	private void OnColorPicked(uint color)
	{
		Annotation? selected = _engine?.SelectedAnnotation;
		switch (selected)
		{
			case TextBoxAnnotation t:
				_session?.Annotations.Replace(t with { Color = color });
				_penColor = color;
				break;
			case SignatureAnnotation s:
				_session?.Annotations.Replace(s with { Color = color });
				_penColor = color;
				break;
			default:
				if (_engine?.Tool == ViewerTool.Marker) _markerColor = color;
				else _penColor = color;
				break;
		}
		ApplyToolState();
	}

	private void SelectTip(int index)
	{
		if (_engine == null) return;
		_tipIndex = index;
		Tip tip = Tips[index];
		if (tip.Marker) _engine.MarkerWidth = tip.Width;
		else _engine.PenWidth = tip.Width;
		SetTool(tip.Marker ? ViewerTool.Marker : ViewerTool.Pen, forceOn: true);
	}

	private void OnEditFabClicked(object? sender, EventArgs e)
	{
		if (ToolDock.IsVisible) CloseDock();
		else OpenDock();
	}

	private void OpenDock()
	{
		ToolDock.IsVisible = true;
		ToolbarRow.IsVisible = false; // the dock stands in for the footer toolbar while editing
		ApplyToolState();
	}

	private void OnDockCloseClicked(object? sender, EventArgs e) => CloseDock();

	private void CloseDock()
	{
		SetTool(ViewerTool.View);
		ToolDock.IsVisible = false;
		ToolbarRow.IsVisible = true;
		ApplyToolState();
	}

	/// <summary>Switches tool; choosing the active one again switches back to plain reading.</summary>
	private void SetTool(ViewerTool tool, bool forceOn = false)
	{
		if (_engine == null) return;
		if (_engine.Tool == tool && !forceOn) tool = ViewerTool.View;
		_engine.Tool = tool;
		_engine.ClearTextSelection();
		_engine.DeselectAnnotation();
		ApplyToolState();
		if (tool == ViewerTool.Signature) RebuildSignatureList();
		if (tool is ViewerTool.Pen or ViewerTool.Marker && !Preferences.Default.Get("hint_draw_shown", false))
		{
			Preferences.Default.Set("hint_draw_shown", true);
			_ = ShowToastAsync("Đang vẽ: dùng 2 ngón để cuộn và phóng to.");
		}
	}

	private void RefreshToolButtons() => MainThread.BeginInvokeOnMainThread(ApplyToolState);

	/// <summary>Brings every dock control in line with the current tool, selection, tip, colours and undo
	/// history.</summary>
	private void ApplyToolState()
	{
		ViewerTool tool = _engine?.Tool ?? ViewerTool.View;
		Annotation? selected = _engine?.SelectedAnnotation;
		TextBoxAnnotation? text = selected as TextBoxAnnotation;
		bool objectSelected = selected is TextBoxAnnotation or SignatureAnnotation or ImageAnnotation;

		if (_engine != null)
		{
			_engine.InkColor = tool == ViewerTool.Marker ? _markerColor : _penColor;
			Tip tip = Tips[_tipIndex];
			if (tip.Marker) _engine.MarkerWidth = tip.Width;
			else _engine.PenWidth = tip.Width;
		}

		Color on = Color.FromArgb("#F6D9D4"), off = Color.FromArgb("#F8F4EC"), primary = (Color)Application.Current!.Resources["Primary"], ink = (Color)Application.Current.Resources["OffBlack"];
		void MarkIcon(Button b, bool active)
		{
			b.BackgroundColor = active ? on : Colors.Transparent;
			b.TextColor = active ? primary : ink;
		}
		void MarkChip(Button b, bool active)
		{
			b.BackgroundColor = active ? on : off;
			b.TextColor = active ? primary : ink;
		}
		MarkIcon(PenButton, tool is ViewerTool.Pen or ViewerTool.Marker);
		MarkIcon(TextToolButton, tool == ViewerTool.Text);
		MarkIcon(SignToolButton, tool == ViewerTool.Signature);

		UndoButton.Opacity = (_session?.Annotations.CanUndo ?? false) ? 1 : 0.3;
		RedoButton.Opacity = (_session?.Annotations.CanRedo ?? false) ? 1 : 0.3;

		// Which context rows show
		bool drawing = tool is ViewerTool.Pen or ViewerTool.Marker;
		ContextSection.IsVisible = tool != ViewerTool.View || objectSelected;
		TipScroll.IsVisible = drawing && !objectSelected;
		SignatureRow.IsVisible = tool == ViewerTool.Signature && !objectSelected;
		TextStyleScroll.IsVisible = text != null;
		ObjectRow.IsVisible = objectSelected;
		ColorScroll.IsVisible = (drawing && !objectSelected) || tool == ViewerTool.Text || text != null || selected is SignatureAnnotation;
		DockHint.IsVisible = tool != ViewerTool.View && !objectSelected;
		DockHint.Text = tool switch
		{
			ViewerTool.Text => "Chạm vào trang để thêm chữ.",
			ViewerTool.Signature => "Giữ một chữ ký rồi kéo vào trang, hoặc chạm để đặt giữa màn hình.",
			_ => "Dùng 2 ngón để cuộn và phóng to khi đang vẽ.",
		};

		for (int i = 0; i < _tipChips.Count; i++)
			_tipChips[i].BackgroundColor = drawing && i == _tipIndex ? on : Colors.Transparent;

		uint current = text?.Color ?? (selected as SignatureAnnotation)?.Color ?? (tool == ViewerTool.Marker ? _markerColor : _penColor);
		foreach ((Button chip, uint color) in _colorChips)
		{
			chip.BorderColor = color == current ? primary : Colors.White;
			chip.BorderWidth = color == current ? 3 : 2;
		}

		string family = text?.FontFamily ?? _textFont;
		MarkChip(FontSansButton, family == "sans");
		MarkChip(FontSerifButton, family == "serif");
		MarkChip(FontMonoButton, family == "mono");
		MarkChip(FontCursiveButton, family == "cursive");
		MarkChip(BoldButton, text?.Bold ?? false);
		MarkChip(ItalicButton, text?.Italic ?? false);

		// The floating action bars sit above the dock when it is open, above the floating button when not.
		var margin = new Thickness(12, 0, 12, 12);
		SelectionBar.Margin = margin;
		AnnotationBar.Margin = margin;
	}

	private void OnPenClicked(object? sender, EventArgs e)
	{
		if (_engine == null) return;
		if (_engine.Tool is ViewerTool.Pen or ViewerTool.Marker) SetTool(ViewerTool.View);
		else SelectTip(_tipIndex);
	}

	private void OnTextToolClicked(object? sender, EventArgs e) => SetTool(ViewerTool.Text);

	private void OnSignToolClicked(object? sender, EventArgs e) => SetTool(ViewerTool.Signature);

	private void OnUndoClicked(object? sender, EventArgs e) => _session?.Annotations.Undo();

	private void OnRedoClicked(object? sender, EventArgs e) => _session?.Annotations.Redo();

	// --- placing text

	private float PageAspectOf(int page)
	{
		(int w, int h) = _session!.EnsureMeasured()[page];
		return (float)h / w;
	}

	private async void OnPlaceRequested(ViewerTool tool, int page, float nx, float ny)
	{
		if (_session == null || _engine == null) return;
		if (tool == ViewerTool.Text)
		{
			string? text = await this.PromptAsync("Thêm văn bản", "Nội dung:", "Thêm", "Huỷ", maxLength: 300);
			if (string.IsNullOrWhiteSpace(text)) return;
			var annotation = new TextBoxAnnotation(Guid.NewGuid(), page, text, Math.Clamp(nx - 0.02f, 0, 0.9f), Math.Clamp(ny - 0.01f, 0, 0.97f), 0.032f, _penColor,
				FontFamily: _textFont, Bold: _textBold, Italic: _textItalic);
			_session.Annotations.Add(annotation);
			SetTool(ViewerTool.View);
			_engine.SelectAnnotation(annotation.Id);
		}
		else if (tool == ViewerTool.Signature && ActiveSignature() is { } sig)
		{
			PlaceSignature(sig, page, nx, ny);
		}
	}

	// --- selected text / signature / picture

	private void OnAnnotationSelectionChanged(Annotation? annotation)
	{
		// Ink and highlights only need delete / done; text, signatures and pictures get the full dock rows.
		AnnotationBar.IsVisible = annotation is InkAnnotation or MarkupAnnotation;
		if (annotation is TextBoxAnnotation or SignatureAnnotation or ImageAnnotation && !ToolDock.IsVisible) OpenDock();
		ApplyToolState();
	}

	private async void OnAnnEditClicked(object? sender, EventArgs e)
	{
		if (_session == null || _engine?.SelectedAnnotation is not TextBoxAnnotation text) return;
		string? edited = await this.PromptAsync("Sửa văn bản", "Nội dung:", "Lưu", "Huỷ", maxLength: 300, initialValue: text.Text);
		if (!string.IsNullOrWhiteSpace(edited)) _session.Annotations.Replace(text with { Text = edited });
	}

	private void OnFontClicked(object? sender, EventArgs e)
	{
		if (sender is not Button { CommandParameter: string family }) return;
		_textFont = family;
		if (_engine?.SelectedAnnotation is TextBoxAnnotation t) _session?.Annotations.Replace(t with { FontFamily = family });
		ApplyToolState();
	}

	private void OnBoldClicked(object? sender, EventArgs e)
	{
		if (_engine?.SelectedAnnotation is TextBoxAnnotation t)
		{
			_textBold = !t.Bold;
			_session?.Annotations.Replace(t with { Bold = _textBold });
		}
		ApplyToolState();
	}

	private void OnItalicClicked(object? sender, EventArgs e)
	{
		if (_engine?.SelectedAnnotation is TextBoxAnnotation t)
		{
			_textItalic = !t.Italic;
			_session?.Annotations.Replace(t with { Italic = _textItalic });
		}
		ApplyToolState();
	}

	private void OnAnnSmallerClicked(object? sender, EventArgs e) => ResizeSelected(1 / 1.15f);

	private void OnAnnLargerClicked(object? sender, EventArgs e) => ResizeSelected(1.15f);

	private void ResizeSelected(float factor)
	{
		if (_session == null || _engine == null) return;
		switch (_engine.SelectedAnnotation)
		{
			case TextBoxAnnotation t:
				_session.Annotations.Replace(t with { FontSize = Math.Clamp(t.FontSize * factor, 0.01f, 0.25f) });
				break;
			case SignatureAnnotation or ImageAnnotation:
				_engine.ScaleSelectedSignature(factor);
				break;
		}
	}

	private void OnAnnDeleteClicked(object? sender, EventArgs e) => _engine?.DeleteSelectedAnnotation();

	private void OnAnnDoneClicked(object? sender, EventArgs e) => _engine?.DeselectAnnotation();

	// --- signatures: a library of drawn and picture signatures

	private SavedSignature? ActiveSignature()
	{
		IReadOnlyList<SavedSignature> all = _signatures.List();
		return all.FirstOrDefault(s => s.Id == _activeSignature) ?? all.FirstOrDefault();
	}

	/// <summary>Puts a signature on a page, centred on (nx, ny), then leaves it selected so it can be moved,
	/// resized and turned straight away.</summary>
	private void PlaceSignature(SavedSignature signature, int page, float nx, float ny)
	{
		if (_session == null || _engine == null) return;
		try
		{
			Annotation placed = signature.PlaceAny(page, nx, ny, 0.30f, PageAspectOf(page), _penColor);
			_session.Annotations.Add(placed);
			_activeSignature = signature.Id;
			SetTool(ViewerTool.View);
			_engine.SelectAnnotation(placed.Id);
		}
		catch (Exception ex)
		{
			Android.Util.Log.Warn("PdfReader", $"placing signature failed: {ex.Message}");
			_ = ShowToastAsync("Không đặt được chữ ký này.");
		}
	}

	private void PlaceSignatureAtViewCenter(SavedSignature signature)
	{
		if (_engine != null && _engine.TryGetViewCenter(out int page, out float nx, out float ny)) PlaceSignature(signature, page, nx, ny);
	}

	/// <summary>Draws the little preview of a drawn signature in the dock.</summary>
	private sealed class SignatureThumbDrawable(SavedSignature signature) : IDrawable
	{
		public void Draw(ICanvas canvas, RectF rect)
		{
			canvas.StrokeColor = Color.FromArgb("#1A237E");
			canvas.StrokeSize = 1.8f;
			canvas.StrokeLineCap = LineCap.Round;
			canvas.StrokeLineJoin = LineJoin.Round;
			float pad = 4;
			float w = rect.Width - 2 * pad, h = rect.Height - 2 * pad;
			// Fit the signature's own aspect inside the box.
			float drawW = Math.Min(w, h * signature.Aspect), drawH = drawW / Math.Max(0.05f, signature.Aspect);
			float left = rect.X + pad + (w - drawW) / 2, top = rect.Y + pad + (h - drawH) / 2;
			foreach (IReadOnlyList<NPoint> stroke in signature.Strokes)
			{
				if (stroke.Count == 0) continue;
				var path = new PathF(left + stroke[0].X * drawW, top + stroke[0].Y * drawH);
				for (int i = 1; i < stroke.Count; i++) path.LineTo(left + stroke[i].X * drawW, top + stroke[i].Y * drawH);
				canvas.DrawPath(path);
			}
		}
	}

	/// <summary>Fills the dock's signature strip: one thumbnail per saved signature. Tap = place in the middle of
	/// the screen; hold and drag = drop it wherever on a page it is released; the small cross deletes it.</summary>
	private void RebuildSignatureList()
	{
		SigList.Clear();
		IReadOnlyList<SavedSignature> all = _signatures.List();
		if (all.Count == 0)
		{
			SigList.Add(new Label
			{
				Text = "Chưa có chữ ký. Chạm biểu tượng bút ký hoặc ảnh ở bên trái để tạo.",
				FontSize = 12,
				TextColor = (Color)Application.Current!.Resources["InkSoft"],
				VerticalOptions = LayoutOptions.Center,
				WidthRequest = 220,
			});
			return;
		}

		SavedSignature? active = ActiveSignature();
		foreach (SavedSignature sig in all)
		{
			SavedSignature captured = sig;
			View content = sig.IsImage
				? new Image { Source = ImageSource.FromFile(sig.ImageFile!), Aspect = Aspect.AspectFit, Margin = 5 }
				: new GraphicsView { Drawable = new SignatureThumbDrawable(sig), Margin = 2 };
			var card = new Border
			{
				Content = content,
				BackgroundColor = Colors.White,
				Stroke = captured.Id == active?.Id ? (Color)Application.Current!.Resources["Primary"] : Color.FromArgb("#D5CBB9"),
				StrokeThickness = captured.Id == active?.Id ? 2.5 : 1,
				StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
			};
			// The preview must not take touches itself (a GraphicsView would swallow them), or the tap and the
			// drag on the whole thumbnail never reach their recognizers.
			card.InputTransparent = true;
			var host = new Grid { WidthRequest = 96, HeightRequest = 54 };
			host.Add(card);

			var delete = new Button
			{
				Text = Icons.Close,
				FontFamily = "Icons",
				FontSize = 12,
				WidthRequest = 22,
				HeightRequest = 22,
				CornerRadius = 11,
				Padding = 0,
				MinimumWidthRequest = 0,
				MinimumHeightRequest = 0,
				BackgroundColor = Color.FromArgb("#CC3B3B3B"),
				TextColor = Colors.White,
				HorizontalOptions = LayoutOptions.End,
				VerticalOptions = LayoutOptions.Start,
				Margin = new Thickness(0, -2, -2, 0),
			};
			delete.Clicked += async (_, _) =>
			{
				bool ok = await this.AlertAsync("Xoá chữ ký", "Xoá chữ ký này khỏi danh sách? Các trang đã đặt nó không bị ảnh hưởng.", "Xoá", "Huỷ");
				if (!ok) return;
				_signatures.Delete(captured.Id);
				RebuildSignatureList();
			};
			host.Add(delete);

			var tap = new TapGestureRecognizer();
			tap.Tapped += (_, _) => PlaceSignatureAtViewCenter(captured);
			host.GestureRecognizers.Add(tap);

			var drag = new DragGestureRecognizer { CanDrag = true };
			drag.DragStarting += (_, e) =>
			{
				_dragSignature = captured;
				_activeSignature = captured.Id;
				e.Data.Text = captured.Id.ToString();
			};
			host.GestureRecognizers.Add(drag);

			SigList.Add(host);
		}
	}

	/// <summary>A signature dragged from the dock was let go over the document.</summary>
	private void OnViewerDrop(object? sender, DropEventArgs e)
	{
		SavedSignature? signature = _dragSignature;
		_dragSignature = null;
		if (signature == null || _engine == null) return;
		Point? where = e.GetPosition(Viewer);
		if (where == null) return;
		double density = DeviceDisplay.Current.MainDisplayInfo.Density;
		if (_engine.TryGetPageAt((float)(where.Value.X * density), (float)(where.Value.Y * density), out int page, out float nx, out float ny))
			PlaceSignature(signature, page, nx, ny);
	}

	private void OnSigDrawClicked(object? sender, EventArgs e) => ShowPad();

	private async void OnSigImageClicked(object? sender, EventArgs e) => await ImportSignatureImageAsync();

	private async Task ImportSignatureImageAsync()
	{
		try
		{
			(string File, float Aspect)? imported = await _signatureImages.PickAndImportAsync();
			if (imported == null) return;
			SavedSignature signature = SavedSignature.FromImage(imported.Value.File, imported.Value.Aspect);
			_signatures.Add(signature);
			_activeSignature = signature.Id;
			RebuildSignatureList();
		}
		catch (Exception ex)
		{
			Android.Util.Log.Warn("PdfReader", $"importing signature image failed: {ex}");
			await this.AlertAsync("Không dùng được ảnh này", ex.Message, "Đóng");
		}
	}

	// --- signature pad

	private sealed class PadDrawable : IDrawable
	{
		public List<List<PointF>> Strokes { get; } = [];

		public void Draw(ICanvas canvas, RectF dirtyRect)
		{
			Color ink = Color.FromArgb("#1A237E");
			canvas.StrokeColor = ink;
			canvas.StrokeSize = 3.5f;
			canvas.StrokeLineCap = LineCap.Round;
			canvas.StrokeLineJoin = LineJoin.Round;
			canvas.FillColor = ink;
			foreach (List<PointF> stroke in Strokes)
			{
				if (stroke.Count == 1)
				{
					canvas.FillCircle(stroke[0], 2);
					continue;
				}
				var path = new PathF(stroke[0]);
				for (int i = 1; i < stroke.Count; i++) path.LineTo(stroke[i]);
				canvas.DrawPath(path);
			}
		}
	}

	private void ShowPad()
	{
		_pad.Strokes.Clear();
		SignaturePad.Invalidate();
		SignaturePadOverlay.IsVisible = true;
	}

	private void HidePad() => SignaturePadOverlay.IsVisible = false;

	private void OnPadStart(object? sender, TouchEventArgs e)
	{
		_pad.Strokes.Add([e.Touches[0]]);
		SignaturePad.Invalidate();
	}

	private void OnPadDrag(object? sender, TouchEventArgs e)
	{
		if (_pad.Strokes.Count == 0) return;
		_pad.Strokes[^1].Add(e.Touches[0]);
		SignaturePad.Invalidate();
	}

	private void OnPadEnd(object? sender, TouchEventArgs e)
	{
		if (_pad.Strokes.Count == 0 || e.Touches.Length == 0) return;
		_pad.Strokes[^1].Add(e.Touches[0]);
		SignaturePad.Invalidate();
	}

	private void OnPadCancel(object? sender, EventArgs e) => SignaturePad.Invalidate();

	private void OnPadClearClicked(object? sender, EventArgs e)
	{
		_pad.Strokes.Clear();
		SignaturePad.Invalidate();
	}

	private void OnPadCancelClicked(object? sender, EventArgs e) => HidePad();

	private async void OnPadSaveClicked(object? sender, EventArgs e)
	{
		SavedSignature? signature = SavedSignature.FromStrokes(_pad.Strokes
			.Select(s => (IReadOnlyList<NPoint>)s.Select(p => new NPoint(p.X, p.Y)).ToList()).ToList());
		if (signature == null)
		{
			await ShowToastAsync("Chưa có nét nào");
			return;
		}
		_signatures.Add(signature);
		_activeSignature = signature.Id;
		HidePad();
		RebuildSignatureList();
	}

	// ------------------------------------------------------------------ PDF tools and export

	/// <summary>The file to run a structural tool on: the original, or a copy with the annotations baked in
	/// when there are some and the person wants them kept.</summary>
	private async Task<string?> SourceWithAnnotationsAsync()
	{
		if (_session == null) return null;
		if (_session.Annotations.Items.Count == 0) return _session.Path;
		bool keep = await this.AlertAsync("Chú thích", "Tài liệu có chú thích của bạn. Giữ chúng trong kết quả? (Các trang có chú thích sẽ được chuyển thành ảnh.)", "Giữ", "Bỏ");
		if (!keep) return _session.Path;
		string? baked = null;
		bool ok = await RunJobAsync("Đang áp dụng chú thích…", async p => baked = await _exporter.ExportAnnotatedAsync(_session, PdfQuality.Medium, p));
		return ok ? baked : null;
	}

	private async Task OpenPageManagerAsync()
	{
		string? source = await SourceWithAnnotationsAsync();
		if (source == null) return;
		await UiHost.Shell.GoToAsync(nameof(PageManagerPage), new Dictionary<string, object>
		{
			["source"] = source,
			["displayName"] = DisplayName ?? "PDF",
		});
	}

	private async void OnExportClicked(object? sender, EventArgs e) => await ExportAsync();

	/// <summary>Export with the edits baked in: asks for a name and a quality, saves the result straight into
	/// Downloads/PdfReader, and says only what it was called and how big it is.</summary>
	private async Task ExportAsync()
	{
		if (_session == null) return;
		if (_session.Annotations.Items.Count == 0)
		{
			await this.AlertAsync("Xuất PDF", "Chưa có chỉnh sửa nào để xuất. Hãy dùng bút, chữ hoặc chữ ký rồi xuất lại.", "Đóng");
			return;
		}
		(string Name, PdfQuality Quality)? options = await this.ExportDialogAsync("Xuất PDF", $"{BaseName}_chinh-sua", DefaultQuality());
		if (options == null) return;
		Preferences.Default.Set(SettingsPage.QualityKey, options.Value.Quality.Key);

		string fileName = ResultService.SafeName(options.Value.Name);
		if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) fileName += ".pdf";
		PdfQuality quality = options.Value.Quality;

		string? file = null;
		(string Name, long Bytes)? saved = null;
		bool ok = await RunJobAsync("Đang xuất PDF…", async p =>
		{
			file = await _exporter.ExportAnnotatedAsync(_session, quality, p);
			saved = await _results.SavePdfAsync(file, fileName);
		});
		if (!ok || file == null) return;

		if (saved is { } s)
		{
			await _results.NotifySavedAsync(file, fileName, s.Name, s.Bytes);
		}
		else
		{
			// Android 9 or older cannot save to Downloads without a storage permission: share instead.
			await _results.DeliverPdfAsync(file, fileName);
		}
	}

	private async Task SplitAsync()
	{
		if (_session == null) return;
		int count = _session.PageCount;
		string? text = await this.PromptAsync("Tách PDF", $"Tài liệu có {count} trang. Nhập các khoảng trang, mỗi khoảng thành một file riêng. Ví dụ: 1-3, 4-6, 7-",
			"Tách", "Huỷ", "1-3, 4-", initialValue: count > 1 ? $"1-{count / 2}, {count / 2 + 1}-" : "1");
		if (string.IsNullOrWhiteSpace(text)) return;

		IReadOnlyList<(int Start, int End)> ranges;
		try { ranges = PageRanges.Parse(text, count); }
		catch (FormatException ex)
		{
			await this.AlertAsync("Khoảng trang chưa đúng", ex.Message, "Đóng");
			return;
		}
		string? source = await SourceWithAnnotationsAsync();
		if (source == null) return;

		var parts = new List<(string File, string Name)>();
		bool ok = await RunJobAsync("Đang tách…", p => Task.Run(() =>
		{
			PdfStructureOps.Split(source, ranges, i =>
			{
				string file = _library.NewWorkFile();
				parts.Add((file, $"{BaseName}_trang-{ranges[i].Start + 1}-{ranges[i].End + 1}.pdf"));
				return file;
			});
		}));
		if (ok) await _results.SaveManyToDownloadsAsync(parts, "application/pdf");
	}

	private async Task CompressAsync()
	{
		if (_session == null) return;
		PdfQuality? quality = await AskQualityAsync();
		if (quality == null) return;
		string? file = null;
		bool ok = await RunJobAsync("Đang nén…", async p => file = await _exporter.CompressAsync(_session, quality, p));
		if (!ok || file == null) return;
		long before = new FileInfo(_session.Path).Length, after = new FileInfo(file).Length;
		if (after >= before)
		{
			bool keep = await this.AlertAsync("Nén PDF", $"Bản nén ({ResultService.FormatSize(after)}) không nhỏ hơn bản gốc ({ResultService.FormatSize(before)}) — file này có lẽ đã gọn. Vẫn giữ bản nén?", "Giữ", "Bỏ");
			if (!keep)
			{
				File.Delete(file);
				return;
			}
		}
		await _results.DeliverPdfAsync(file, $"{BaseName}_nen.pdf");
	}

	private async Task MakeSearchableAsync()
	{
		if (_session == null) return;
		bool go = await this.AlertAsync("PDF tìm kiếm được", "Ứng dụng sẽ nhận dạng chữ (OCR) ở những trang là ảnh scan, rồi lưu thành PDF mới có thể chọn, sao chép và tìm chữ. Trang đã có chữ được giữ nguyên. Việc này có thể mất vài phút với tài liệu dài.", "Bắt đầu", "Huỷ");
		if (!go) return;
		PdfQuality? quality = await AskQualityAsync();
		if (quality == null) return;

		string? file = null;
		bool ok = await RunJobAsync("Nhận dạng chữ…", async p => file = await _exporter.MakeSearchableAsync(_session, quality, p));
		if (!ok) return;
		if (file == null)
		{
			await this.AlertAsync("PDF tìm kiếm được", "Mọi trang đã có chữ, hoặc không nhận dạng được chữ nào — không cần tạo bản mới.", "Đóng");
			return;
		}
		await _results.DeliverPdfAsync(file, $"{BaseName}_ocr.pdf");
	}

	private async Task PagesToImagesAsync()
	{
		if (_session == null || _engine == null) return;
		string? text = await this.PromptAsync("Chuyển trang thành ảnh", $"Nhập các trang (1–{_session.PageCount}). Ví dụ: 1-3, 5",
			"Tiếp", "Huỷ", initialValue: $"{_engine.CurrentPage + 1}");
		if (string.IsNullOrWhiteSpace(text)) return;
		IReadOnlyList<int> pages;
		try { pages = PageRanges.Flatten(PageRanges.Parse(text, _session.PageCount)); }
		catch (FormatException ex)
		{
			await this.AlertAsync("Trang chưa đúng", ex.Message, "Đóng");
			return;
		}
		string? format = await this.ChoiceAsync("Định dạng ảnh", "Huỷ", null, "PNG (nét, nặng hơn)", "JPG (nhẹ hơn)");
		if (format == null) return;
		bool png = format.StartsWith("PNG");

		IReadOnlyList<string> files = [];
		bool ok = await RunJobAsync("Đang chuyển thành ảnh…", async p => files = await _exporter.PagesToImagesAsync(_session, pages, png, 150, p));
		if (!ok) return;
		string ext = png ? "png" : "jpg";
		var named = files.Select((f, i) => (f, $"{BaseName}_trang-{pages[i] + 1}.{ext}")).ToList();
		await _results.SaveManyToDownloadsAsync(named, png ? "image/png" : "image/jpeg");
	}

	private async Task ProtectAsync()
	{
		if (_session == null) return;
		string? password = await this.PromptAsync("Đặt mật khẩu", "Nhập mật khẩu để mở bản PDF mới. Hãy nhớ nó — không có cách khôi phục.", "Tạo", "Huỷ", "Mật khẩu", maxLength: 64);
		if (string.IsNullOrEmpty(password)) return;
		string? source = await SourceWithAnnotationsAsync();
		if (source == null) return;
		string? file = null;
		bool ok = await RunJobAsync("Đang mã hoá…", async _ => file = await _exporter.ProtectAsync(source, password));
		if (ok && file != null) await _results.DeliverPdfAsync(file, $"{BaseName}_mat-khau.pdf");
	}

	private void AddPageNumbers()
	{
		if (_session == null) return;
		var added = new List<Annotation>();
		for (int i = 0; i < _session.PageCount; i++)
		{
			string label = $"{i + 1}";
			const float size = 0.026f;
			added.Add(new TextBoxAnnotation(Guid.NewGuid(), i, label, 0.5f - label.Length * size * 0.55f / 2, 0.94f, size, 0xFF424242));
		}
		_session.Annotations.AddRange(added);
		_ = ShowToastAsync("Đã thêm số trang. Dùng Hoàn tác để bỏ.");
	}

	private async Task AddWatermarkAsync()
	{
		if (_session == null) return;
		string? text = await this.PromptAsync("Thêm watermark", "Chữ chìm đặt chéo giữa mỗi trang:", "Thêm", "Huỷ", "VD: BẢN SAO", maxLength: 40);
		if (string.IsNullOrWhiteSpace(text)) return;
		var added = new List<Annotation>();
		const float size = 0.085f;
		for (int i = 0; i < _session.PageCount; i++)
		{
			float aspect = PageAspectOf(i);
			float w = text.Length * size * 0.55f, h = size * TextBoxAnnotation.LineHeightFactor / aspect;
			added.Add(new TextBoxAnnotation(Guid.NewGuid(), i, text, Math.Clamp(0.5f - w / 2, 0, 1), 0.5f - h / 2, size, 0x40616161, RotationDeg: -35, Bold: true));
		}
		_session.Annotations.AddRange(added);
		await ShowToastAsync("Đã thêm watermark. Dùng Hoàn tác để bỏ.");
	}

	private async Task ClearAnnotationsAsync()
	{
		if (_session == null || _session.Annotations.Items.Count == 0)
		{
			await ShowToastAsync("Chưa có chú thích nào");
			return;
		}
		bool ok = await this.AlertAsync("Xoá tất cả chú thích", $"Xoá {_session.Annotations.Items.Count} chú thích trên tài liệu này? Vẫn có thể Hoàn tác.", "Xoá", "Huỷ");
		if (ok) _session.Annotations.Clear();
	}
}
