using Android.Animation;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using PdfReader.Core.Annotations;
using PdfReader.Core.Text;
using PdfReader.Services;
using AView = Android.Views.View;
using AColor = global::Android.Graphics.Color;
using Paint = Android.Graphics.Paint;
using RectF = Android.Graphics.RectF;

namespace PdfReader.Platforms.Android;

public enum ViewerTool { View, Pen, Marker, Text, Signature }

public enum ReadTheme { Light, Dark, Sepia }

/// <summary>
/// The PDF reading surface: one native view that draws every page of the document as a continuous
/// vertical strip, with pinch-zoom, pan, fling, double-tap zoom, text selection, search highlights and the
/// annotation tools. A single view owns all touch handling because zoom/pan, drawing a stroke and
/// selecting text all want the same fingers, and sorting that out across separate MAUI layers does not work.
///
/// How it stays smooth on long documents: only the visible pages (plus a neighbour either side) have
/// bitmaps; they are rendered on one background thread, nearest-to-screen first, and a render request is
/// dropped as soon as the user has moved on. While zoomed in, a sharp bitmap of just the part of the page
/// on screen is rendered on top of the (blurrier) whole-page one, so zoom stays crisp without ever
/// rendering a whole page at 6x. Coordinates: "doc" = the strip at zoom 1 (page width = view width);
/// screen = doc * scale + translation. Annotations and text live in page-normalised coordinates.
/// </summary>
public sealed class PdfCanvasView : AView
{
	private const float MaxScale = 6f;

	private readonly float _density;
	private readonly ScaleGestureDetector _scaleDetector;
	private readonly GestureDetector _gestureDetector;
	private readonly OverScroller _scroller;
	private readonly Paint _pagePaint = new(PaintFlags.FilterBitmap);
	private readonly Paint _fill = new(PaintFlags.AntiAlias);
	private readonly Paint _stroke = new(PaintFlags.AntiAlias);
	private readonly SemaphoreSlim _renderGate = new(1, 1);
	private readonly Dictionary<int, Bitmap> _base = [];
	private readonly Dictionary<int, Detail> _detail = [];
	private readonly Java.Lang.Runnable _planRunnable;

	private PdfSession? _s;
	private float _vw, _vh;

	/// <summary>Side margin around the pages (dp -> px) and the width a page gets after it.</summary>
	private float _side, _pw;
	private float[] _top = [], _h = [];
	private float _docH;
	private float _scale = 1f, _tx, _ty;
	private volatile int _gen;
	private bool _measured;
	private bool _disposed;
	private int _currentPage = -1;
	private ValueAnimator? _zoomAnim;

	// tool state
	private int _strokePage = -1;
	private List<NPoint>? _liveStroke;
	private bool _fingerDown, _multiTouch;
	private float _lastCx, _lastCy;

	// text selection
	private PageText? _selText;
	private int _selPage = -1, _selA, _selB;
	private bool _selecting;

	// annotation selection / drag
	private Guid? _selAnn;
	private bool _draggingAnn;
	private float _dragLastX, _dragLastY;

	// search
	private IReadOnlyList<SearchHit> _hits = [];
	private int _hitIndex = -1;

	public PdfCanvasView(Context context) : base(context)
	{
		_density = context.Resources!.DisplayMetrics!.Density;
		_scaleDetector = new ScaleGestureDetector(context, new ScaleListener(this)) { QuickScaleEnabled = false };
		_gestureDetector = new GestureDetector(context, new GestureListener(this));
		_scroller = new OverScroller(context);
		_planRunnable = new Java.Lang.Runnable(PlanRender);
		SetBackgroundColor(AColor.Transparent);
		ApplyTheme();
	}

	// ------------------------------------------------------------------ public API

	public ViewerTool Tool { get; set; } = ViewerTool.View;

	/// <summary>ARGB of the pen / marker / new text box.</summary>
	public uint InkColor { get; set; } = 0xFFD32F2F;

	/// <summary>Pen thickness as a fraction of the page width.</summary>
	public float PenWidth { get; set; } = 0.005f;

	private ReadTheme _theme = ReadTheme.Light;

	public ReadTheme Theme
	{
		get => _theme;
		set
		{
			_theme = value;
			ApplyTheme();
			Invalidate();
		}
	}

	public int CurrentPage => Math.Max(0, _currentPage);

	public int PageCount => _s?.PageCount ?? 0;

	public bool HasTextSelection => _selText != null;

	public string SelectedText => _selText == null ? "" : _selText.Join(_selA, _selB);

	public Annotation? SelectedAnnotation => _selAnn is { } id ? _s?.Annotations.Find(id) : null;

	public event Action<int>? PageChanged;
	public event Action? Tapped;
	public event Action<ViewerTool, int, float, float>? PlaceRequested;
	public event Action<bool>? TextSelectionChanged;
	public event Action<Annotation?>? AnnotationSelectionChanged;
	public event Action<string?>? Busy;

	/// <summary>A short message for the person (e.g. no text found on the page); the page shows it.</summary>
	public event Action<string>? Message;

	/// <summary>Shows <paramref name="session"/>, starting at <paramref name="startPage"/>.</summary>
	public void Load(PdfSession session, int startPage)
	{
		Unload();
		_s = session;
		_currentPage = -1;
		_measured = false;
		_s.Annotations.Changed += OnAnnotationsChanged;
		_top = new float[session.PageCount];
		_h = new float[session.PageCount];
		Relayout();
		_pendingStartPage = startPage;
		_ = MeasureAsync(session);
	}

	private int? _pendingStartPage;

	/// <summary>Jumps to the page the document was last read at, once both the real page sizes and the
	/// view size are known (either can arrive first).</summary>
	private void ApplyPendingStart()
	{
		if (_pendingStartPage is not { } p || !_measured || _vw <= 0 || _s == null) return;
		// A start page past the measured head waits until every page height is known, or it would land wrong.
		if (p >= _s.MeasuredCount && !_s.FullyMeasured) return;
		_pendingStartPage = null;
		GoToPage(p);
	}

	private async Task MeasureAsync(PdfSession session)
	{
		// The first pages are measured and shown at once; the rest of a long document is measured behind them.
		try
		{
			await session.MeasureHeadAsync();
		}
		catch (Exception ex)
		{
			global::Android.Util.Log.Warn("PdfReader", $"measuring pages failed: {ex.Message}");
		}
		if (_disposed || _s != session) return;
		_measured = true;
		Relayout();
		ApplyPendingStart();
		NotifyPage();
		RequestRender();
		Invalidate();

		if (session.FullyMeasured) return;
		try
		{
			await session.MeasureAllAsync();
		}
		catch (Exception ex)
		{
			global::Android.Util.Log.Warn("PdfReader", $"measuring pages failed: {ex.Message}");
			return;
		}
		if (_disposed || _s != session) return;
		(int page, float frac) = TopAnchor();
		Relayout();
		RestoreAnchor(page, frac);
		ApplyPendingStart();
		NotifyPage();
		RequestRender();
		Invalidate();
	}

	public void GoToPage(int page)
	{
		if (_s == null || _top.Length == 0) return;
		page = Math.Clamp(page, 0, _top.Length - 1);
		_scroller.ForceFinished(true);
		_ty = -(page == 0 ? 0 : _top[page] - Gap / 2) * _scale;
		Clamp();
		AfterMove();
	}

	/// <summary>Scrolls so that <paramref name="rect"/> of <paramref name="page"/> is comfortably visible.</summary>
	public void ScrollToRect(int page, NRect rect)
	{
		if (_s == null || page < 0 || page >= _top.Length) return;
		_scroller.ForceFinished(true);
		float docY = _top[page] + rect.CenterY * _h[page];
		float docX = _side + rect.CenterX * _pw;
		_ty = _vh * 0.35f - docY * _scale;
		if (_scale > 1.01f) _tx = _vw / 2 - docX * _scale;
		Clamp();
		AfterMove();
	}

	public void SetSearchHits(IReadOnlyList<SearchHit> hits, int current)
	{
		_hits = hits;
		_hitIndex = hits.Count == 0 ? -1 : Math.Clamp(current, 0, hits.Count - 1);
		if (_hitIndex >= 0) ScrollToRect(_hits[_hitIndex].Page, NRect.UnionAll(_hits[_hitIndex].Rects));
		Invalidate();
	}

	public void SetCurrentHit(int index)
	{
		if (_hits.Count == 0) return;
		_hitIndex = Math.Clamp(index, 0, _hits.Count - 1);
		ScrollToRect(_hits[_hitIndex].Page, NRect.UnionAll(_hits[_hitIndex].Rects));
		Invalidate();
	}

	public void ClearTextSelection()
	{
		if (_selText == null) return;
		_selText = null;
		_selecting = false;
		TextSelectionChanged?.Invoke(false);
		Invalidate();
	}

	public void SelectAnnotation(Guid id)
	{
		Annotation? a = _s?.Annotations.Find(id);
		if (a == null) return;
		ClearTextSelection();
		_selAnn = id;
		AnnotationSelectionChanged?.Invoke(a);
		Invalidate();
	}

	public void DeselectAnnotation()
	{
		if (_selAnn == null) return;
		_selAnn = null;
		AnnotationSelectionChanged?.Invoke(null);
		Invalidate();
	}

	public void DeleteSelectedAnnotation()
	{
		if (_selAnn is { } id) _s?.Annotations.Remove(id);
		DeselectAnnotation();
	}

	/// <summary>Turns the selected text into a highlight / underline / strikeout / redaction.</summary>
	public void ApplyMarkup(MarkupKind kind, uint color)
	{
		if (_s == null || _selText == null) return;
		var rects = _selText.RectsFor(_selA, _selB).Select(r => r.Inflate(0.001f, 0.0005f)).ToList();
		_s.Annotations.Add(new MarkupAnnotation(Guid.NewGuid(), _selPage, kind, color, rects));
		ClearTextSelection();
	}

	/// <summary>Selects every word of the page on screen, running OCR first when it is a scan.</summary>
	public async Task SelectAllOnPageAsync()
	{
		if (_s == null) return;
		int page = CurrentPage;
		PageText? text = await LoadTextAsync(page);
		if (text == null || !text.IsUsable) return;
		_selText = text;
		_selPage = page;
		_selA = 0;
		_selB = text.Words.Count - 1;
		TextSelectionChanged?.Invoke(true);
		Invalidate();
	}

	public void ScaleSelectedSignature(float factor)
	{
		if (_s == null) return;
		switch (SelectedAnnotation)
		{
			case SignatureAnnotation sig:
				_s.Annotations.Replace(sig.Scaled(factor, PageAspect(sig.Page)));
				break;
			case ImageAnnotation img:
				_s.Annotations.Replace(img.Scaled(factor));
				break;
		}
	}

	public void Unload()
	{
		_gen++;
		_zoomAnim?.Cancel();
		RemoveCallbacks(_planRunnable);
		_planScheduled = false;
		if (_s != null) _s.Annotations.Changed -= OnAnnotationsChanged;
		foreach (Bitmap b in _base.Values) Retire(b);
		_base.Clear();
		foreach (Detail d in _detail.Values) Retire(d.Bitmap);
		_detail.Clear();
		_s = null;
		_selText = null;
		_selAnn = null;
		_hits = [];
		_scale = 1f;
		_tx = _ty = 0;
		_currentPage = -1;
	}

	// ------------------------------------------------------------------ layout

	private float Gap => 10 * _density;

	protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
	{
		base.OnSizeChanged(w, h, oldw, oldh);
		(int page, float frac) = TopAnchor();
		_vw = w;
		_side = 5 * _density;
		_pw = Math.Max(1, _vw - 2 * _side);
		_vh = h;
		Relayout();
		RestoreAnchor(page, frac);
		ApplyPendingStart();
		RequestRender();
	}

	/// <summary>Which page is at the top of the screen and how far down it, so a relayout can keep it there.</summary>
	private (int Page, float Fraction) TopAnchor()
	{
		if (_s == null || _top.Length == 0 || _vw <= 0) return (0, 0);
		float docY = -_ty / _scale;
		int p = PageAtDocY(docY);
		return (p, _h[p] <= 0 ? 0 : (docY - _top[p]) / _h[p]);
	}

	private void RestoreAnchor(int page, float fraction)
	{
		if (_s == null || _top.Length == 0) return;
		page = Math.Clamp(page, 0, _top.Length - 1);
		_ty = -(_top[page] + fraction * _h[page]) * _scale;
		Clamp();
	}

	private void Relayout()
	{
		if (_s == null) return;
		(int Width, int Height)[]? sizes = _s.PageSizes;
		// The first page sits flush against the top of the view (no empty strip above it); pages are
		// separated by Gap, with one more Gap under the last.
		float y = 0;
		// Pages not measured yet are assumed to be as tall as the first one.
		float fallback = sizes != null && sizes.Length > 0 && sizes[0].Width > 0 ? (float)sizes[0].Height / sizes[0].Width : 1.414f;
		for (int i = 0; i < _top.Length; i++)
		{
			float ratio = sizes != null && sizes[i].Width > 0 ? (float)sizes[i].Height / sizes[i].Width : fallback;
			_top[i] = y;
			_h[i] = _pw * ratio;
			y += _h[i] + Gap;
		}
		_docH = y;
		Clamp();
	}

	private float PageAspect(int page) => _vw <= 0 || page < 0 || page >= _h.Length ? 1.414f : _h[page] / _pw;

	private int PageAtDocY(float docY)
	{
		if (_top.Length == 0) return 0;
		int i = Array.BinarySearch(_top, docY);
		if (i < 0) i = ~i - 1;
		return Math.Clamp(i, 0, _top.Length - 1);
	}

	private void Clamp()
	{
		_scale = Math.Clamp(_scale, 1f, MaxScale);
		(float minX, float maxX, float minY, float maxY) = Bounds();
		_tx = Math.Clamp(_tx, minX, maxX);
		_ty = Math.Clamp(_ty, minY, maxY);
	}

	private (float MinX, float MaxX, float MinY, float MaxY) Bounds()
	{
		float contentW = _vw * _scale, contentH = _docH * _scale;
		float minX = Math.Min(0, _vw - contentW), maxX = 0;
		if (contentW <= _vw) minX = maxX = (_vw - contentW) / 2;
		float minY = Math.Min(0, _vh - contentH), maxY = 0;
		return (minX, maxX, minY, maxY);
	}

	private RectF PageRect(int i) =>
		new(_tx + _side * _scale, _ty + _top[i] * _scale, _tx + (_vw - _side) * _scale, _ty + (_top[i] + _h[i]) * _scale);

	private (int First, int Last) VisiblePages()
	{
		if (_top.Length == 0) return (0, -1);
		int first = PageAtDocY(-_ty / _scale), last = PageAtDocY((_vh - _ty) / _scale);
		return (first, last);
	}

	private bool ScreenToPage(float sx, float sy, out int page, out float nx, out float ny)
	{
		page = 0;
		nx = ny = 0;
		if (_s == null || _top.Length == 0 || _vw <= 0) return false;
		float docX = (sx - _tx) / _scale, docY = (sy - _ty) / _scale;
		page = PageAtDocY(docY);
		if (docY < _top[page] || docY > _top[page] + _h[page] || docX < _side || docX > _vw - _side) return false;
		nx = (docX - _side) / _pw;
		ny = (docY - _top[page]) / _h[page];
		return true;
	}

	/// <summary>Position on <paramref name="page"/> clamped to it, even when the finger has left the page.</summary>
	private NPoint ScreenToPageClamped(float sx, float sy, int page)
	{
		float nx = (((sx - _tx) / _scale) - _side) / _pw;
		float ny = (((sy - _ty) / _scale) - _top[page]) / _h[page];
		return new NPoint(Math.Clamp(nx, 0, 1), Math.Clamp(ny, 0, 1));
	}

	// ------------------------------------------------------------------ drawing

	private void ApplyTheme()
	{
		switch (_theme)
		{
			case ReadTheme.Dark:
				_pagePaint.SetColorFilter(new ColorMatrixColorFilter(new ColorMatrix(new float[]
				{
					-0.88f, 0, 0, 0, 228,
					0, -0.88f, 0, 0, 228,
					0, 0, -0.88f, 0, 228,
					0, 0, 0, 1, 0,
				})));
				break;
			case ReadTheme.Sepia:
				_pagePaint.SetColorFilter(new ColorMatrixColorFilter(new ColorMatrix(new float[]
				{
					0.957f, 0, 0, 0, 0,
					0, 0.925f, 0, 0, 0,
					0, 0, 0.80f, 0, 0,
					0, 0, 0, 1, 0,
				})));
				break;
			default:
				_pagePaint.SetColorFilter(null);
				break;
		}
	}

	private AColor BackgroundColor() => _theme switch
	{
		ReadTheme.Dark => new AColor(0x0E, 0x0E, 0x0E),
		ReadTheme.Sepia => new AColor(0xC9, 0xBD, 0x9E),
		_ => new AColor(0xD9, 0xD3, 0xC9),
	};

	protected override void OnDraw(Canvas canvas)
	{
		base.OnDraw(canvas);
		// MAUI layouts do not clip their children, so without this the pages scrolled above the top edge of this
		// view are painted over the banner and title bar above it.
		canvas.ClipRect(0f, 0f, Width, Height);
		canvas.DrawColor(BackgroundColor());
		if (_s == null || _top.Length == 0) return;

		(int first, int last) = VisiblePages();
		for (int i = first; i <= last; i++)
		{
			RectF pr = PageRect(i);
			if (_base.TryGetValue(i, out Bitmap? bmp) && !bmp.IsRecycled)
				canvas.DrawBitmap(bmp, null, pr, _pagePaint);
			else DrawPlaceholder(canvas, pr, i);

			if (_detail.TryGetValue(i, out Detail? d) && !d.Bitmap.IsRecycled)
			{
				var dst = new RectF(_tx + d.Doc.Left * _scale, _ty + d.Doc.Top * _scale, _tx + d.Doc.Right * _scale, _ty + d.Doc.Bottom * _scale);
				canvas.DrawBitmap(d.Bitmap, null, dst, _pagePaint);
			}

			int save = canvas.Save();
			canvas.ClipRect(pr);
			AnnotationPainter.Draw(canvas, _s.Annotations.OnPage(i), pr);
			DrawOverlays(canvas, i, pr);
			canvas.RestoreToCount(save);
		}
	}

	private void DrawPlaceholder(Canvas canvas, RectF pr, int page)
	{
		_fill.SetStyle(Paint.Style.Fill);
		_fill.Color = _theme == ReadTheme.Dark ? new AColor(0x24, 0x24, 0x24) : _theme == ReadTheme.Sepia ? new AColor(0xF4, 0xEC, 0xD8) : AColor.White;
		canvas.DrawRect(pr, _fill);
		_fill.Color = new AColor(0x88, 0x80, 0x70);
		_fill.TextSize = 16 * _density;
		_fill.TextAlign = Paint.Align.Center;
		float cy = Math.Clamp(pr.CenterY(), 40 * _density, _vh - 40 * _density);
		if (cy > pr.Top && cy < pr.Bottom) canvas.DrawText($"{page + 1}", pr.CenterX(), cy, _fill);
		_fill.TextAlign = Paint.Align.Left;
	}

	private void DrawOverlays(Canvas canvas, int page, RectF pr)
	{
		_fill.SetStyle(Paint.Style.Fill);

		// search hits
		for (int h = 0; h < _hits.Count; h++)
		{
			if (_hits[h].Page != page) continue;
			_fill.Color = h == _hitIndex ? new AColor(unchecked((int)0x99FF9800)) : new AColor(unchecked((int)0x66FFEB3B));
			foreach (NRect r in _hits[h].Rects) canvas.DrawRect(AnnotationPainter.ToRect(r.Inflate(0.001f, 0.0005f), pr), _fill);
		}

		// text selection
		if (_selText != null && _selPage == page)
		{
			_fill.Color = new AColor(unchecked((int)0x553B82F6));
			foreach (NRect r in _selText.RectsFor(_selA, _selB)) canvas.DrawRect(AnnotationPainter.ToRect(r, pr), _fill);
		}

		// selected annotation outline
		if (SelectedAnnotation is { } sel && sel.Page == page)
		{
			RectF box = sel is TextBoxAnnotation tb ? AnnotationPainter.MeasureText(tb, pr) : AnnotationPainter.ToRect(sel.Bounds(PageAspect(page)), pr);
			box.Inset(-4 * _density, -4 * _density);
			_stroke.SetStyle(Paint.Style.Stroke);
			_stroke.Color = new AColor(0x3B, 0x82, 0xF6);
			_stroke.StrokeWidth = 1.5f * _density;
			_stroke.SetPathEffect(new DashPathEffect([6 * _density, 4 * _density], 0));
			int outlineSave = canvas.Save();
			if (Math.Abs(sel.Rotation) > 0.01f) canvas.Rotate(sel.Rotation, box.CenterX(), box.CenterY());
			canvas.DrawRect(box, _stroke);
			canvas.RestoreToCount(outlineSave);
			_stroke.SetPathEffect(null);
		}

		// stroke being drawn
		if (_liveStroke != null && _strokePage == page)
		{
			bool marker = Tool == ViewerTool.Marker;
			uint color = marker ? (InkColor & 0x00FFFFFFu) | 0x66000000u : InkColor;
			AnnotationPainter.DrawLiveStroke(canvas, _liveStroke, marker ? MarkerWidth : PenWidth, color, marker, pr);
		}
	}

	/// <summary>Highlighter thickness as a fraction of the page width.</summary>
	public float MarkerWidth { get; set; } = 0.028f;

	private static readonly global::Android.OS.Handler MainHandler = new(global::Android.OS.Looper.MainLooper!);

	/// <summary>Frees a bitmap a moment later rather than now: the render thread may still be drawing a frame
	/// that references it, and recycling underneath it can crash the app.</summary>
	private static void Retire(Bitmap bitmap) => MainHandler.PostDelayed(() => bitmap.Recycle(), 800);

	private void OnAnnotationsChanged()
	{
		if (_selAnn is { } id && _s?.Annotations.Find(id) == null)
		{
			_selAnn = null;
			AnnotationSelectionChanged?.Invoke(null);
		}
		Invalidate();
	}

	// ------------------------------------------------------------------ rendering

	private sealed class Detail(Bitmap bitmap, float scale, RectF doc)
	{
		public Bitmap Bitmap { get; } = bitmap;
		public float Scale { get; } = scale;
		/// <summary>Where this bitmap sits, in doc coordinates (zoom 1).</summary>
		public RectF Doc { get; } = doc;
	}

	private abstract record RenderJob(PdfSession Session, int Page);

	private sealed record BaseJob(PdfSession Session, int Page, int WidthPx) : RenderJob(Session, Page);

	private sealed record DetailJob(PdfSession Session, int Page, float Scale, RectF Doc, double PxPerPoint, double LeftPt, double TopPt, int W, int H) : RenderJob(Session, Page);

	/// <summary>Asks for a render pass soon. Throttled, not debounced: while the finger keeps moving a pass
	/// still runs every ~50 ms, so pages fill in as they scroll into view instead of only after stopping.</summary>
	private void RequestRender()
	{
		if (_planScheduled) return;
		_planScheduled = true;
		PostDelayed(_planRunnable, 50);
	}

	private bool _planScheduled;

	private void PlanRender()
	{
		_planScheduled = false;
		if (_disposed || _s == null || !_measured || _vw <= 0) return;
		// Mid-fling at high speed the pages on screen are about to be gone: render nothing until it slows.
		if (!_scroller.IsFinished && _scroller.CurrVelocity > 2500)
		{
			RequestRender();
			return;
		}
		PdfSession session = _s;
		(int first, int last) = VisiblePages();

		// Free what is far away.
		foreach (int p in _base.Keys.Where(p => p < first - 2 || p > last + 2).ToList())
		{
			Retire(_base[p]);
			_base.Remove(p);
		}
		foreach (int p in _detail.Keys.Where(p => p < first || p > last).ToList())
		{
			Retire(_detail[p].Bitmap);
			_detail.Remove(p);
		}

		var jobs = new List<RenderJob>();
		int baseW = (int)Math.Clamp(_pw * 1.0f, 200, 1100);
		for (int p = first; p <= last; p++)
			if (!_base.ContainsKey(p)) jobs.Add(new BaseJob(session, p, baseW));

		if (_scale > 1.25f && session.PageSizes is { } sizes)
		{
			float viewLeft = -_tx / _scale, viewTop = -_ty / _scale, viewRight = (_vw - _tx) / _scale, viewBottom = (_vh - _ty) / _scale;
			for (int p = first; p <= last; p++)
			{
				float pageTop = _top[p], pageBottom = _top[p] + _h[p];
				// Needed: what is on screen now. Rendered: that plus a margin, so a small pan stays sharp.
				if (sizes[p].Width <= 0) continue; // not measured yet
				var need = new RectF(Math.Max(_side, viewLeft), Math.Max(pageTop, viewTop), Math.Min(_vw - _side, viewRight), Math.Min(pageBottom, viewBottom));
				if (need.Width() <= 0 || need.Height() <= 0) continue;
				if (_detail.TryGetValue(p, out Detail? have) && Math.Abs(have.Scale / _scale - 1) < 0.12f && Contains(have.Doc, need)) continue;

				float mx = (viewRight - viewLeft) * 0.15f, my = (viewBottom - viewTop) * 0.15f;
				var doc = new RectF(Math.Max(_side, need.Left - mx), Math.Max(pageTop, need.Top - my), Math.Min(_vw - _side, need.Right + mx), Math.Min(pageBottom, need.Bottom + my));
				double pxPerPoint = _pw * _scale / sizes[p].Width;
				double leftPt = (doc.Left - _side) * _scale / pxPerPoint, topPt = (doc.Top - pageTop) * _scale / pxPerPoint;
				int w = Math.Max(1, (int)Math.Round(doc.Width() * _scale)), h = Math.Max(1, (int)Math.Round(doc.Height() * _scale));
				jobs.Add(new DetailJob(session, p, _scale, doc, pxPerPoint, leftPt, topPt, w, h));
			}
		}

		// Neighbours last, so they never delay what is on screen.
		for (int p = Math.Max(0, first - 1); p <= Math.Min(_top.Length - 1, last + 1); p++)
			if ((p < first || p > last) && !_base.ContainsKey(p)) jobs.Add(new BaseJob(session, p, baseW));

		int gen = ++_gen;
		if (jobs.Count == 0) return;
		_ = Task.Run(() => RunJobs(gen, jobs));
	}

	private static bool Contains(RectF outer, RectF inner) =>
		outer.Left <= inner.Left + 1 && outer.Top <= inner.Top + 1 && outer.Right >= inner.Right - 1 && outer.Bottom >= inner.Bottom - 1;

	private async Task RunJobs(int gen, List<RenderJob> jobs)
	{
		await _renderGate.WaitAsync();
		try
		{
			foreach (RenderJob job in jobs)
			{
				if (gen != _gen || _disposed) return;
				Bitmap? bitmap = null;
				try
				{
					bitmap = job switch
					{
						BaseJob b => b.Session.Pages.RenderFitWidth(b.Page, b.WidthPx),
						DetailJob d => d.Session.Pages.RenderRegion(d.Page, d.PxPerPoint, d.LeftPt, d.TopPt, d.W, d.H),
						_ => null,
					};
				}
				catch (Exception ex)
				{
					global::Android.Util.Log.Warn("PdfReader", $"render page {job.Page + 1} failed: {ex.Message}");
				}
				if (bitmap == null) continue;
				if (_disposed)
				{
					bitmap.Recycle();
					return;
				}
				// Delivered even when a newer plan has superseded this one: the work is done, and the next
				// plan evicts whatever turned out not to be needed.
				Bitmap ready = bitmap;
				Post(() => Deliver(job, ready));
			}
		}
		finally { _renderGate.Release(); }
	}

	private void Deliver(RenderJob job, Bitmap bitmap)
	{
		if (_disposed || _s != job.Session)
		{
			bitmap.Recycle();
			return;
		}
		// A page that has already scrolled far away by the time its picture is ready is not worth keeping: it was
		// never drawn, so it can be freed at once (this is what keeps a fast fling through a long document from
		// piling up bitmaps).
		(int first, int last) = VisiblePages();
		if (job is BaseJob && (job.Page < first - 2 || job.Page > last + 2))
		{
			bitmap.Recycle();
			return;
		}
		switch (job)
		{
			case BaseJob b:
				if (_base.Remove(b.Page, out Bitmap? old)) Retire(old);
				_base[b.Page] = bitmap;
				break;
			case DetailJob d:
				if (_detail.Remove(d.Page, out Detail? oldDetail)) Retire(oldDetail.Bitmap);
				_detail[d.Page] = new Detail(bitmap, d.Scale, d.Doc);
				break;
		}
		Invalidate();
	}

	// ------------------------------------------------------------------ movement

	private void AfterMove()
	{
		NotifyPage();
		RequestRender();
		Invalidate();
	}

	private void NotifyPage()
	{
		if (_s == null || _top.Length == 0) return;
		int page = PageAtDocY((_vh * 0.4f - _ty) / _scale);
		if (page == _currentPage) return;
		_currentPage = page;
		PageChanged?.Invoke(page);
	}

	public override void ComputeScroll()
	{
		base.ComputeScroll();
		if (_scroller.ComputeScrollOffset())
		{
			_tx = _scroller.CurrX;
			_ty = _scroller.CurrY;
			Clamp();
			NotifyPage();
			RequestRender();
			PostInvalidateOnAnimation();
		}
	}

	private void AnimateZoomTo(float targetScale, float focusX, float focusY)
	{
		_zoomAnim?.Cancel();
		float startScale = _scale, startTx = _tx, startTy = _ty;
		targetScale = Math.Clamp(targetScale, 1f, MaxScale);
		// Keep the doc point under the focus fixed.
		float docX = (focusX - startTx) / startScale, docY = (focusY - startTy) / startScale;
		float endTx = focusX - docX * targetScale, endTy = focusY - docY * targetScale;
		var anim = ValueAnimator.OfFloat(0f, 1f)!;
		anim.SetDuration(180);
		anim.Update += (_, e) =>
		{
			float t = e.Animation!.AnimatedFraction;
			_scale = startScale + (targetScale - startScale) * t;
			_tx = startTx + (endTx - startTx) * t;
			_ty = startTy + (endTy - startTy) * t;
			Clamp();
			Invalidate();
		};
		anim.AnimationEnd += (_, _) => AfterMove();
		_zoomAnim = anim;
		anim.Start();
	}

	// ------------------------------------------------------------------ touch

	public override bool OnTouchEvent(MotionEvent? e)
	{
		if (e == null || _s == null) return base.OnTouchEvent(e);
		Parent?.RequestDisallowInterceptTouchEvent(true);

		MotionEventActions action = e.ActionMasked;

		// A second finger going down while a text / signature / picture is selected, with either finger on it:
		// from here the two fingers resize and turn THAT, not the page.
		if (action == MotionEventActions.PointerDown && e.PointerCount == 2 && !_annGesture && SelectedAnnotation is { } target
			&& AnnotationTransforms.CanTransform(target) && (PointerOn(e, 0, target) || PointerOn(e, 1, target)))
			BeginAnnotationGesture(e, target);
		if (_annGesture) return HandleAnnotationGesture(e, action);

		_scaleDetector.OnTouchEvent(e);

		if (action == MotionEventActions.Down)
		{
			_fingerDown = true;
			_annGesture = false;
			_longPressActive = false;
			_multiTouch = false;
			_scroller.ForceFinished(true);
			_zoomAnim?.Cancel();
		}

		// Two or more fingers: pinch (handled by the scale detector) plus panning by their centre.
		if (e.PointerCount > 1 || _scaleDetector.IsInProgress)
		{
			if (!_multiTouch)
			{
				_multiTouch = true;
				CancelStroke();
				_selecting = false;
				_draggingAnn = false;
				CancelGestureDetector(e);
				(_lastCx, _lastCy) = Centroid(e);
			}
			if (action == MotionEventActions.Move)
			{
				(float cx, float cy) = Centroid(e);
				_tx += cx - _lastCx;
				_ty += cy - _lastCy;
				_lastCx = cx;
				_lastCy = cy;
				Clamp();
				Invalidate();
			}
			else if (action is MotionEventActions.PointerUp or MotionEventActions.PointerDown)
			{
				(_lastCx, _lastCy) = CentroidExcluding(e, action == MotionEventActions.PointerUp ? e.ActionIndex : -1);
			}
			if (action is MotionEventActions.Up or MotionEventActions.Cancel) EndTouch();
			return true;
		}
		if (_multiTouch)
		{
			// One finger left after a pinch: ignore it until everything is lifted.
			if (action is MotionEventActions.Up or MotionEventActions.Cancel) EndTouch();
			return true;
		}

		bool drawing = Tool is ViewerTool.Pen or ViewerTool.Marker;
		if (drawing) return HandleDrawing(e, action);

		if (_longPressActive && !_selecting)
		{
			// Long press recognised, selection still loading (first use of a page reads its text): swallow
			// the finger so the page does not scroll under it.
			if (action is MotionEventActions.Up or MotionEventActions.Cancel)
			{
				_longPressActive = false;
				EndTouch();
			}
			return true;
		}

		if (_selecting) return HandleSelectionDrag(e, action);

		if (HandleAnnotationDrag(e, action)) return true;

		_gestureDetector.OnTouchEvent(e);
		if (action is MotionEventActions.Up or MotionEventActions.Cancel) EndTouch();
		return true;
	}

	// --- two fingers on the selected annotation: resize + turn + move

	private bool _annGesture;
	private Annotation? _annStart;
	private float _gDist0, _gAngle0, _gAnglePrev, _gAngleAcc, _gCx0, _gCy0;

	private bool PointerOn(MotionEvent e, int index, Annotation a)
	{
		if (!ScreenToPage(e.GetX(index), e.GetY(index), out int page, out float nx, out float ny) || page != a.Page) return false;
		return a.HitTest(nx, ny, PageAspect(page), 0.03f);
	}

	private void BeginAnnotationGesture(MotionEvent e, Annotation target)
	{
		_annGesture = true;
		_multiTouch = true;
		_annStart = target;
		CancelStroke();
		_selecting = false;
		_draggingAnn = false;
		CancelGestureDetector(e);
		(_gCx0, _gCy0) = Centroid(e);
		float dx = e.GetX(1) - e.GetX(0), dy = e.GetY(1) - e.GetY(0);
		_gDist0 = Math.Max(1f, MathF.Sqrt(dx * dx + dy * dy));
		_gAngle0 = _gAnglePrev = MathF.Atan2(dy, dx);
		_gAngleAcc = 0;
		_s?.Annotations.Checkpoint(); // the whole gesture is one undo step
	}

	private bool HandleAnnotationGesture(MotionEvent e, MotionEventActions action)
	{
		if (action == MotionEventActions.Move && e.PointerCount >= 2 && _annStart is { } start && _s != null)
		{
			float dx = e.GetX(1) - e.GetX(0), dy = e.GetY(1) - e.GetY(0);
			float dist = MathF.Sqrt(dx * dx + dy * dy);
			float angle = MathF.Atan2(dy, dx);
			float step = angle - _gAnglePrev;
			// Unwrap so turning past the +-180 degree seam does not jump.
			if (step > MathF.PI) step -= 2 * MathF.PI;
			else if (step < -MathF.PI) step += 2 * MathF.PI;
			_gAngleAcc += step;
			_gAnglePrev = angle;

			float rotate = _gAngleAcc * 180f / MathF.PI;
			float baseRotation = start.Rotation;
			float total = baseRotation + rotate;
			float nearest = MathF.Round(total / 90f) * 90f;
			if (MathF.Abs(total - nearest) < 3f) rotate = nearest - baseRotation; // snap to upright / sideways

			(float cx, float cy) = Centroid(e);
			float dxPage = (cx - _gCx0) / _scale / _pw, dyPage = (cy - _gCy0) / _scale / _h[start.Page];
			_s.Annotations.Replace(AnnotationTransforms.Apply(start, dxPage, dyPage, dist / _gDist0, rotate, PageAspect(start.Page)), recordHistory: false);
		}
		else if (action is MotionEventActions.PointerUp or MotionEventActions.Up or MotionEventActions.Cancel)
		{
			// One finger lifted: the gesture is over, and the finger left must not start scrolling the page.
			_annGesture = false;
			_annStart = null;
			if (action is MotionEventActions.Up or MotionEventActions.Cancel) EndTouch();
		}
		return true;
	}

	/// <summary>The page under a screen point (clamped to the nearest page when the point is in a gap), and the
	/// position on it; false when nothing is loaded. Used to drop a signature dragged from the dock.</summary>
	public bool TryGetPageAt(float screenX, float screenY, out int page, out float nx, out float ny)
	{
		page = 0;
		nx = ny = 0;
		if (_s == null || _top.Length == 0 || _vw <= 0) return false;
		page = PageAtDocY((screenY - _ty) / _scale);
		NPoint p = ScreenToPageClamped(screenX, screenY, page);
		nx = p.X;
		ny = p.Y;
		return true;
	}

	/// <summary>The middle of the part of the document on screen, as a page and a position on it.</summary>
	public bool TryGetViewCenter(out int page, out float nx, out float ny) => TryGetPageAt(_vw / 2, _vh / 2, out page, out nx, out ny);

	private void EndTouch()
	{
		_fingerDown = false;
		_multiTouch = false;
		AfterMove();
	}

	private void CancelGestureDetector(MotionEvent e)
	{
		MotionEvent cancel = MotionEvent.Obtain(e)!;
		cancel.Action = MotionEventActions.Cancel;
		_gestureDetector.OnTouchEvent(cancel);
		cancel.Recycle();
	}

	private static (float, float) Centroid(MotionEvent e) => CentroidExcluding(e, -1);

	private static (float, float) CentroidExcluding(MotionEvent e, int skip)
	{
		float x = 0, y = 0;
		int n = 0;
		for (int i = 0; i < e.PointerCount; i++)
		{
			if (i == skip) continue;
			x += e.GetX(i);
			y += e.GetY(i);
			n++;
		}
		return n == 0 ? (e.GetX(), e.GetY()) : (x / n, y / n);
	}

	// --- pen / highlighter

	private bool HandleDrawing(MotionEvent e, MotionEventActions action)
	{
		switch (action)
		{
			case MotionEventActions.Down:
				if (ScreenToPage(e.GetX(), e.GetY(), out int page, out _, out _))
				{
					ClearTextSelection();
					DeselectAnnotation();
					_strokePage = page;
					_liveStroke = [ScreenToPageClamped(e.GetX(), e.GetY(), page)];
					Invalidate();
				}
				return true;
			case MotionEventActions.Move when _liveStroke != null:
				for (int i = 0; i < e.HistorySize; i++) AddStrokePoint(e.GetHistoricalX(i), e.GetHistoricalY(i));
				AddStrokePoint(e.GetX(), e.GetY());
				Invalidate();
				return true;
			case MotionEventActions.Up:
				CommitStroke();
				EndTouch();
				return true;
			case MotionEventActions.Cancel:
				CancelStroke();
				EndTouch();
				return true;
		}
		return true;
	}

	private void AddStrokePoint(float sx, float sy)
	{
		if (_liveStroke == null) return;
		NPoint p = ScreenToPageClamped(sx, sy, _strokePage);
		NPoint last = _liveStroke[^1];
		float dx = (p.X - last.X) * _pw * _scale, dy = (p.Y - last.Y) * _h[_strokePage] * _scale;
		if (dx * dx + dy * dy >= 2.25f) _liveStroke.Add(p); // at least 1.5 px apart
	}

	private void CommitStroke()
	{
		if (_liveStroke == null || _s == null) return;
		bool marker = Tool == ViewerTool.Marker;
		uint color = marker ? (InkColor & 0x00FFFFFFu) | 0x66000000u : InkColor;
		List<NPoint> points = _liveStroke;
		if (points.Count == 1) points.Add(points[0]);
		_s.Annotations.Add(new InkAnnotation(Guid.NewGuid(), _strokePage, color, marker ? MarkerWidth : PenWidth, marker, points));
		_liveStroke = null;
		_strokePage = -1;
		Invalidate();
	}

	private void CancelStroke()
	{
		if (_liveStroke == null) return;
		_liveStroke = null;
		_strokePage = -1;
		Invalidate();
	}

	// --- text selection drag (after a long press)

	private bool HandleSelectionDrag(MotionEvent e, MotionEventActions action)
	{
		if (action == MotionEventActions.Move && _selText != null)
		{
			NPoint p = ScreenToPageClamped(e.GetX(), e.GetY(), _selPage);
			int w = _selText.NearestWord(p.X, p.Y);
			if (w >= 0 && w != _selB)
			{
				_selB = w;
				Invalidate();
			}
		}
		else if (action is MotionEventActions.Up or MotionEventActions.Cancel)
		{
			_selecting = false;
			_longPressActive = false;
			if (_selText != null) TextSelectionChanged?.Invoke(true);
			EndTouch();
		}
		return true;
	}

	// --- moving the selected annotation

	private bool HandleAnnotationDrag(MotionEvent e, MotionEventActions action)
	{
		if (_s == null) return false;
		switch (action)
		{
			case MotionEventActions.Down when SelectedAnnotation is { } sel && ScreenToPage(e.GetX(), e.GetY(), out int page, out float nx, out float ny) && page == sel.Page:
				if (!HitAnnotation(sel, nx, ny)) return false;
				_draggingAnn = true;
				_s.Annotations.Checkpoint();
				_dragLastX = e.GetX();
				_dragLastY = e.GetY();
				return true;
			case MotionEventActions.Move when _draggingAnn && SelectedAnnotation is { } cur:
				float dx = (e.GetX() - _dragLastX) / _scale / _pw;
				float dy = (e.GetY() - _dragLastY) / _scale / _h[cur.Page];
				_dragLastX = e.GetX();
				_dragLastY = e.GetY();
				_s.Annotations.Replace(cur.Moved(dx, dy), recordHistory: false);
				return true;
			case MotionEventActions.Up or MotionEventActions.Cancel when _draggingAnn:
				_draggingAnn = false;
				EndTouch();
				return true;
		}
		return false;
	}

	private bool HitAnnotation(Annotation a, float nx, float ny)
	{
		float aspect = PageAspect(a.Page);
		// Text boxes are measured with the real font by the painter for the outline, but for grabbing,
		// a generous padding around the estimate is enough.
		return a.HitTest(nx, ny, aspect, 0.02f);
	}

	// --- taps, double tap, long press (from GestureDetector)

	private void OnSingleTap(float x, float y)
	{
		if (_selText != null)
		{
			ClearTextSelection();
			return;
		}
		if (ScreenToPage(x, y, out int page, out float nx, out float ny))
		{
			Annotation? hit = _s?.Annotations.HitTest(page, nx, ny, PageAspect(page), 0.02f);
			if (hit != null)
			{
				_selAnn = hit.Id;
				AnnotationSelectionChanged?.Invoke(hit);
				Invalidate();
				return;
			}
			if (_selAnn != null) DeselectAnnotation();
			if (Tool is ViewerTool.Text or ViewerTool.Signature)
			{
				PlaceRequested?.Invoke(Tool, page, nx, ny);
				return;
			}
		}
		else if (_selAnn != null) DeselectAnnotation();
		Tapped?.Invoke();
	}

	private void OnDoubleTap(float x, float y)
	{
		if (_scale > 1.05f) AnimateZoomTo(1f, x, y);
		else AnimateZoomTo(2.5f, x, y);
	}

	private bool _longPressActive;

	private void OnLongPress(float x, float y)
	{
		if (_s == null || Tool is ViewerTool.Pen or ViewerTool.Marker) return;
		if (!ScreenToPage(x, y, out int page, out float nx, out float ny)) return;
		PerformHapticFeedback(FeedbackConstants.LongPress);
		_longPressActive = true;
		_ = BeginSelectionAsync(page, nx, ny);
	}

	private async Task BeginSelectionAsync(int page, float nx, float ny)
	{
		PageText? text = await LoadTextAsync(page);
		if (text == null) return;
		if (!text.IsUsable)
		{
			ShowToast("Không tìm thấy chữ trên trang này.");
			return;
		}
		int word = text.HitTest(nx, ny, 0.05f);
		if (word < 0) return;
		DeselectAnnotation();
		_selText = text;
		_selPage = page;
		_selA = _selB = word;
		_selecting = _longPressActive && _fingerDown && !_multiTouch;
		if (!_selecting) TextSelectionChanged?.Invoke(true);
		Invalidate();
	}

	/// <summary>The page's text, running OCR (with a busy message) when it is a scan.</summary>
	private async Task<PageText?> LoadTextAsync(int page)
	{
		PdfSession? session = _s;
		if (session == null) return null;
		PageText? text = session.Text.TryGetCached(page);
		if (text != null && (text.IsUsable || text.IsOcr)) return text;
		try
		{
			Busy?.Invoke("Đang đọc văn bản…");
			text = await session.Text.GetAsync(page, allowOcr: false);
			if (!text.IsUsable && !text.IsOcr)
			{
				Busy?.Invoke(session.Text.HasOcr(page) ? "Đang đọc văn bản…" : "Trang scan — đang nhận dạng chữ (OCR)…");
				text = await session.Text.GetAsync(page, allowOcr: true);
			}
			return _s == session ? text : null;
		}
		catch (Exception ex)
		{
			global::Android.Util.Log.Warn("PdfReader", $"loading text failed: {ex.Message}");
			ShowToast("Không đọc được chữ trên trang này.");
			return null;
		}
		finally { Busy?.Invoke(null); }
	}

	private void ShowToast(string message) => Message?.Invoke(message);

	// ------------------------------------------------------------------ listeners

	private sealed class ScaleListener(PdfCanvasView v) : ScaleGestureDetector.SimpleOnScaleGestureListener
	{
		public override bool OnScale(ScaleGestureDetector detector)
		{
			float newScale = Math.Clamp(v._scale * detector.ScaleFactor, 1f, MaxScale);
			float f = newScale / v._scale;
			v._tx = detector.FocusX - (detector.FocusX - v._tx) * f;
			v._ty = detector.FocusY - (detector.FocusY - v._ty) * f;
			v._scale = newScale;
			v.Clamp();
			v.Invalidate();
			return true;
		}
	}

	private sealed class GestureListener(PdfCanvasView v) : GestureDetector.SimpleOnGestureListener
	{
		public override bool OnDown(MotionEvent e) => true;

		public override bool OnScroll(MotionEvent? e1, MotionEvent e2, float distanceX, float distanceY)
		{
			v._tx -= distanceX;
			v._ty -= distanceY;
			v.Clamp();
			v.NotifyPage();
			v.RequestRender();
			v.Invalidate();
			return true;
		}

		public override bool OnFling(MotionEvent? e1, MotionEvent e2, float velocityX, float velocityY)
		{
			(float minX, float maxX, float minY, float maxY) = v.Bounds();
			v._scroller.Fling((int)v._tx, (int)v._ty, (int)velocityX, (int)velocityY, (int)minX, (int)maxX, (int)minY, (int)maxY);
			v.PostInvalidateOnAnimation();
			return true;
		}

		public override bool OnSingleTapConfirmed(MotionEvent e)
		{
			v.OnSingleTap(e.GetX(), e.GetY());
			return true;
		}

		public override bool OnDoubleTap(MotionEvent e)
		{
			v.OnDoubleTap(e.GetX(), e.GetY());
			return true;
		}

		public override void OnLongPress(MotionEvent e) => v.OnLongPress(e.GetX(), e.GetY());
	}

	protected override void OnDetachedFromWindow()
	{
		base.OnDetachedFromWindow();
		Unload(); // the reader page reloads its session whenever it appears again
	}

	/// <summary>Only an explicit dispose may touch Java objects. When the garbage collector finalizes this view
	/// (<paramref name="disposing"/> false) the Java side may already be gone, and calling into it
	/// (RemoveCallbacks...) throws on the finalizer thread, which kills the whole app -- the crash seen after
	/// opening and closing documents many times.</summary>
	protected override void Dispose(bool disposing)
	{
		if (disposing && !_disposed)
		{
			_disposed = true;
			Unload();
		}
		base.Dispose(disposing);
	}
}
