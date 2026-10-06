using Microsoft.Maui.Handlers;
using PdfReader.Views;

namespace PdfReader.Platforms.Android;

/// <summary>Creates the native <see cref="PdfCanvasView"/> for a <see cref="PdfViewerView"/>.</summary>
internal sealed class PdfViewerHandler : ViewHandler<PdfViewerView, PdfCanvasView>
{
	public static readonly IPropertyMapper<PdfViewerView, PdfViewerHandler> Mapper =
		new PropertyMapper<PdfViewerView, PdfViewerHandler>(ViewHandler.ViewMapper);

	public PdfViewerHandler() : base(Mapper)
	{
	}

	protected override PdfCanvasView CreatePlatformView() => new(Context);

	protected override void DisconnectHandler(PdfCanvasView platformView)
	{
		platformView.Unload();
		base.DisconnectHandler(platformView);
	}
}
