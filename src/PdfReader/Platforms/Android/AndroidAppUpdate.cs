using Android.App;
using Android.Gms.Extensions;
using Xamarin.Google.Android.Play.Core.AppUpdate;
using Xamarin.Google.Android.Play.Core.AppUpdate.Install.Model;

namespace PdfReader.Services;

/// <summary>
/// Forces every user onto the latest Play-published build: whenever Play reports a newer version exists,
/// blocks the app behind Play's own full-screen update UI (AppUpdateType.Immediate) until they update or
/// quit. No server of our own. Ported from DocScanner's AndroidAppUpdate (Mobile-doc-scanner repo); the
/// only change is the log call on failure (that repo's Core.Perf -> a plain Android log line here).
/// </summary>
internal static class AndroidAppUpdate
{
	public static async void CheckAndForce(Activity activity)
	{
		try
		{
			IAppUpdateManager manager = AppUpdateManagerFactory.Create(activity);
			AppUpdateInfo info = await manager.GetAppUpdateInfo().AsAsync<AppUpdateInfo>();
			int availability = info.UpdateAvailability();

			bool resuming = availability == UpdateAvailability.DeveloperTriggeredUpdateInProgress;
			bool starting = availability == UpdateAvailability.UpdateAvailable && info.IsUpdateTypeAllowed(AppUpdateType.Immediate);
			if (!resuming && !starting) return;

			const int requestCode = 0x5047;
			manager.StartUpdateFlowForResult(info, activity,
				AppUpdateOptions.NewBuilder(AppUpdateType.Immediate).Build(), requestCode);
		}
		catch (Exception ex)
		{
			// No Play Services, or a transient Play error: never let this block or crash the app.
			Android.Util.Log.Warn("PdfReader", $"update check failed: {ex.Message}");
		}
	}
}
