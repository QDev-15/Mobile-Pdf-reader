using System.Text.Json.Serialization;
using PdfReader.Core.Text;

namespace PdfReader.Core.Annotations;

public enum MarkupKind { Highlight, Underline, Strikeout, Redact }

/// <summary>
/// One edit the user made on a page. Everything is stored in page-normalised coordinates (see
/// <see cref="NRect"/>) and sizes as a fraction of the page WIDTH, so an annotation looks the same at any
/// zoom, on any screen, and in the exported PDF. Immutable records: moving or recolouring makes a new
/// value with the same <see cref="Id"/>, which is what makes undo/redo a plain list of snapshots.
/// <c>pageAspect</c> below is page height / page width.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(InkAnnotation), "ink")]
[JsonDerivedType(typeof(MarkupAnnotation), "markup")]
[JsonDerivedType(typeof(TextBoxAnnotation), "text")]
[JsonDerivedType(typeof(SignatureAnnotation), "signature")]
[JsonDerivedType(typeof(ImageAnnotation), "image")]
public abstract record Annotation(Guid Id, int Page)
{
	public abstract NRect Bounds(float pageAspect);

	public abstract Annotation Moved(float dx, float dy);

	/// <summary>Degrees clockwise this annotation is turned about the centre of its <see cref="Bounds"/>.
	/// Only text, signatures and pictures can be turned; the rest stay 0.</summary>
	public virtual float Rotation => 0f;

	/// <summary>Same annotation turned to <paramref name="degrees"/> (ignored by kinds that cannot turn).</summary>
	public virtual Annotation Rotated(float degrees) => this;

	/// <summary>True when (x, y) is on this annotation, give or take <paramref name="tolerance"/> (a
	/// fraction of the page width). Ink overrides this to hit the stroke itself, not its bounding box; a
	/// turned annotation tests the point turned back the other way.</summary>
	public virtual bool HitTest(float x, float y, float pageAspect, float tolerance)
	{
		NRect bounds = Bounds(pageAspect);
		(float ux, float uy) = Rotation == 0 ? (x, y) : Unrotate(x, y, bounds.CenterX, bounds.CenterY, Rotation, pageAspect);
		return bounds.Inflate(tolerance, tolerance / pageAspect).Contains(ux, uy);
	}

	/// <summary>Where (x, y) was before the annotation was turned <paramref name="degrees"/> clockwise about
	/// (cx, cy). Turning happens in "page-width units" (y scaled by the page aspect) so it is a true rotation
	/// and not a skew.</summary>
	public static (float X, float Y) Unrotate(float x, float y, float cx, float cy, float degrees, float pageAspect)
	{
		double a = degrees * Math.PI / 180.0, cos = Math.Cos(a), sin = Math.Sin(a);
		double dx = x - cx, dy = (y - cy) * pageAspect;
		double rx = dx * cos + dy * sin, ry = -dx * sin + dy * cos;
		return ((float)(cx + rx), (float)(cy + ry / pageAspect));
	}
}

/// <summary>Moving, resizing and turning a text / signature / picture annotation in one go -- what a
/// two-finger gesture on it does. Always computed from the annotation as it was when the gesture began, so
/// the result never drifts however many small steps the gesture is made of.</summary>
public static class AnnotationTransforms
{
	/// <summary>True for the kinds a two-finger gesture can resize and turn.</summary>
	public static bool CanTransform(Annotation a) => a is TextBoxAnnotation or SignatureAnnotation or ImageAnnotation;

	/// <param name="start">The annotation when the gesture began.</param>
	/// <param name="dx">Movement of the centre, as a fraction of page width.</param>
	/// <param name="dy">Movement of the centre, as a fraction of page height.</param>
	/// <param name="scale">Size relative to the start (1 = unchanged).</param>
	/// <param name="rotateDeg">Extra clockwise degrees relative to the start.</param>
	public static Annotation Apply(Annotation start, float dx, float dy, float scale, float rotateDeg, float pageAspect)
	{
		scale = Math.Clamp(scale, 0.05f, 20f);
		switch (start)
		{
			case TextBoxAnnotation t:
			{
				NRect before = t.Bounds(pageAspect);
				float font = Math.Clamp(t.FontSize * scale, 0.008f, 0.3f);
				TextBoxAnnotation sized = t with { FontSize = font };
				NRect after = sized.Bounds(pageAspect);
				// Keep the centre where the gesture put it while the box grows or shrinks around it.
				float cx = before.CenterX + dx, cy = before.CenterY + dy;
				return sized with { X = cx - after.Width / 2, Y = cy - after.Height / 2, RotationDeg = Normalize(t.RotationDeg + rotateDeg) };
			}
			case SignatureAnnotation s:
				return ((SignatureAnnotation)s.Scaled(scale, pageAspect).Moved(dx, dy)).Rotated(s.RotationDeg + rotateDeg);
			case ImageAnnotation i:
				return i.Scaled(scale).Moved(dx, dy).Rotated(i.RotationDeg + rotateDeg);
			default:
				return start;
		}
	}

	public static float Normalize(float degrees)
	{
		float d = degrees % 360f;
		return d < 0 ? d + 360f : d;
	}
}

/// <summary>A free-hand stroke -- the pen, or the highlighter when <see cref="Highlighter"/> (translucent,
/// wide, drawn under the text colour like a marker).</summary>
public sealed record InkAnnotation(Guid Id, int Page, uint Color, float Width, bool Highlighter, IReadOnlyList<NPoint> Points)
	: Annotation(Id, Page)
{
	public override NRect Bounds(float pageAspect)
	{
		if (Points.Count == 0) return default;
		float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
		foreach (NPoint p in Points)
		{
			minX = Math.Min(minX, p.X);
			maxX = Math.Max(maxX, p.X);
			minY = Math.Min(minY, p.Y);
			maxY = Math.Max(maxY, p.Y);
		}
		return new NRect(minX, minY, maxX, maxY).Inflate(Width / 2, Width / 2 / pageAspect);
	}

	public override Annotation Moved(float dx, float dy) =>
		this with { Points = Points.Select(p => new NPoint(p.X + dx, p.Y + dy)).ToList() };

	public override bool HitTest(float x, float y, float pageAspect, float tolerance)
	{
		// Work in "page-width units" on both axes so distances are not squashed by the page aspect.
		float py = y * pageAspect;
		float reach = tolerance + Width / 2;
		if (Points.Count == 1)
			return Dist(x, py, Points[0].X, Points[0].Y * pageAspect) <= reach;
		for (int i = 1; i < Points.Count; i++)
		{
			if (SegmentDistance(x, py, Points[i - 1].X, Points[i - 1].Y * pageAspect, Points[i].X, Points[i].Y * pageAspect) <= reach)
				return true;
		}
		return false;
	}

	private static float Dist(float ax, float ay, float bx, float by) => MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

	private static float SegmentDistance(float px, float py, float ax, float ay, float bx, float by)
	{
		float dx = bx - ax, dy = by - ay;
		float len2 = dx * dx + dy * dy;
		float t = len2 <= 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
		return Dist(px, py, ax + t * dx, ay + t * dy);
	}
}

/// <summary>Highlight / underline / strikeout over a run of text, or a black redaction box. One
/// rectangle per line of text.</summary>
public sealed record MarkupAnnotation(Guid Id, int Page, MarkupKind Kind, uint Color, IReadOnlyList<NRect> Rects)
	: Annotation(Id, Page)
{
	public override NRect Bounds(float pageAspect) => NRect.UnionAll(Rects);

	public override Annotation Moved(float dx, float dy) =>
		this with { Rects = Rects.Select(r => r.Offset(dx, dy)).ToList() };

	public override bool HitTest(float x, float y, float pageAspect, float tolerance) =>
		Rects.Any(r => r.Inflate(tolerance, tolerance / pageAspect).Contains(x, y));
}

/// <summary>A block of typed text. <see cref="X"/>/<see cref="Y"/> is the top-left of the (unrotated)
/// text, <see cref="FontSize"/> a fraction of the page width. <see cref="RotationDeg"/> turns it about its
/// own centre -- used by the diagonal watermark.</summary>
public sealed record TextBoxAnnotation(Guid Id, int Page, string Text, float X, float Y, float FontSize, uint Color, float RotationDeg = 0, bool Bold = false, string FontFamily = "sans", bool Italic = false)
	: Annotation(Id, Page)
{
	public const float LineHeightFactor = 1.25f;

	/// <summary>The families a text box can use; names a person never sees, mapped to a typeface by the painter.</summary>
	public static readonly IReadOnlyList<string> FontFamilies = ["sans", "serif", "mono", "cursive"];

	public override float Rotation => RotationDeg;

	public override Annotation Rotated(float degrees) => this with { RotationDeg = AnnotationTransforms.Normalize(degrees) };

	/// <summary>A rough size from character counts -- good enough for tapping and dragging; the exact
	/// size is measured by the platform text engine at draw time.</summary>
	public override NRect Bounds(float pageAspect)
	{
		string[] lines = Text.Split('\n');
		int longest = lines.Max(l => l.Length);
		float w = Math.Max(1, longest) * FontSize * 0.55f;
		float h = lines.Length * FontSize * LineHeightFactor / pageAspect;
		return new NRect(X, Y, X + w, Y + h);
	}

	public override Annotation Moved(float dx, float dy) => this with { X = X + dx, Y = Y + dy };
}

/// <summary>A saved signature placed on a page: the strokes are kept as vectors in a 0..1 unit box
/// (<see cref="Strokes"/>) and scaled into <see cref="Box"/> when drawn, so it stays crisp at any size and
/// in the exported PDF. <see cref="PenWidth"/> is a fraction of the page width.</summary>
public sealed record SignatureAnnotation(Guid Id, int Page, IReadOnlyList<IReadOnlyList<NPoint>> Strokes, NRect Box, float PenWidth, uint Color, float RotationDeg = 0)
	: Annotation(Id, Page)
{
	public override NRect Bounds(float pageAspect) => Box;

	public override float Rotation => RotationDeg;

	public override Annotation Rotated(float degrees) => this with { RotationDeg = AnnotationTransforms.Normalize(degrees) };

	public override Annotation Moved(float dx, float dy) => this with { Box = Box.Offset(dx, dy) };

	/// <summary>Same signature scaled about its centre (stroke thickness scales with it).</summary>
	public SignatureAnnotation Scaled(float factor, float pageAspect)
	{
		float w = Box.Width * factor, h = Box.Height * factor;
		var box = new NRect(Box.CenterX - w / 2, Box.CenterY - h / 2, Box.CenterX + w / 2, Box.CenterY + h / 2);
		return this with { Box = box, PenWidth = PenWidth * factor };
	}
}

/// <summary>A picture placed on a page -- the user's own signature image. <see cref="File"/> is a PNG in
/// the app's private storage (background already made transparent), so the annotation survives the original
/// picture being deleted from the gallery.</summary>
public sealed record ImageAnnotation(Guid Id, int Page, string File, NRect Box, float RotationDeg = 0)
	: Annotation(Id, Page)
{
	public override NRect Bounds(float pageAspect) => Box;

	public override float Rotation => RotationDeg;

	public override Annotation Rotated(float degrees) => this with { RotationDeg = AnnotationTransforms.Normalize(degrees) };

	public override Annotation Moved(float dx, float dy) => this with { Box = Box.Offset(dx, dy) };

	/// <summary>Same picture scaled about its centre, keeping its proportions.</summary>
	public ImageAnnotation Scaled(float factor)
	{
		float w = Box.Width * factor, h = Box.Height * factor;
		return this with { Box = new NRect(Box.CenterX - w / 2, Box.CenterY - h / 2, Box.CenterX + w / 2, Box.CenterY + h / 2) };
	}

	/// <summary>Places a picture of <paramref name="imageAspect"/> (width / height) centred at (cx, cy),
	/// <paramref name="widthFraction"/> of the page wide, kept inside the page.</summary>
	public static ImageAnnotation PlaceOn(int page, string file, float imageAspect, float cx, float cy, float widthFraction, float pageAspect)
	{
		float w = widthFraction, h = w / Math.Max(0.05f, imageAspect) / pageAspect;
		float left = Math.Clamp(cx - w / 2, 0, Math.Max(0, 1 - w));
		float top = Math.Clamp(cy - h / 2, 0, Math.Max(0, 1 - h));
		return new ImageAnnotation(Guid.NewGuid(), page, file, new NRect(left, top, left + w, top + h));
	}
}
