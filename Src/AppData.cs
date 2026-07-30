using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Sdcb.PaddleOCR;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace framenion.Src;

public class AppData
{
	public static Window? MainWindow { get; set; }

	public static string AppDataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Framenion");
	public static string CacheDir { get; } = Path.Combine(AppDataDir, "cache");
	public static string IconsCacheDir { get; } = Path.Combine(CacheDir, "icons");

	public static ConcurrentDictionary<string, Lazy<Bitmap?>> BitmapCache { get; } = new(StringComparer.Ordinal);
	public static  SemaphoreSlim IconDownloadSemaphore { get; } = new(10, 10);
	public static HttpClient HttpClient { get; } = new() {
		BaseAddress = new Uri("http://content.warframe.com/PublicExport"),
	};
	public static PaddleOcrAll? PaddleEngine { get; set; }
	public static WarframeMonitor? Monitor { get; } = new();

	public static AppSettings AppSettings { get; set; } = new();

	public static List<RelicRewardWindow> RewardWindows { get; } = [];

	public static async Task<Stream> GetStreamAsync(string url)
	{
		var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsStreamAsync();
	}

	public static async Task DownloadToFileAsync(string url, string filePath)
	{
		using var stream = await GetStreamAsync(url);
		using var fileStream = File.Create(filePath);
		await stream.CopyToAsync(fileStream);
	}
}
