// PaddleOCR is linux-only. Windows reads text with Windows.Media.Ocr, which is more accurate on
// Warframe's UI font, needs no model download, and avoids ~266 MB of native runtime.
#if !WINDOWS
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace framenion.Src.Ocr;

public sealed class PaddleOcrEngine : IOcrEngine
{
	private readonly PaddleOcrAll engine;
	private readonly object gate = new();
	private bool disposed;

	public string Name => "PaddleOCR";

	public PaddleOcrEngine(FullOcrModel model)
	{
		engine = new PaddleOcrAll(model, PaddleDevice.Onnx()) {
			AllowRotateDetection = false,
			Enable180Classification = false,
		};
	}

	public Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(Mat image)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if (image.Empty()) return Task.FromResult<IReadOnlyList<OcrTextLine>>([]);

		using var input = image.Channels() == 1 ? image.CvtColor(ColorConversionCodes.GRAY2BGR) : image.Clone();
		PaddleOcrResult result;
		lock (gate) {
			result = engine.Run(input);
		}

		var lines = new List<OcrTextLine>(result.Regions.Length);
		foreach (var region in result.Regions) {
			if (string.IsNullOrWhiteSpace(region.Text)) continue;
			lines.Add(new OcrTextLine(region.Text, region.Rect.BoundingRect()));
		}
		return Task.FromResult<IReadOnlyList<OcrTextLine>>(lines);
	}

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		engine.Dispose();
	}
}
#endif
