using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using framenion.Src;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace framenion;

/// <summary>One editable region in the scan-regions editor.</summary>
public sealed class ScanRegionRow : INotifyPropertyChanged
{
	private static readonly IBrush overriddenBrush = new SolidColorBrush(Color.Parse("#7FD858"));
	private static readonly IBrush automaticBrush = new SolidColorBrush(Color.Parse("#D0D0D0"));

	private ScanRegion? region;

	public event PropertyChangedEventHandler? PropertyChanged;

	public required ScanRegionKind Kind { get; init; }

	public string Label => ScanRegions.Label(Kind);

	public string Hint => ScanRegions.Hint(Kind);

	public ScanRegion? Region
	{
		get => region;
		set {
			region = value;
			// Every readout and the numeric boxes all derive from this.
			Raise(nameof(Region));
			Raise(nameof(HasOverride));
			Raise(nameof(Status));
			Raise(nameof(StatusBrush));
			Raise(nameof(XPercent));
			Raise(nameof(YPercent));
			Raise(nameof(WidthPercent));
			Raise(nameof(HeightPercent));
		}
	}

	public bool HasOverride => region != null;

	public string Status => region != null
		? $"Custom:  {region.Value}"
		: $"Automatic  (default is {ScanRegions.DefaultFor(Kind)})";

	public IBrush StatusBrush => region != null ? overriddenBrush : automaticBrush;

	/// <summary>Effective region for previewing, falling back to the documented default.</summary>
	public ScanRegion Effective => region ?? ScanRegions.DefaultFor(Kind);

	public decimal XPercent
	{
		get => (decimal)((region?.X ?? ScanRegions.DefaultFor(Kind).X) * 100);
		set => Update(r => r with { X = (double)value / 100 });
	}

	public decimal YPercent
	{
		get => (decimal)((region?.Y ?? ScanRegions.DefaultFor(Kind).Y) * 100);
		set => Update(r => r with { Y = (double)value / 100 });
	}

	public decimal WidthPercent
	{
		get => (decimal)((region?.Width ?? ScanRegions.DefaultFor(Kind).Width) * 100);
		set => Update(r => r with { Width = (double)value / 100 });
	}

	public decimal HeightPercent
	{
		get => (decimal)((region?.Height ?? ScanRegions.DefaultFor(Kind).Height) * 100);
		set => Update(r => r with { Height = (double)value / 100 });
	}

	private void Update(Func<ScanRegion, ScanRegion> change)
	{
		var updated = change(Effective).Clamped();
		if (updated == region) return;

		region = updated;
		Raise(nameof(HasOverride));
		Raise(nameof(Status));
		Raise(nameof(StatusBrush));
	}

	private void Raise([CallerMemberName] string? name = null)
		=> PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class ScanRegionsWindow : Window
{
	public ObservableCollection<ScanRegionRow> Rows { get; } = [];

	public ScanRegionsWindow()
	{
		InitializeComponent();
		DataContext = this;

		var saved = AppData.AppSettings.Regions;
		foreach (var kind in ScanRegions.EditOrder) {
			Rows.Add(new ScanRegionRow { Kind = kind, Region = saved.Get(kind) });
		}
	}

	private ScanRegionRow? RowFor(object? sender)
		=> sender is Control { Tag: ScanRegionKind kind } ? Rows.FirstOrDefault(r => r.Kind == kind) : null;

	private async void OnPickClick(object? sender, RoutedEventArgs e)
	{
		var row = RowFor(sender);
		if (row == null) return;

		try {
			// The picker focuses Warframe and parks every Framenion window before capturing.
			var picked = await RegionPickerWindow.PickAsync(row.Kind, row.Effective);
			if (picked != null) {
				row.Region = picked;
				Log.Info($"[Regions] Picked {ScanRegions.Label(row.Kind)}: {picked.Value}. Not stored until Save is pressed.");
			} else {
				Log.Info($"[Regions] Pick cancelled for {ScanRegions.Label(row.Kind)}.");
			}
		} catch (Exception ex) {
			Log.Error("Region picker failed.", ex);
			MessageBox.Show("Scan regions", $"Could not pick a region: {ex.Message}");
		} finally {
			Activate();
		}
	}

	private async void OnPreviewClick(object? sender, RoutedEventArgs e)
	{
		var row = RowFor(sender);
		if (row == null) return;

		try {
			await RegionPreviewWindow.FlashAsync(row.Effective, ScanRegions.Label(row.Kind));
		} catch (Exception ex) {
			Log.Warn("Could not preview the region.", ex);
		}
	}

	private void OnResetClick(object? sender, RoutedEventArgs e)
	{
		var row = RowFor(sender);
		if (row != null) row.Region = null;
	}

	private async void OnSaveClick(object? sender, RoutedEventArgs e)
	{
		try {
			var settings = await AppSettings.LoadAsync();
			foreach (var row in Rows) {
				settings.Regions.Set(row.Kind, row.Region);
			}

			await settings.SaveAsync();
			// Publish immediately so the next scan uses the new regions.
			AppData.AppSettings.Regions = settings.Regions;

			Log.Info($"Scan regions saved: relic={Describe(settings.Regions.RelicRewards)} "
				+ $"rivenReroll={Describe(settings.Regions.RivenReroll)} rivenRerollWeapon={Describe(settings.Regions.RivenRerollWeapon)} "
				+ $"rivenChat={Describe(settings.Regions.RivenChat)} rivenChatWeapon={Describe(settings.Regions.RivenChatWeapon)}");
			Close();
		} catch (Exception ex) {
			Log.Error("Failed to save scan regions.", ex);
			MessageBox.Show("Scan regions", $"Could not save: {ex.Message}");
		}
	}

	private static string Describe(ScanRegion? region) => region?.ToString() ?? "automatic";

	private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
