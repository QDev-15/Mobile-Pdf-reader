using PdfReader.Core.Text;

namespace PdfReader.Core.Annotations;

/// <summary>A signature the user drew once and keeps to stamp on documents later: the strokes as vectors in
/// a 0..1 unit box, and <see cref="Aspect"/> = drawn width / drawn height so it can be placed without
/// being squashed.</summary>
public sealed record SavedSignature(Guid Id, float Aspect, IReadOnlyList<IReadOnlyList<NPoint>> Strokes, string? ImageFile = null)
{
	/// <summary>True for a signature that is a picture (<see cref="ImageFile"/>) rather than drawn strokes.</summary>
	public bool IsImage => ImageFile != null;

	/// <summary>A picture signature: <paramref name="file"/> is the cleaned PNG (see SignatureImageStore).</summary>
	public static SavedSignature FromImage(string file, float aspect) => new(Guid.NewGuid(), aspect, [], file);

	/// <summary>Puts this signature on a page whatever kind it is: a vector signature or a picture.</summary>
	public Annotation PlaceAny(int page, float cx, float cy, float widthFraction, float pageAspect, uint color) =>
		ImageFile != null
			? ImageAnnotation.PlaceOn(page, ImageFile, Aspect, cx, cy, widthFraction, pageAspect)
			: PlaceOn(page, cx, cy, widthFraction, pageAspect, color);

	/// <summary>Builds a signature from raw pad strokes (any pixel coordinates). Returns null when nothing
	/// was drawn.</summary>
	public static SavedSignature? FromStrokes(IReadOnlyList<IReadOnlyList<NPoint>> raw)
	{
		var strokes = raw.Where(s => s.Count > 0).ToList();
		if (strokes.Count == 0) return null;

		float minX = strokes.Min(s => s.Min(p => p.X)), maxX = strokes.Max(s => s.Max(p => p.X));
		float minY = strokes.Min(s => s.Min(p => p.Y)), maxY = strokes.Max(s => s.Max(p => p.Y));
		float w = Math.Max(maxX - minX, 1e-3f), h = Math.Max(maxY - minY, 1e-3f);

		// A signature that is a single dot or a perfectly straight line would have a zero-sized axis; keep
		// a sane aspect so it can still be placed.
		float aspect = Math.Clamp(w / h, 0.2f, 12f);
		IReadOnlyList<IReadOnlyList<NPoint>> unit = strokes
			.Select(s => (IReadOnlyList<NPoint>)s.Select(p => new NPoint((p.X - minX) / w, (p.Y - minY) / h)).ToList())
			.ToList();
		return new SavedSignature(Guid.NewGuid(), aspect, unit);
	}

	/// <summary>Places this signature on <paramref name="page"/> centred at (<paramref name="cx"/>,
	/// <paramref name="cy"/>), <paramref name="widthFraction"/> of the page wide. <paramref name="pageAspect"/>
	/// is page height / width.</summary>
	public SignatureAnnotation PlaceOn(int page, float cx, float cy, float widthFraction, float pageAspect, uint color)
	{
		float w = widthFraction;
		float h = w / Aspect / pageAspect;
		float left = Math.Clamp(cx - w / 2, 0, Math.Max(0, 1 - w));
		float top = Math.Clamp(cy - h / 2, 0, Math.Max(0, 1 - h));
		// Pen thickness: about 1.3% of the signature's width, in page-width units.
		return new SignatureAnnotation(Guid.NewGuid(), page, Strokes, new NRect(left, top, left + w, top + h), w * 0.013f, color);
	}
}
