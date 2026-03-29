using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using framenion.Src;
using System;
using System.Threading.Tasks;

namespace framenion;

public partial class RelicRewardWindow : Window
{
	public string ItemName { get; set; } = string.Empty;
	public string Ducats { get; set; } = "0";
	public string Price { get; set; } = "0";
	
	public RelicRewardWindow()
	{
		InitializeComponent();
	}

	public RelicRewardWindow(string name, string price, string ducats, int x, int y, int width)
	{
		ItemName = name;
		Ducats = ducats;
		Price = price;
		InitializeComponent();
		DataContext = this;
		Width = width;
		this.WindowStartupLocation = WindowStartupLocation.Manual;
		this.Position = new PixelPoint(x, y);
		this.IsHitTestVisible = false;
	}

	public static async Task Display(Window owner, RewardInfo reward, int x, int y, int width, TimeSpan duration)
	{
		await Dispatcher.UIThread.InvokeAsync(() => {
			var win = new RelicRewardWindow(reward.ItemName, reward.Platinum, reward.Ducats, x, y, width);
			try { win.ShowActivated = false; } catch { }
			win.Show(owner);
			_ = Task.Run(async () => {
				await Task.Delay(duration);
				await Dispatcher.UIThread.InvokeAsync(() => {
					if (win.IsVisible) win.Close();
				});
			});
		});
	}
}
