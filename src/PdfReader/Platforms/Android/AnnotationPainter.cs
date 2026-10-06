using Android.Graphics;
using PdfReader.Core.Annotations;
using PdfReader.Core.Text;
using Paint = Android.Graphics.Paint;
using RectF = Android.Graphics.RectF;
using Path = Android.Graphics.Path;

namespace PdfReader.Services;

/// <summary>
/// Draws annotations onto an Android Canvas inside a page rectangle. The one routine behind both what is
/// shown over the page while reading and what is baked into a page when exporting, so the two always
/// agree. The page rectangle is wherever the page sits in the canvas (on screen: the zoomed page; when
/// exporting: the whole bitmap); all sizes are fractions of its width.
/// </summary>
public static class AnnotationPainter
{
	public static void Draw(Canvas canvas, IEnumerable<Annotation> annotations, RectF page)
	{
		foreach (Annotation a in annotations) Draw(canvas, a, page);
	}

	public static void Draw(Canvas canvas, Annotation a, RectF page)
	{
		switch (a)
		{
			case InkAnnotation ink: DrawInk(canvas, ink, page); break;
			case MarkupAnnotation m: DrawMarkup(canvas, m, page); break;
			case TextBoxAnnotation t: DrawText(canvas, t, page); break;
			case SignatureAnnotation s: DrawSignature(canvas, s, page); break;
			case ImageAnnotation img: DrawImage(canvas, img, page); break;
		}
	}

	public static RectF ToRect(NRect r, RectF page) =>
		new(page.Left + r.Left * page.Width(), page.Top + r.Top * page.Height(), page.Left + r.Right * page.Width(), page.Top + r.Bottom * page.Height());

	private static Android.Graphics.Color ColorOf(uint argb) => new(unchecked((int)argb));

	private static void DrawInk(Canvas canvas, InkAnnotation ink, RectF page) =>
		DrawStroke(canvas, ink.Points.Select(p => (page.Left + p.X * page.Width(), page.Top + p.Y * page.Height())).ToList(),
			ink.Width * page.Width(), ColorOf(ink.Color), ink.Highlighter);

	private static void DrawStroke(Canvas canvas, IReadOnlyList<(float X, float Y)> pts, float width, Android.Graphics.Color color, bool highlighter)
	{
		if (pts.Count == 0) return;
		using var paint = new Paint(PaintFlags.AntiAlias) { Color = color };
		paint.StrokeCap = highlighter ? Paint.Cap.Square : Paint.Cap.Round;
		paint.StrokeJoin = Paint.Join.Round;
		paint.StrokeWidth = Math.Max(1f, width);
		if (pts.Count == 1)
		{
			paint.SetStyle(Paint.Style.Fill);
			canvas.DrawCircle(pts[0].X, pts[0].Y, Math.Max(1f, width) / 2, paint);
			return;
		}
		paint.SetStyle(Paint.Style.Stroke);
		using var path = new Path();
		path.MoveTo(pts[0].X, pts[0].Y);
		for (int i = 1; i < pts.Count; i++)
		{
			float mx = (pts[i - 1].X + pts[i].X) / 2, my = (pts[i - 1].Y + pts[i].Y) / 2;
			path.QuadTo(pts[i - 1].X, pts[i - 1].Y, mx, my);
		}
		path.LineTo(pts[^1].X, pts[^1].Y);
		canvas.DrawPath(path, paint);
	}

	/// <summary>A stroke being drawn right now (not yet an annotation), for live feedback.</summary>
	public static void DrawLiveStroke(Canvas canvas, IReadOnlyList<NPoint> points, float widthFraction, uint color, bool highlighter, RectF page) =>
		DrawStroke(canvas, points.Select(p => (page.Left + p.X * page.Width(), page.Top + p.Y * page.Height())).ToList(),
			widthFraction * page.Width(), ColorOf(color), highlighter);

	private static void DrawMarkup(Canvas canvas, MarkupAnnotation m, RectF page)
	{
		using var paint = new Paint(PaintFlags.AntiAlias) { Color = ColorOf(m.Color) };
		foreach (NRect nr in m.Rects)
		{
			RectF r = ToRect(nr, page);
			switch (m.Kind)
			{
				case MarkupKind.Highlight:
				case MarkupKind.Redact:
					paint.SetStyle(Paint.Style.Fill);
					canvas.DrawRect(r, paint);
					break;
				case MarkupKind.Underline:
					paint.SetStyle(Paint.Style.Fill);
					canvas.DrawRect(r.Left, r.Bottom - Thickness(r), r.Right, r.Bottom, paint);
					break;
				case MarkupKind.Strikeout:
					paint.SetStyle(Paint.Style.Fill);
					float mid = (r.Top + r.Bottom) / 2, t = Thickness(r);
					canvas.DrawRect(r.Left, mid - t / 2, r.Right, mid + t / 2, paint);
					break;
			}
		}
	}

	private static float Thickness(RectF r) => Math.Max(1.5f, r.Height() * 0.08f);

	private static void DrawText(Canvas canvas, TextBoxAnnotation t, RectF page)
	{
		using Paint paint = TextPaint(t, page, out float lineHeight);
		string[] lines = t.Text.Split('\n');
		float left = page.Left + t.X * page.Width(), top = page.Top + t.Y * page.Height();
		Paint.FontMetrics fm = paint.GetFontMetrics()!;

		int save = canvas.Save();
		if (Math.Abs(t.RotationDeg) > 0.01f)
		{
			RectF box = MeasureText(t, page);
			canvas.Rotate(t.RotationDeg, box.CenterX(), box.CenterY());
		}
		for (int i = 0; i < lines.Length; i++)
			canvas.DrawText(lines[i], left, top + i * lineHeight - fm.Ascent, paint);
		canvas.RestoreToCount(save);
	}

	private static Paint TextPaint(TextBoxAnnotation t, RectF page, out float lineHeight)
	{
		var paint = new Paint(PaintFlags.AntiAlias | PaintFlags.SubpixelText) { Color = ColorOf(t.Color) };
		paint.TextSize = Math.Max(4f, t.FontSize * page.Width());
		paint.SetTypeface(Face(t.FontFamily, t.Bold, t.Italic));
		lineHeight = paint.TextSize * TextBoxAnnotation.LineHeightFactor;
		return paint;
	}

	/// <summary>Typeface for a text box's font family ("sans", "serif", "mono", "cursive") and style.</summary>
	public static Typeface Face(string family, bool bold, bool italic)
	{
		TypefaceStyle style = bold && italic ? TypefaceStyle.BoldItalic : bold ? TypefaceStyle.Bold : italic ? TypefaceStyle.Italic : TypefaceStyle.Normal;
		string name = family switch { "serif" => "serif", "mono" => "monospace", "cursive" => "cursive", _ => "sans-serif" };
		return Typeface.Create(name, style) ?? Typeface.Default!;
	}

	/// <summary>The exact on-screen rectangle of a text box (the Core estimate is only for hit-testing).</summary>
	public static RectF MeasureText(TextBoxAnnotation t, RectF page)
	{
		using Paint paint = TextPaint(t, page, out float lineHeight);
		string[] lines = t.Text.Split('\n');
		float width = lines.Max(l => paint.MeasureText(l));
		float left = page.Left + t.X * page.Width(), top = page.Top + t.Y * page.Height();
		return new RectF(left, top, left + width, top + lines.Length * lineHeight);
	}

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Bitmap?> ImageCache = new();

	private static void DrawImage(Canvas canvas, ImageAnnotation img, RectF page)
	{
		Bitmap? bitmap = ImageCache.GetOrAdd(img.File, f => File.Exists(f) ? BitmapFactory.DecodeFile(f) : null);
		if (bitmap == null || bitmap.IsRecycled) return;
		using var paint = new Paint(PaintFlags.FilterBitmap | PaintFlags.AntiAlias);
		RectF box = ToRect(img.Box, page);
		int save = canvas.Save();
		if (Math.Abs(img.RotationDeg) > 0.01f) canvas.Rotate(img.RotationDeg, box.CenterX(), box.CenterY());
		canvas.DrawBitmap(bitmap, null, box, paint);
		canvas.RestoreToCount(save);
	}

	private static void DrawSignature(Canvas canvas, SignatureAnnotation s, RectF page)
	{
		RectF box = ToRect(s.Box, page);
		int save = canvas.Save();
		if (Math.Abs(s.RotationDeg) > 0.01f) canvas.Rotate(s.RotationDeg, box.CenterX(), box.CenterY());
		foreach (IReadOnlyList<NPoint> stroke in s.Strokes)
			DrawStroke(canvas, stroke.Select(p => (box.Left + p.X * box.Width(), box.Top + p.Y * box.Height())).ToList(),
				s.PenWidth * page.Width(), ColorOf(s.Color), highlighter: false);
		canvas.RestoreToCount(save);
	}
}
