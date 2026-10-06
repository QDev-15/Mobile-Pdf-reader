using PdfReader.Views;

namespace PdfReader;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
		Routing.RegisterRoute(nameof(ReaderPage), typeof(ReaderPage));
	}
}
