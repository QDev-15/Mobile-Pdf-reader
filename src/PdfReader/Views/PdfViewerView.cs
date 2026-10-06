using PdfReader.Platforms.Android;

namespace PdfReader.Views;

/// <summary>The MAUI-side handle for the native reading surface (<see cref="PdfCanvasView"/>). All the
/// work happens in the native view, reached through <see cref="Engine"/> once the handler is attached
/// (after the page has appeared).</summary>
public sealed class PdfViewerView : View
{
	public PdfCanvasView? Engine => Handler?.PlatformView as PdfCanvasView;
}
