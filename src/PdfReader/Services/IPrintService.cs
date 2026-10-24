namespace PdfReader.Services;

/// <summary>
/// Hands a PDF file off to the platform's own print framework -- printer discovery (Wi-Fi/network via
/// installed print services such as Mopria, which most Android phones already have; USB/Bluetooth via a
/// printer-vendor's own print service app), paper size, copies and page-range selection are all the
/// platform's native print dialog, not anything this app builds itself. Android's Print Service
/// architecture deliberately keeps real printer discovery/communication out of individual apps' hands.
/// </summary>
public interface IPrintService
{
	bool IsSupported { get; }

	/// <summary><paramref name="pdfPath"/> must already exist and be the exact bytes to print (the caller
	/// decides whether that is the original file or one with annotations baked in). <paramref name="pageCount"/>
	/// is reported to the OS print dialog so it can offer a custom page-range picker in addition to "All".</summary>
	Task PrintAsync(string pdfPath, string jobName, int pageCount);
}
