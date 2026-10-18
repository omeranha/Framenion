#if !WINDOWS
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Online;
using System;
#endif
using System.Threading.Tasks;

namespace framenion.Src.Ocr;

public static class OcrEngineFactory
{
	/// <summary>Shown when no engine could be built, with the actionable next step per platform.</summary>
	public const string UnavailableMessage =
#if WINDOWS
		"Windows OCR is unavailable. Install the English OCR language feature: Settings > Time & language > "
		+ "Language & region > English > Language options > Optional features > Optical character recognition.";
#else
		"No OCR engine could be initialized. See the log for details.";
#endif

	/// <summary>
	/// Builds the platform's OCR engine. Windows uses the built-in Windows.Media.Ocr engine,
	/// which reads Warframe's font better than the PaddleOCR models and needs no model
	/// download; linux falls back to PaddleOCR.
	/// </summary>
	public static Task<IOcrEngine?> CreateAsync()
	{
#if WINDOWS
		var windows = WindowsMediaOcrEngine.TryCreate();
		if (windows == null) Log.Error(UnavailableMessage);
		return Task.FromResult<IOcrEngine?>(windows);
#else
		return CreatePaddleAsync();
#endif
	}

#if !WINDOWS
	private static async Task<IOcrEngine?> CreatePaddleAsync()
	{
		try {
			FullOcrModel model = await OnlineFullModels.EnglishV4.DownloadAsync();
			return new PaddleOcrEngine(model);
		} catch (Exception ex) {
			Log.Error("Could not initialize PaddleOCR.", ex);
			return null;
		}
	}
#endif
}
