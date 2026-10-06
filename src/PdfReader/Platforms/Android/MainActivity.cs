using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace PdfReader;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// Opening a PDF from another app: a file manager's "Open with" (VIEW) or "Share" (SEND) sheet. Two
// separate filters (not one with two actions) because SEND's data is in ClipData/EXTRA_STREAM, not
// Intent.Data -- ResolvePendingOpen below reads whichever one actually carries the Uri.
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataMimeType = "application/pdf")]
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "application/pdf")]
public class MainActivity : MauiAppCompatActivity
{
	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		ResolvePendingOpen(Intent);
	}

	// LaunchMode.SingleTop: an already-running activity gets the new Intent here instead of a fresh
	// OnCreate. Intent must be updated too, or a later OnResume-triggered recreation would see the stale one.
	protected override void OnNewIntent(Intent? intent)
	{
		base.OnNewIntent(intent);
		Intent = intent;
		ResolvePendingOpen(intent);
	}

	/// <summary>Google's own recommended hook for in-app updates: re-checks every time the app comes
	/// back to the foreground, so an Immediate update interrupted last time (user left mid-download) resumes.</summary>
	protected override void OnResume()
	{
		base.OnResume();
		Services.AndroidAppUpdate.CheckAndForce(this);
	}

	private static void ResolvePendingOpen(Intent? intent)
	{
		if (intent == null) return;
		Android.Net.Uri? uri = intent.Action switch
		{
			Intent.ActionView => intent.Data,
			Intent.ActionSend => intent.ClipData?.ItemCount > 0
				? intent.ClipData.GetItemAt(0)?.Uri
				: intent.GetParcelableExtra(Intent.ExtraStream) as Android.Net.Uri,
			_ => null,
		};
		if (uri != null) Services.PendingPdfOpen.Set(uri.ToString()!);
	}
}
