#if WINDOWS
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace framenion.Src.Ocr;

/// <summary>
/// OCR backed by the Windows.Media.Ocr (WinRT) engine shipped with Windows. It reads the
/// stylized, low-contrast fonts Warframe uses noticeably better than the PaddleOCR models and
/// needs no model download, but it depends on an installed OCR language pack.
/// </summary>
public sealed class WindowsMediaOcrEngine : IOcrEngine
{
	private readonly OcrEngine engine;
	private readonly SemaphoreSlim gate;
	private bool disposed;

	public string Name => $"Windows.Media.Ocr ({engine.RecognizerLanguage.LanguageTag})";

	private WindowsMediaOcrEngine(OcrEngine engine)
	{
		this.engine = engine;
		gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
	}

	public static WindowsMediaOcrEngine? TryCreate()
	{
		try {
			var engine = OcrEngine.TryCreateFromLanguage(new Language("en")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
			return engine == null ? null : new WindowsMediaOcrEngine(engine);
		} catch {
			return null;
		}
	}

	public async Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(Mat image)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if (image.Empty() || image.Width > OcrEngine.MaxImageDimension || image.Height > OcrEngine.MaxImageDimension) return [];

		await gate.WaitAsync();
		try {
			using var bitmap = await DecodeAsync(image);
			var result = await engine.RecognizeAsync(bitmap);
			var lines = new List<OcrTextLine>(result.Lines.Count);
			foreach (var line in result.Lines) {
				lines.Add(new OcrTextLine(line.Text, LineBox(line)));
			}
			return lines;
		} finally {
			gate.Release();
		}
	}

	private static async Task<SoftwareBitmap> DecodeAsync(Mat image)
	{
		Cv2.ImEncode(".bmp", image, out byte[] encoded);

		var stream = new InMemoryRandomAccessStream();
		var writer = new DataWriter(stream);
		try {
			writer.WriteBytes(encoded);
			await writer.StoreAsync();
			await writer.FlushAsync();
		} finally {
			// DetachStream must run before Dispose: disposing the writer closes the underlying
			// stream, which makes the Seek below fail with RO_E_CLOSED (0x80000013).
			writer.DetachStream();
			writer.Dispose();
		}

		stream.Seek(0);
		var decoder = await BitmapDecoder.CreateAsync(stream);
		return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
	}

	private static Rect LineBox(OcrLine line)
	{
		double left = double.MaxValue, top = double.MaxValue, right = 0, bottom = 0;
		foreach (var word in line.Words) {
			var box = word.BoundingRect;
			left = Math.Min(left, box.X);
			top = Math.Min(top, box.Y);
			right = Math.Max(right, box.X + box.Width);
			bottom = Math.Max(bottom, box.Y + box.Height);
		}

		if (left == double.MaxValue) return default;
		return new Rect((int)Math.Round(left), (int)Math.Round(top), (int)Math.Max(0, Math.Round(right - left)), (int)Math.Max(0, Math.Round(bottom - top)));
	}

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		gate.Dispose();
	}
}
#endif
