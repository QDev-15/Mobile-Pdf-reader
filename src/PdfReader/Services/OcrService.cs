using Android.Gms.Extensions;
using Android.Graphics;
using PdfReader.Core.Pdf;
using PdfReader.Core.Text;
using Xamarin.Google.MLKit.Vision.Common;
using Xamarin.Google.MLKit.Vision.Text;
using Xamarin.Google.MLKit.Vision.Text.Latin;
using Rect = Android.Graphics.Rect;
using MlText = Xamarin.Google.MLKit.Vision.Text.Text;

namespace PdfReader.Services;

/// <summary>
/// On-device OCR with Google ML Kit Text Recognition v2 (Latin script, model bundled in the app: free,
/// offline, no network). Turns a page picture into <see cref="TextWord"/>s in page-normalised coordinates,
/// the same shape the PDF's own text layer produces, so selection / copy / search treat both alike.
/// Known weak spot (plan section 3): Vietnamese diacritics are recognised less reliably than plain Latin
/// text -- to be judged on real scanned documents.
/// </summary>
public sealed class OcrService
{
	private ITextRecognizer? _recognizer;

	private ITextRecognizer Recognizer => _recognizer ??= TextRecognition.GetClient(TextRecognizerOptions.DefaultOptions)!;

	public async Task<IReadOnlyList<TextWord>> RecognizeAsync(Bitmap bitmap)
	{
		using InputImage image = InputImage.FromBitmap(bitmap, 0)!;
		Java.Lang.Object? raw = await Recognizer.Process(image).AsAsync<Java.Lang.Object>();
		var result = (MlText?)raw;
		var found = new List<(string Text, NRect Box)>();
		if (result == null) return [];

		float w = bitmap.Width, h = bitmap.Height;
		foreach (MlText.TextBlock block in result.TextBlocks)
			foreach (MlText.Line line in block.Lines)
				foreach (MlText.Element element in line.Elements)
				{
					Rect? b = element.BoundingBox;
					string text = element.Text?.Trim() ?? "";
					if (b == null || text.Length == 0) continue;
					found.Add((text, new NRect(Math.Clamp(b.Left / w, 0, 1), Math.Clamp(b.Top / h, 0, 1), Math.Clamp(b.Right / w, 0, 1), Math.Clamp(b.Bottom / h, 0, 1))));
				}
		return ReadingOrder.ArrangeWords(found);
	}
}
