using PdfReader.Core;

namespace PdfReader.Views;

/// <summary>
/// The app's own pop-ups: rounded corners on a light-grey card with black text, replacing the stock
/// Android dialogs. A pop-up is a dim layer added on top of the current page's root grid, so it lives and
/// dies with the page (no extra windows, no page-lifecycle side effects) and the hardware Back button can
/// close it (<see cref="TryDismiss"/>). All methods are extensions on <see cref="Page"/> (a Shell stands for
/// its current page) with the same shapes as the stock Display* calls, so call sites read the same.
/// </summary>
public static class Dialogs
{
	private static readonly Color CardColor = Color.FromArgb("#EEEEEE");
	private static readonly Color Ink = Color.FromArgb("#111111");
	private static readonly Color Soft = Color.FromArgb("#5F5F5F");
	private static readonly Color Line = Color.FromArgb("#D5D5D5");

	private static System.Action? _dismiss;

	/// <summary>Closes the pop-up on screen, if any (as if cancelled). True when there was one.</summary>
	public static bool TryDismiss()
	{
		System.Action? dismiss = _dismiss;
		if (dismiss == null) return false;
		dismiss();
		return true;
	}

	public static bool IsOpen => _dismiss != null;

	// ------------------------------------------------------------------ public API

	public static Task AlertAsync(this Page host, string title, string message, string ok) =>
		ShowAsync<bool>(host, close => Card(title, message, null, [Btn(ok, () => close(true), bold: true)]), false);

	public static Task<bool> AlertAsync(this Page host, string title, string message, string accept, string cancel) =>
		ShowAsync<bool>(host, close => Card(title, message, null, [Btn(cancel, () => close(false)), Btn(accept, () => close(true), bold: true)]), false);

	public static Task<string?> ChoiceAsync(this Page host, string title, string cancel, string? destruction, params string[] options) =>
		ShowAsync<string?>(host, close =>
		{
			var list = new VerticalStackLayout { Spacing = 0 };
			foreach (string option in options.Where(o => !string.IsNullOrEmpty(o)))
			{
				string o = option;
				list.Add(Row(o, () => close(o), o == destruction));
			}
			var scroll = new ScrollView { Content = list, MaximumHeightRequest = MaxListHeight(host) };
			return Card(title, null, scroll, [Btn(cancel, () => close(null))]);
		}, null);

	public static Task<string?> PromptAsync(this Page host, string title, string message, string accept = "OK", string cancel = "Huỷ",
		string? placeholder = null, int maxLength = -1, Keyboard? keyboard = null, string initialValue = "") =>
		ShowAsync<string?>(host, close =>
		{
			var entry = new Entry
			{
				Text = initialValue,
				Placeholder = placeholder,
				Keyboard = keyboard ?? Keyboard.Default,
				TextColor = Ink,
				PlaceholderColor = Soft,
				FontSize = 15,
			};
			if (maxLength > 0) entry.MaxLength = maxLength;
			entry.Completed += (_, _) => close(entry.Text);
			// Strict focus <-> keyboard coupling: whatever makes the entry lose focus (tapping a button,
			// the system picking a different view, ...) must also put the keyboard away -- Android does
			// not reliably do this on its own just because MAUI's IsFocused flipped.
			entry.Unfocused += (_, _) => HideKeyboard();
			Dispatcher(host)?.Dispatch(() => entry.Focus());
			var field = new Border
			{
				Content = entry,
				Padding = new Thickness(12, 0),
				BackgroundColor = Colors.White,
				Stroke = Line,
				StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
			};
			return Card(title, message, field, [Btn(cancel, () => close(null)), Btn(accept, () => close(entry.Text ?? ""), bold: true)]);
		}, null);

	/// <summary>Asks for a file name and a quality (radio buttons). Null when cancelled.</summary>
	public static Task<(string Name, PdfQuality Quality)?> ExportDialogAsync(this Page host, string title, string defaultName, PdfQuality defaultQuality) =>
		ShowAsync<(string, PdfQuality)?>(host, close =>
		{
			var entry = new Entry { Text = defaultName, TextColor = Ink, FontSize = 15, MaxLength = 120 };
			entry.Unfocused += (_, _) => HideKeyboard();
			var field = new Border
			{
				Content = entry,
				Padding = new Thickness(12, 0),
				BackgroundColor = Colors.White,
				Stroke = Line,
				StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
			};
			var body = new VerticalStackLayout { Spacing = 6 };
			body.Add(new Label { Text = "Tên file", FontSize = 12, TextColor = Soft });
			body.Add(field);
			body.Add(new Label { Text = "Chất lượng", FontSize = 12, TextColor = Soft, Margin = new Thickness(0, 8, 0, 0) });
			PdfQuality chosen = defaultQuality;
			string group = "export-quality-" + Guid.NewGuid().ToString("N");
			foreach (PdfQuality q in PdfQuality.All)
			{
				PdfQuality quality = q;
				var radio = new RadioButton
				{
					Content = q.Label,
					GroupName = group,
					IsChecked = q.Key == defaultQuality.Key,
					TextColor = Ink,
					FontSize = 14,
				};
				radio.CheckedChanged += (_, e) =>
				{
					if (e.Value) chosen = quality;
				};
				body.Add(radio);
			}
			return Card(title, null, body, [
				Btn("Huỷ", () => close(null)),
				Btn("Xuất", () =>
				{
					string name = entry.Text?.Trim() ?? "";
					close(name.Length == 0 ? null : (name, chosen));
				}, bold: true),
			]);
		}, null);

	/// <summary>A short message at the bottom of the page: rounded, grey, black text, gone after ~2 s. Replaces the
	/// stock Android toast, which cannot be styled.</summary>
	public static void Toast(this Page host, string text)
	{
		ContentPage? page = Page(host);
		if (page?.Content is not Grid root) return;
		var bubble = new Border
		{
			Content = new Label { Text = text, TextColor = Ink, FontSize = 13.5, HorizontalTextAlignment = TextAlignment.Center },
			BackgroundColor = CardColor,
			Stroke = Line,
			StrokeThickness = 1,
			Padding = new Thickness(18, 11),
			Margin = new Thickness(30, 0, 30, 96),
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.End,
			ZIndex = 900,
			InputTransparent = true,
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 22 },
		};
		Grid.SetRowSpan(bubble, Math.Max(1, root.RowDefinitions.Count));
		Grid.SetColumnSpan(bubble, Math.Max(1, root.ColumnDefinitions.Count));
		root.Add(bubble);
		page.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(2200), () => root.Remove(bubble));
	}

	// ------------------------------------------------------------------ plumbing

	/// <summary>Puts the soft keyboard away. A text box inside a pop-up takes focus (and the keyboard) with it;
	/// when the pop-up goes, the keyboard must not stay behind.</summary>
	public static void HideKeyboard()
	{
		try
		{
			var activity = Platform.CurrentActivity;
			Android.Views.View? focus = activity?.CurrentFocus ?? activity?.Window?.DecorView;
			if (activity == null || focus == null) return;
			var imm = (Android.Views.InputMethods.InputMethodManager?)activity.GetSystemService(Android.Content.Context.InputMethodService);
			imm?.HideSoftInputFromWindow(focus.WindowToken, Android.Views.InputMethods.HideSoftInputFlags.None);
			focus.ClearFocus();
		}
		catch
		{
			// best effort
		}
	}

	private static IDispatcher? Dispatcher(Page host) => (Page(host) as BindableObject)?.Dispatcher;

	private static ContentPage? Page(Page host) => host is Shell shell ? shell.CurrentPage as ContentPage : host as ContentPage;

	private static double MaxListHeight(Page host) => Math.Max(160, (Page(host)?.Height ?? 800) * 0.5);

	private static Task<T> ShowAsync<T>(Page host, Func<System.Action<T>, View> build, T cancelValue)
	{
		ContentPage? page = Page(host);
		if (page?.Content is not Grid root) return Task.FromResult(cancelValue);

		var tcs = new TaskCompletionSource<T>();
		var layer = new Grid { BackgroundColor = Color.FromArgb("#88000000"), ZIndex = 1000 };
		Grid.SetRowSpan(layer, Math.Max(1, root.RowDefinitions.Count));
		Grid.SetColumnSpan(layer, Math.Max(1, root.ColumnDefinitions.Count));

		void Close(T value)
		{
			if (_dismiss == null && !root.Children.Contains(layer)) return;
			HideKeyboard();
			root.Remove(layer);
			_dismiss = null;
			tcs.TrySetResult(value);
		}

		View card = build(Close);
		// Deliberately no tap-on-scrim-to-dismiss: a pop-up must be closed through one of its own
		// buttons (or the hardware Back button, via TryDismiss) -- tapping outside it used to close it
		// as if cancelled, which lost the person's place in a flow without them asking to leave it.
		layer.Add(card);

		if (_dismiss != null) TryDismiss(); // never stack two
		_dismiss = () => Close(cancelValue);
		root.Add(layer);
		return tcs.Task;
	}

	private static View Card(string title, string? message, View? body, IList<View> actions)
	{
		var content = new VerticalStackLayout { Spacing = 10 };
		content.Add(new Label { Text = title, FontSize = 17, FontAttributes = FontAttributes.Bold, TextColor = Ink });
		if (!string.IsNullOrEmpty(message))
			content.Add(new Label { Text = message, FontSize = 14, TextColor = Soft, LineHeight = 1.2 });
		if (body != null) content.Add(body);

		var buttons = new HorizontalStackLayout { Spacing = 4, HorizontalOptions = LayoutOptions.End, Margin = new Thickness(0, 6, 0, 0) };
		foreach (View a in actions) buttons.Add(a);
		content.Add(buttons);

		return new Border
		{
			Content = content,
			BackgroundColor = CardColor,
			Stroke = Colors.Transparent,
			StrokeThickness = 0,
			Padding = new Thickness(22, 20, 14, 10),
			Margin = new Thickness(26, 0),
			HorizontalOptions = LayoutOptions.Fill,
			VerticalOptions = LayoutOptions.Center,
			MaximumWidthRequest = 440,
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 24 },
		};
	}

	private static Button Btn(string text, System.Action onTap, bool bold = false)
	{
		var button = new Button
		{
			Text = text,
			TextColor = Ink,
			BackgroundColor = Colors.Transparent,
			FontSize = 14,
			FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
			Padding = new Thickness(14, 0),
			HeightRequest = 42,
			CornerRadius = 21,
		};
		button.Clicked += (_, _) => onTap();
		return button;
	}

	private static View Row(string text, System.Action onTap, bool destructive)
	{
		var row = new Grid { Padding = new Thickness(2, 13), BackgroundColor = Colors.Transparent };
		row.Add(new Label { Text = text, FontSize = 15, TextColor = destructive ? Color.FromArgb("#C62828") : Ink, VerticalOptions = LayoutOptions.Center });
		var tap = new TapGestureRecognizer();
		tap.Tapped += (_, _) => onTap();
		row.GestureRecognizers.Add(tap);
		var wrapper = new VerticalStackLayout { Spacing = 0 };
		wrapper.Add(row);
		wrapper.Add(new BoxView { HeightRequest = 1, Color = Line });
		return wrapper;
	}
}
