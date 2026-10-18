using OpenCvSharp;
using System;

namespace framenion.Src.Ocr;

/// <summary>
/// Turns a raw reward-card crop into the shape both OCR engines were calibrated for:
/// a ~720px wide strip of dark text on a white background.
/// </summary>
public static class OcrPreprocess
{
	// The engines were calibrated on 1080p title strips upscaled 3x (~700px wide, one text line
	// ~60px tall). A 4K strip is already twice that wide, so a fixed 3x would quadruple the OCR
	// pixels instead of hitting the calibrated size. Target the width, not the factor.
	private const int stripUpscale = 3;
	private const int stripTargetWidth = 720;

	// Quiet margin around the strip; the Windows engine loses glyphs that touch the image edge.
	private const int stripPadding = 8;

	// A strip that spans a card gap is flat backdrop, not text. Skipping it before OCR keeps the
	// per-layout probing cheap and stops empty slots from producing fuzzy matches.
	private const int minContrastRange = 40;

	public static int StripTargetWidth(int sourceWidth)
	{
		if (sourceWidth <= 0) return 0;
		return Math.Max(8, (int)Math.Round(Math.Min(sourceWidth * (double)stripUpscale, stripTargetWidth)));
	}

	/// <summary>
	/// Extracts a horizontal band of <paramref name="source"/>, upscales it to the calibrated
	/// width and binarizes it with Otsu. Returns null when the band holds no usable contrast.
	/// </summary>
	public static Mat? BinarizeRegion(Mat source, double topFraction, double heightFraction)
	{
		var gray = PrepareGray(source, topFraction, heightFraction);
		if (gray == null) return null;

		try {
			var mono = new Mat();
			Cv2.Threshold(gray, mono, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

			// Whichever class covers more of the crop is the background, so a light UI theme (dark
			// text on a pale card) comes out the same way up as the default. Always emit dark text
			// on white, whichever way round the source was.
			bool brightIsBackground = Cv2.CountNonZero(mono) * 2 > mono.Rows * mono.Cols;
			if (!brightIsBackground) {
				Cv2.BitwiseNot(mono, mono);
			}

			Cv2.CopyMakeBorder(mono, mono, stripPadding, stripPadding, stripPadding, stripPadding, BorderTypes.Constant, Scalar.White);
			return mono;
		} finally {
			gray.Dispose();
		}
	}

	/// <summary>
	/// Same scaling and contrast stretch as <see cref="BinarizeRegion"/> but without the Otsu
	/// threshold. A riven card sits over a bright diorama, and how much card background falls
	/// inside the crop moves the threshold enough to wipe the text out; the Windows engine reads
	/// grayscale perfectly well, so this variant is tried alongside the binarized one.
	/// </summary>
	public static Mat? EnhanceRegion(Mat source, double topFraction, double heightFraction)
	{
		var gray = PrepareGray(source, topFraction, heightFraction);
		if (gray == null) return null;

		try {
			var padded = new Mat();
			Cv2.CopyMakeBorder(gray, padded, stripPadding, stripPadding, stripPadding, stripPadding, BorderTypes.Replicate);
			return padded;
		} finally {
			gray.Dispose();
		}
	}

	/// <summary>Crops the band, scales it to the calibrated width and stretches its contrast.</summary>
	private static Mat? PrepareGray(Mat source, double topFraction, double heightFraction)
	{
		int srcWidth = source.Width;
		int srcHeight = source.Height;
		if (srcWidth < 8 || srcHeight < 8) return null;

		int top = Math.Clamp((int)Math.Round(srcHeight * topFraction), 0, srcHeight - 1);
		int height = Math.Clamp((int)Math.Round(srcHeight * heightFraction), 1, srcHeight - top);

		int targetWidth = StripTargetWidth(srcWidth);
		int targetHeight = Math.Max(1, (int)Math.Round(height * (double)targetWidth / srcWidth));

		using var band = source[new Rect(0, top, srcWidth, height)].Clone();
		var gray = new Mat();
		if (band.Channels() == 1) {
			band.CopyTo(gray);
		} else {
			Cv2.CvtColor(band, gray, band.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
		}
		Cv2.Resize(gray, gray, new Size(targetWidth, targetHeight), 0, 0, InterpolationFlags.Lanczos4);

		Cv2.MinMaxLoc(gray, out double min, out double max);
		if (max - min < minContrastRange) {
			gray.Dispose();
			return null;
		}

		// Stretch to the full range first, the way sharp's normalise() does, so Otsu sees the
		// same histogram the reference pipeline was tuned against.
		Cv2.Normalize(gray, gray, 0, 255, NormTypes.MinMax);
		return gray;
	}
}
