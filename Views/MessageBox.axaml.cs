using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using framenion.Src;
using System;
using System.Threading.Tasks;

namespace framenion;

public partial class MessageBox : Window
{
	public string? Message { get; }
	public bool ShowCancelButton { get; }
	public string OkButtonText { get; } = "OK";
	public string CancelButtonText { get; } = "Cancel";

	public MessageBox()
	{
		InitializeComponent();
		DataContext = this;
	}

	public MessageBox(string message, string title, bool showCancelButton = false, string okButtonText = "OK", string cancelButtonText = "Cancel")
	{
		Message = message;
		ShowCancelButton = showCancelButton;
		OkButtonText = okButtonText;
		CancelButtonText = cancelButtonText;
		InitializeComponent();
		DataContext = this;
		Title = title;
	}

	/// <summary>
	/// Safe to call from any thread. Most callers are catch blocks, which can run on a worker,
	/// and constructing a Window off the dispatcher throws.
	/// </summary>
	public static void Show(string title, string message)
	{
		Log.Warn($"Dialog: {title} - {message}");
		Dispatcher.UIThread.Post(() => {
			var msgBox = new MessageBox(message, title, showCancelButton: false);
			var owner = AppData.MainWindow;
			if (owner != null && owner.IsVisible) {
				_ = msgBox.ShowDialog(owner);
			} else {
				msgBox.Show();
			}
		});
	}

	public static Task<bool> AskYesNo(string title, string message, string okButtonText = "Yes", string cancelButtonText = "No")
	{
		var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		Dispatcher.UIThread.Post(async () => {
			try {
				var owner = AppData.MainWindow;
				if (owner == null || !owner.IsVisible) {
					completion.SetResult(false);
					return;
				}

				var msgBox = new MessageBox(message, title, showCancelButton: true, okButtonText: okButtonText, cancelButtonText: cancelButtonText);
				completion.SetResult(await msgBox.ShowDialog<bool?>(owner) ?? false);
			} catch (Exception ex) {
				completion.SetException(ex);
			}
		});
		return completion.Task;
	}

	private void Ok_Click(object? sender, RoutedEventArgs e) => Close(true);

	private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
