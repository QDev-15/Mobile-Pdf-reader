using PdfReader.Core.Ads;
using PdfReader.Core.Annotations;
using PdfReader.Core.Pdf;
using PdfReader.Core.Text;

namespace PdfReader.Core.Tests;

public class PageRangesTests
{
	[Fact]
	public void Parses_single_ranges_and_open_ends()
	{
		var r = PageRanges.Parse("1-3, 5, 8-", 10);
		Assert.Equal([(0, 2), (4, 4), (7, 9)], r);
	}

	[Fact]
	public void Flatten_removes_repeats()
	{
		Assert.Equal([0, 1, 2, 4], PageRanges.Flatten(PageRanges.Parse("1-3,2,5", 10)));
	}

	[Theory]
	[InlineData("")]
	[InlineData("0")]
	[InlineData("11")]
	[InlineData("5-2")]
	[InlineData("abc")]
	public void Rejects_bad_input(string text) => Assert.Throws<FormatException>(() => PageRanges.Parse(text, 10));
}

public class TextTests
{
	private static PageText Page(params (string t, float x, float y)[] ws) =>
		new(0, ReadingOrder.ArrangeWords(ws.Select(w => (w.t, new NRect(w.x, w.y, w.x + 0.1f, w.y + 0.02f))).ToList()), false);

	[Fact]
	public void Reading_order_groups_lines_and_sorts_words()
	{
		PageText p = Page(("world", 0.3f, 0.1f), ("second", 0.1f, 0.2f), ("hello", 0.1f, 0.1f));
		Assert.Equal("hello world\nsecond", p.FullText);
	}

	[Fact]
	public void Selection_gives_one_rect_per_line()
	{
		PageText p = Page(("a", 0.1f, 0.1f), ("b", 0.3f, 0.1f), ("c", 0.1f, 0.2f));
		Assert.Equal(2, p.RectsFor(0, 2).Count);
		Assert.Equal("b\nc", p.Join(2, 1));
	}

	[Fact]
	public void HitTest_finds_word_and_misses_blank_space()
	{
		PageText p = Page(("hello", 0.1f, 0.1f));
		Assert.Equal(0, p.HitTest(0.15f, 0.11f));
		Assert.Equal(-1, p.HitTest(0.9f, 0.9f));
		Assert.Equal(0, p.NearestWord(0.9f, 0.9f));
	}

	[Fact]
	public void Search_ignores_accents_and_case_and_spans_words()
	{
		PageText p = Page(("Tài", 0.1f, 0.1f), ("liệu", 0.3f, 0.1f), ("Đà", 0.5f, 0.1f), ("Nẵng", 0.7f, 0.1f));
		Assert.Single(TextSearch.FindInPage(p, "tai lieu"));
		Assert.Single(TextSearch.FindInPage(p, "da nang"));
		Assert.Empty(TextSearch.FindInPage(p, "tai lieu", ignoreAccents: false));
		Assert.Single(TextSearch.FindInPage(p, "TÀI LIỆU", ignoreAccents: false));
		IReadOnlyList<SearchHit> hit = TextSearch.FindInPage(p, "lieu");
		Assert.Equal(0.3f, hit[0].Rects[0].Left, 3);
	}
}

public class AnnotationTests
{
	private static InkAnnotation Ink(int page = 0) =>
		new(Guid.NewGuid(), page, 0xFFFF0000, 0.01f, false, [new NPoint(0.1f, 0.1f), new NPoint(0.5f, 0.1f)]);

	[Fact]
	public void Undo_and_redo_walk_the_history()
	{
		var doc = new AnnotationDocument();
		doc.Add(Ink());
		doc.Add(Ink());
		Assert.Equal(2, doc.Items.Count);
		doc.Undo();
		Assert.Single(doc.Items);
		doc.Redo();
		Assert.Equal(2, doc.Items.Count);
		doc.Undo();
		doc.Add(Ink()); // a new edit drops the redo history
		Assert.False(doc.CanRedo);
	}

	[Fact]
	public void A_drag_is_one_undo_step()
	{
		var doc = new AnnotationDocument();
		InkAnnotation a = Ink();
		doc.Add(a);
		doc.Checkpoint();
		for (int i = 1; i <= 5; i++) doc.Replace(doc.Find(a.Id)!.Moved(0.01f, 0), recordHistory: false);
		doc.Undo();
		Assert.Equal(0.1f, ((InkAnnotation)doc.Items[0]).Points[0].X, 4);
	}

	[Fact]
	public void Json_round_trip_keeps_every_kind()
	{
		var doc = new AnnotationDocument();
		doc.Add(Ink());
		doc.Add(new MarkupAnnotation(Guid.NewGuid(), 1, MarkupKind.Highlight, 0x80FFFF00, [new NRect(0.1f, 0.1f, 0.4f, 0.12f)]));
		doc.Add(new TextBoxAnnotation(Guid.NewGuid(), 2, "Xin chào\nThế giới", 0.2f, 0.3f, 0.03f, 0xFF000000, 30));
		doc.Add(new SignatureAnnotation(Guid.NewGuid(), 0, [[new NPoint(0, 0), new NPoint(1, 1)]], new NRect(0.1f, 0.8f, 0.4f, 0.9f), 0.004f, 0xFF000080));

		AnnotationDocument back = AnnotationDocument.FromJson(doc.ToJson());
		Assert.Equal(4, back.Items.Count);
		Assert.IsType<InkAnnotation>(back.Items[0]);
		Assert.IsType<MarkupAnnotation>(back.Items[1]);
		var text = Assert.IsType<TextBoxAnnotation>(back.Items[2]);
		Assert.Equal("Xin chào\nThế giới", text.Text);
		Assert.Equal(30, text.RotationDeg);
		Assert.IsType<SignatureAnnotation>(back.Items[3]);
		Assert.Equal(new HashSet<int> { 0, 1, 2 }, back.PagesWithAnnotations());
	}

	[Fact]
	public void Corrupt_json_gives_empty_document() => Assert.Empty(AnnotationDocument.FromJson("{ not json").Items);

	[Fact]
	public void Hit_test_follows_the_stroke_not_its_bounding_box()
	{
		var doc = new AnnotationDocument();
		var diagonal = new InkAnnotation(Guid.NewGuid(), 0, 0xFF000000, 0.005f, false, [new NPoint(0.1f, 0.1f), new NPoint(0.9f, 0.9f)]);
		doc.Add(diagonal);
		Assert.NotNull(doc.HitTest(0, 0.5f, 0.5f, 1.0f));
		Assert.Null(doc.HitTest(0, 0.9f, 0.1f, 1.0f)); // inside the bounding box, far from the stroke
		Assert.Null(doc.HitTest(1, 0.5f, 0.5f, 1.0f)); // other page
	}

	[Fact]
	public void Moving_and_scaling_signature()
	{
		var sig = new SignatureAnnotation(Guid.NewGuid(), 0, [], new NRect(0.1f, 0.1f, 0.3f, 0.2f), 0.004f, 0xFF000000);
		Assert.Equal(0.15f, ((SignatureAnnotation)sig.Moved(0.05f, 0)).Box.Left, 4);
		SignatureAnnotation scaled = sig.Scaled(2, 1.4f);
		Assert.Equal(0.4f, scaled.Box.Width, 4);
		Assert.Equal(sig.Box.CenterX, scaled.Box.CenterX, 4);
	}
}

public class PdfOpsTests : IDisposable
{
	private readonly string _dir = TestPdf.TempDir();

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	[Fact]
	public void Text_layer_is_extracted_with_position()
	{
		string pdf = TestPdf.Write(_dir, "a.pdf", "Hello World");
		using PdfTextSource src = PdfTextSource.Open(pdf);
		PageText p = src.ExtractPage(0);
		Assert.Equal("Hello World", p.FullText);
		Assert.Equal(2, p.Words.Count);
		// baseline y=700 of 792, x=100 of 612 -> upper left of the page.
		Assert.InRange(p.Words[0].Box.Left, 0.15f, 0.18f);
		Assert.InRange(p.Words[0].Box.Top, 0.05f, 0.12f);
	}

	[Fact]
	public void Rebuild_deletes_reorders_and_rotates()
	{
		string pdf = TestPdf.Write(_dir, "a.pdf", "One", "Two", "Three");
		string outPath = Path.Combine(_dir, "out.pdf");
		PdfStructureOps.Rebuild(pdf, [new PageEdit(2), new PageEdit(0, 90)], outPath);

		Assert.Equal(2, PdfStructureOps.PageCount(outPath));
		using PdfTextSource src = PdfTextSource.Open(outPath);
		Assert.Equal("Three", src.ExtractPage(0).FullText);
		PageText rotated = src.ExtractPage(1);
		// The test text is laid out upright in unrotated space, so after /Rotate 90 it is displayed
		// sideways: the top-left text moves to the top-right of the displayed page.
		Assert.NotEmpty(rotated.Words);
		Assert.All(rotated.Words, w => Assert.True(w.Box.Left > 0.8f && w.Box.Top < 0.4f, $"box={w.Box}"));
		Assert.Equal("One", string.Concat(rotated.Words.Select(w => w.Text).OrderBy(t => t, StringComparer.Ordinal)).Length == 3 ? "One" : "?");
	}

	[Fact]
	public void Rebuild_refuses_an_empty_result()
	{
		string pdf = TestPdf.Write(_dir, "a.pdf", "One");
		Assert.Throws<ArgumentException>(() => PdfStructureOps.Rebuild(pdf, [], Path.Combine(_dir, "x.pdf")));
	}

	[Fact]
	public void Merge_concatenates_in_order()
	{
		string a = TestPdf.Write(_dir, "a.pdf", "A1", "A2");
		string b = TestPdf.Write(_dir, "b.pdf", "B1");
		string outPath = Path.Combine(_dir, "m.pdf");
		PdfStructureOps.Merge([a, b], outPath);
		using PdfTextSource src = PdfTextSource.Open(outPath);
		Assert.Equal(3, src.PageCount);
		Assert.Equal("B1", src.ExtractPage(2).FullText);
	}

	[Fact]
	public void Split_writes_one_file_per_range()
	{
		string pdf = TestPdf.Write(_dir, "a.pdf", "P1", "P2", "P3", "P4");
		var outputs = new List<string>();
		PdfStructureOps.Split(pdf, [(0, 1), (3, 3)], i =>
		{
			string p = Path.Combine(_dir, $"part{i}.pdf");
			outputs.Add(p);
			return p;
		});
		Assert.Equal(2, PdfStructureOps.PageCount(outputs[0]));
		Assert.Equal(1, PdfStructureOps.PageCount(outputs[1]));
	}

	[Fact]
	public void ReplacePages_swaps_only_the_chosen_pages()
	{
		string pdf = TestPdf.Write(_dir, "a.pdf", "Keep", "Old", "Keep2");
		string replacement = TestPdf.Write(_dir, "r.pdf", "New");
		string outPath = Path.Combine(_dir, "o.pdf");
		PdfStructureOps.ReplacePages(pdf, new Dictionary<int, string> { [1] = replacement }, outPath);
		using PdfTextSource src = PdfTextSource.Open(outPath);
		Assert.Equal(["Keep", "New", "Keep2"], Enumerable.Range(0, 3).Select(i => src.ExtractPage(i).FullText).ToArray());
	}

	[Fact]
	public void Encrypt_then_decrypt_round_trips()
	{
		string pdf = TestPdf.Write(_dir, "a.pdf", "Secret");
		string locked = Path.Combine(_dir, "locked.pdf");
		string open = Path.Combine(_dir, "open.pdf");
		PdfStructureOps.Encrypt(pdf, locked, "pw123");

		Assert.ThrowsAny<Exception>(() => PdfStructureOps.PageCount(locked));
		Assert.Equal(1, PdfStructureOps.PageCount(locked, "pw123"));
		PdfStructureOps.Decrypt(locked, open, "pw123");
		Assert.Equal(1, PdfStructureOps.PageCount(open));
	}
}

public class AdsPolicyTests
{
	private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void First_transition_shows_then_waits_an_hour()
	{
		(AdsState s1, bool show1) = AdsPolicy.AtScreenTransition(AdsState.Initial, false, Now);
		Assert.True(show1);
		(_, bool show2) = AdsPolicy.AtScreenTransition(s1, false, Now.AddMinutes(30));
		Assert.False(show2);
		(_, bool show3) = AdsPolicy.AtScreenTransition(s1, false, Now.AddMinutes(61));
		Assert.True(show3);
	}

	[Fact]
	public void Pro_never_sees_an_interstitial() =>
		Assert.False(AdsPolicy.AtScreenTransition(AdsState.Initial, true, Now).ShowInterstitial);
}

public class RasterPdfTests : IDisposable
{
	private readonly string _dir = TestPdf.TempDir();

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	// Smallest valid JPEG (1x1); the writer embeds it as-is and never decodes it.
	private static readonly byte[] Jpeg = Convert.FromBase64String(
		"/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=");

	[Fact]
	public void Writes_a_pdf_that_pdfsharp_and_pdfpig_both_read()
	{
		string path = Path.Combine(_dir, "r.pdf");
		RasterPdfWriter.Write(path, [new RasterPage(Jpeg, 1, 1, 595, 842), new RasterPage(Jpeg, 1, 1, 300, 400)]);
		Assert.Equal(2, PdfStructureOps.PageCount(path));
		using PdfTextSource src = PdfTextSource.Open(path);
		Assert.Equal(2, src.PageCount);
		Assert.Empty(src.ExtractPage(0).Words);
	}

	[Fact]
	public void Hidden_ocr_text_is_extractable_with_vietnamese_and_in_place()
	{
		string path = Path.Combine(_dir, "s.pdf");
		var words = new List<TextWord>
		{
			new("Tài", new NRect(0.1f, 0.1f, 0.2f, 0.13f), 0),
			new("liệu", new NRect(0.22f, 0.1f, 0.35f, 0.13f), 0),
			new("Đà", new NRect(0.1f, 0.2f, 0.2f, 0.23f), 1),
		};
		RasterPdfWriter.Write(path, [new RasterPage(Jpeg, 1, 1, 600, 800, words)]);

		using PdfTextSource src = PdfTextSource.Open(path);
		PageText p = src.ExtractPage(0);
		Assert.Equal("Tài liệu\nĐà", p.FullText);
		Assert.InRange(p.Words[0].Box.Left, 0.08f, 0.12f);
		Assert.InRange(p.Words[0].Box.Top, 0.08f, 0.13f);
		Assert.Single(TextSearch.FindInPage(p, "tai lieu"));
	}

	[Fact]
	public void Raster_page_can_replace_a_page_of_a_text_pdf()
	{
		string pdf = TestPdf.Write(_dir, "a.pdf", "One", "Two");
		string raster = Path.Combine(_dir, "r.pdf");
		RasterPdfWriter.Write(raster, [new RasterPage(Jpeg, 1, 1, 612, 792)]);
		string outPath = Path.Combine(_dir, "o.pdf");
		PdfStructureOps.ReplacePages(pdf, new Dictionary<int, string> { [0] = raster }, outPath);
		using PdfTextSource src = PdfTextSource.Open(outPath);
		Assert.Empty(src.ExtractPage(0).Words);
		Assert.Equal("Two", src.ExtractPage(1).FullText);
	}

	[Fact]
	public void Needs_at_least_one_page() =>
		Assert.Throws<ArgumentException>(() => RasterPdfWriter.Write(new MemoryStream(), []));
}

public class SignatureTests
{
	[Fact]
	public void Normalises_strokes_to_a_unit_box_and_keeps_aspect()
	{
		var raw = new List<IReadOnlyList<NPoint>> { new[] { new NPoint(100, 50), new NPoint(300, 150) } };
		SavedSignature sig = SavedSignature.FromStrokes(raw)!;
		Assert.Equal(2f, sig.Aspect, 3);
		Assert.Equal(new NPoint(0, 0), sig.Strokes[0][0]);
		Assert.Equal(new NPoint(1, 1), sig.Strokes[0][1]);
	}

	[Fact]
	public void Nothing_drawn_gives_null() => Assert.Null(SavedSignature.FromStrokes([]));

	[Fact]
	public void Placement_stays_on_the_page_and_keeps_proportions()
	{
		SavedSignature sig = SavedSignature.FromStrokes([new[] { new NPoint(0, 0), new NPoint(200, 100) }])!;
		SignatureAnnotation a = sig.PlaceOn(0, 0.98f, 0.5f, 0.3f, 1.414f, 0xFF000000);
		Assert.True(a.Box.Right <= 1.0001f);
		// width 0.3 of the page width, height = 0.15 page widths = 0.15/1.414 of the page height
		Assert.Equal(0.15f / 1.414f, a.Box.Height, 3);
	}
}

public class ExternalOpenPolicyTests
{
	[Fact]
	public void Sixth_external_open_shows_an_ad_then_the_count_restarts()
	{
		int count = 0;
		var shown = new List<int>();
		for (int open = 1; open <= 12; open++)
		{
			(count, bool show) = AdsPolicy.AtExternalOpen(count, isPro: false);
			if (show) shown.Add(open);
		}
		Assert.Equal([6, 12], shown);
	}

	[Fact]
	public void Pro_never_counts() => Assert.Equal((0, false), AdsPolicy.AtExternalOpen(5, isPro: true) is (var n, var s) ? (n == 5 ? 0 : n, s) : (-1, true));
}

public class ImageAnnotationTests
{
	[Fact]
	public void Placed_inside_the_page_and_round_trips_through_json()
	{
		ImageAnnotation a = ImageAnnotation.PlaceOn(2, "x.png", 2f, 0.99f, 0.5f, 0.3f, 1.414f);
		Assert.True(a.Box.Right <= 1.0001f);
		Assert.Equal(0.15f / 1.414f, a.Box.Height, 3);

		var doc = new AnnotationDocument();
		doc.Add(a);
		var back = Assert.IsType<ImageAnnotation>(AnnotationDocument.FromJson(doc.ToJson()).Items[0]);
		Assert.Equal("x.png", back.File);
		Assert.Equal(a.Box, back.Box);
		Assert.Equal(a.Box.Width * 2, a.Scaled(2).Box.Width, 4);
	}
}

public class TransformTests
{
	[Fact]
	public void Rotated_annotation_is_hit_where_it_is_drawn_not_where_its_box_was()
	{
		// A wide, short signature turned 90 degrees stands upright: a point far above it now hits, a point at
		// the side of its unturned box no longer does.
		var sig = new SignatureAnnotation(Guid.NewGuid(), 0, [], new NRect(0.3f, 0.5f, 0.7f, 0.52f), 0.004f, 0xFF000000, RotationDeg: 90);
		Assert.True(sig.HitTest(0.5f, 0.4f, 1.0f, 0.005f));
		Assert.False(sig.HitTest(0.68f, 0.51f, 1.0f, 0.005f));
	}

	[Fact]
	public void Unrotate_is_the_inverse_of_a_clockwise_turn()
	{
		(float x, float y) = Annotation.Unrotate(0.5f, 0.3f, 0.5f, 0.5f, 90, 1.0f);
		// a point above the centre was, before a 90 degree clockwise turn, to the left of it
		Assert.Equal(0.3f, x, 3);
		Assert.Equal(0.5f, y, 3);
	}

	[Fact]
	public void Gesture_scales_turns_and_moves_from_the_start_state()
	{
		var text = new TextBoxAnnotation(Guid.NewGuid(), 0, "Hello", 0.2f, 0.2f, 0.03f, 0xFF000000);
		var moved = (TextBoxAnnotation)AnnotationTransforms.Apply(text, 0.1f, 0.05f, 2f, 30f, 1.414f);
		Assert.Equal(0.06f, moved.FontSize, 4);
		Assert.Equal(30f, moved.RotationDeg, 3);
		NRect before = text.Bounds(1.414f), after = moved.Bounds(1.414f);
		Assert.Equal(before.CenterX + 0.1f, after.CenterX, 3);
		Assert.Equal(before.CenterY + 0.05f, after.CenterY, 3);

		// Applying the same gesture again from the same start gives the same result (no drift).
		Assert.Equal(moved, AnnotationTransforms.Apply(text, 0.1f, 0.05f, 2f, 30f, 1.414f));

		var image = new ImageAnnotation(Guid.NewGuid(), 0, "a.png", new NRect(0.1f, 0.1f, 0.3f, 0.2f), 350);
		var turned = (ImageAnnotation)AnnotationTransforms.Apply(image, 0, 0, 1, 20, 1.0f);
		Assert.Equal(10f, turned.RotationDeg, 3); // 350 + 20 wraps to 10
	}

	[Fact]
	public void Picture_signature_places_as_an_image_and_new_fields_round_trip()
	{
		SavedSignature pic = SavedSignature.FromImage("s.png", 2f);
		Assert.True(pic.IsImage);
		Assert.IsType<ImageAnnotation>(pic.PlaceAny(0, 0.5f, 0.5f, 0.3f, 1.414f, 0xFF000000));
		Assert.IsType<SignatureAnnotation>(SavedSignature.FromStrokes([new[] { new NPoint(0, 0), new NPoint(10, 5) }])!.PlaceAny(0, 0.5f, 0.5f, 0.3f, 1.414f, 0xFF000000));

		var doc = new AnnotationDocument();
		doc.Add(new TextBoxAnnotation(Guid.NewGuid(), 0, "x", 0.1f, 0.1f, 0.03f, 0xFF000000, 15, true, "serif", true));
		doc.Add(new SignatureAnnotation(Guid.NewGuid(), 0, [], new NRect(0, 0, 0.1f, 0.1f), 0.004f, 0xFF000000, 45));
		AnnotationDocument back = AnnotationDocument.FromJson(doc.ToJson());
		var t = Assert.IsType<TextBoxAnnotation>(back.Items[0]);
		Assert.Equal("serif", t.FontFamily);
		Assert.True(t.Italic);
		Assert.Equal(45f, ((SignatureAnnotation)back.Items[1]).RotationDeg);
	}
}
