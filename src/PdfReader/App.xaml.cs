using Microsoft.Extensions.DependencyInjection;

namespace PdfReader;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// The start screen first; it builds the shell (main screen) while it animates, then hands over --
		// see Views/SplashPage.xaml.cs. Same technique as the sibling DocScanner repo.
		return new Window(new Views.SplashPage(() => new AppShell()));
	}
}