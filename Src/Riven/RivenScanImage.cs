using OpenCvSharp;
using System;

namespace framenion.Src.Riven;

/// <summary>
/// Crop geometry for the riven card, as fractions of the game's content area.
/// Measured on the Kuva reroll screen (OmegaRerollSelection).
/// </summary>
public static class RivenScanImage
{
	/// <summary>The centered single-card view used by the initial and choice scans.</summary>
	public static readonly RivenRect SingleCard = new(0.22, 0.43, 0.56, 0.45);

	/// <summary>A riven linked in chat sits higher up the screen.</summary>
	public static readonly RivenRect ChatCard = new(0.22, 0.30, 0.56, 0.45);

	/// <summary>The right-hand card during the post-roll transition.</summary>
	public static readonly RivenRect RollCard = new(0.36, 0.416, 0.28, 0.434);

	/// <summary>
	/// Where the stat block sits inside the card crop, as fractions of the crop's height.
	/// The reroll screen's card sits lower relative to its crop than the chat dialog's, so the
	/// default bottom edge clipped its last stat line.
	/// </summary>
	public static readonly RivenStatBand RerollBand = new(0.34, 0.91);
	public static readonly RivenStatBand ChatBand = new(0.34, 0.84);
	public static readonly RivenStatBand RollBand = new(0.34, 0.91);

	/// <summary>The "FITS IN" plate that names the weapon.</summary>
	public static readonly RivenRect FitsInName = new(0.78, 0.72, 0.21, 0.22);
	public static readonly RivenRect FitsInPanel = new(0.70, 0.55, 0.30, 0.45);
	public static readonly RivenRect FitsInWide = new(0.58, 0.48, 0.42, 0.47);

	/// <summary>
	/// A chat-linked riven has no "FITS IN" plate: the weapon is the first word of the mod's
	/// own name, printed as the card's title, so that is where the class comes from.
	/// </summary>
	public static readonly RivenRect ChatTitle = new(0.395, 0.345, 0.21, 0.09);

	private const double cardAspectRatio = 287.0 / 433.0;
	private const double maxAspectOverflow = 1.1;
	private const double trimWidthMargin = 1.25;
	private const double maxCenterShift = 0.2;

	// Horizontal extent of the stat block within the card.
	private const double statLeft = 0.08;
	private const double statRight = 0.92;

	private const int minCropSize = 30;

	/// <summary>Both readers were calibrated around a 320px-tall stat block.</summary>
	private const int reliableStatCropHeight = 320;
	private const int maxUpscale = 3;

	public static Mat CropRatio(Mat frame, RivenRect rect) => frame[ToPixels(frame, rect)].Clone();

	/// <summary>Resolves a fractional rect against a frame, clamped inside it.</summary>
	public static Rect ToPixels(Mat frame, RivenRect rect)
	{
		int left = Math.Clamp((int)Math.Round(rect.X * frame.Width), 0, Math.Max(0, frame.Width - 1));
		int top = Math.Clamp((int)Math.Round(rect.Y * frame.Height), 0, Math.Max(0, frame.Height - 1));
		int width = Math.Clamp((int)Math.Round(rect.Width * frame.Width), 1, frame.Width - left);
		int height = Math.Clamp((int)Math.Round(rect.Height * frame.Height), 1, frame.Height - top);
		return new Rect(left, top, width, height);
	}

	/// <summary>
	/// The card's own column inside a card crop. The crop is deliberately wider than the card
	/// so the card is found at any UI scale, so this is what "where the card is" actually means:
	/// the crop narrowed to the card's aspect ratio and centred on the detected text column.
	/// </summary>
	/// <param name="followText">
	/// Centre on the detected text rather than on the crop. That is what makes the OCR framing
	/// robust, but it also makes the result depend on the card's contents, so anything that
	/// needs a stable answer across rolls (the overlay anchor) has to pass false.
	/// </param>
	public static Rect CardColumn(Mat cardCrop, bool followText = true)
	{
		var column = CardColumn(cardCrop.Width, cardCrop.Height);
		if (!followText || column.Width >= cardCrop.Width) return column;

		int centre = cardCrop.Width / 2;
		var metrics = Measure(cardCrop, 0.2);
		if (metrics.Found && metrics.Width <= column.Width) {
			int textCentre = metrics.Left + (metrics.Width / 2);
			if (Math.Abs(textCentre - centre) <= cardCrop.Width * maxCenterShift) centre = textCentre;
		}

		int left = Math.Clamp(centre - (column.Width / 2), 0, Math.Max(0, cardCrop.Width - column.Width));
		return new Rect(left, 0, column.Width, column.Height);
	}

	/// <summary>
	/// Purely geometric form: the card column implied by a crop's size, centred in it. Needs no
	/// pixels, so the overlay anchor can be computed without reading the frame.
	/// </summary>
	public static Rect CardColumn(int cropWidth, int cropHeight)
	{
		var whole = new Rect(0, 0, cropWidth, cropHeight);
		if (cropWidth < 50 || cropHeight < 50) return whole;

		int expectedWidth = (int)Math.Floor(cropHeight * cardAspectRatio);
		if (expectedWidth <= 0 || cropWidth <= expectedWidth * maxAspectOverflow) return whole;

		int keepWidth = Math.Min(cropWidth, (int)Math.Floor(expectedWidth * trimWidthMargin));
		int left = Math.Clamp((cropWidth / 2) - (keepWidth / 2), 0, Math.Max(0, cropWidth - keepWidth));
		return new Rect(left, 0, keepWidth, cropHeight);
	}

	/// <summary>
	/// Rebuilds the card rect from a stat-block rect. A configured region covers the stat lines
	/// only, while the overlay has to sit beside the card, so the insets the automatic path uses
	/// to go from card to stat block are applied in reverse.
	/// </summary>
	public static Rect CardFromStatArea(Rect statArea, RivenStatBand band)
	{
		double widthFraction = statRight - statLeft;
		double heightFraction = band.Bottom - band.Top;
		if (widthFraction <= 0 || heightFraction <= 0) return statArea;

		int width = (int)Math.Round(statArea.Width / widthFraction);
		int height = (int)Math.Round(statArea.Height / heightFraction);
		return new Rect(
			statArea.X - (int)Math.Round(width * statLeft),
			statArea.Y - (int)Math.Round(height * band.Top),
			width,
			height);
	}

	/// <summary>
	/// Narrows a wide card crop to the card's own aspect ratio, centred on the detected text
	/// column, then takes the stat band out of it.
	/// </summary>
	public static Mat CropStatArea(Mat cardCrop, RivenStatBand band)
	{
		if (cardCrop.Width < 50 || cardCrop.Height < 50) return cardCrop.Clone();

		var trimmed = cardCrop[CardColumn(cardCrop)].Clone();

		try {
			int top = (int)Math.Round(trimmed.Height * band.Top);
			int bottom = (int)Math.Round(trimmed.Height * band.Bottom);
			int left = (int)Math.Round(trimmed.Width * statLeft);
			int right = (int)Math.Round(trimmed.Width * statRight);

			top = Math.Clamp(top, 0, Math.Max(0, trimmed.Height - 1));
			bottom = Math.Clamp(bottom, top + 1, trimmed.Height);
			left = Math.Clamp(left, 0, Math.Max(0, trimmed.Width - 1));
			right = Math.Clamp(right, left + 1, trimmed.Width);

			int width = right - left;
			int height = bottom - top;
			if (width < minCropSize || height < minCropSize) return trimmed.Clone();

			return trimmed[new Rect(left, top, width, height)].Clone();
		} finally {
			trimmed.Dispose();
		}
	}

	/// <summary>Frames the stat band on the detected text bounds instead of fixed fractions.</summary>
	public static Mat? CropStatAreaFallback(Mat cardCrop)
	{
		var metrics = Measure(cardCrop, 0.08);
		if (!metrics.Found) return null;

		int padX = Math.Max(2, (int)Math.Round(metrics.Width * 0.06));
		int padY = Math.Max(2, (int)Math.Round(metrics.Height * 0.06));

		int left = Math.Max(0, metrics.Left - padX);
		int top = Math.Max(0, metrics.Top - padY);
		int right = Math.Min(cardCrop.Width, metrics.Left + metrics.Width + padX);
		int bottom = Math.Min(cardCrop.Height, metrics.Top + metrics.Height + padY);

		int width = right - left;
		int height = bottom - top;
		if (width < minCropSize || height < minCropSize) return null;

		return cardCrop[new Rect(left, top, width, height)].Clone();
	}

	public static int UpscaleFactor(int height)
	{
		if (height <= 0) return 1;
		return Math.Clamp((int)Math.Ceiling(reliableStatCropHeight / (double)height), 1, maxUpscale);
	}

	public static Mat Upscale(Mat source)
	{
		int factor = UpscaleFactor(source.Height);
		if (factor <= 1) return source.Clone();

		var scaled = new Mat();
		Cv2.Resize(source, scaled, new Size(source.Width * factor, source.Height * factor), 0, 0, InterpolationFlags.Lanczos4);
		return scaled;
	}

	public readonly record struct TextMetrics(bool Found, int Left, int Top, int Width, int Height, double Coverage);

	/// <summary>
	/// Locates the text column by sampling for bright, low-saturation pixels. Riven card text is
	/// near-white on a coloured backdrop, so saturation separates it from the art behind it.
	/// </summary>
	public static TextMetrics Measure(Mat image, double minBoundsWidthFraction)
	{
		if (image.Empty() || image.Width < 8 || image.Height < 8) return default;

		int sampleCols = Math.Max(48, Math.Min(image.Width, 320));
		int sampleRows = Math.Max(32, Math.Min(image.Height, 160));
		double stepX = image.Width / (double)sampleCols;
		double stepY = image.Height / (double)sampleRows;

		var rowHits = new int[sampleRows];
		var colHits = new int[sampleCols];
		int total = 0;

		for (int ry = 0; ry < sampleRows; ry++) {
			int y = Math.Min(image.Height - 1, (int)(ry * stepY));
			for (int rx = 0; rx < sampleCols; rx++) {
				int x = Math.Min(image.Width - 1, (int)(rx * stepX));
				var pixel = image.At<Vec3b>(y, x);
				int max = Math.Max(pixel.Item2, Math.Max(pixel.Item1, pixel.Item0));
				int min = Math.Min(pixel.Item2, Math.Min(pixel.Item1, pixel.Item0));
				double saturation = max == 0 ? 0 : (max - min) / (double)max;

				if (max < 146 || saturation > 0.42) continue;
				rowHits[ry]++;
				colHits[rx]++;
				total++;
			}
		}

		if (total == 0) return default;

		var rowRange = ActiveRange(rowHits);
		var colRange = ActiveRange(colHits);
		if (rowRange.Count == 0 || colRange.Count == 0) return default;

		int padX = Math.Max(2, (int)(sampleCols * 0.04));
		int padY = Math.Max(2, (int)(sampleRows * 0.03));

		int left = (int)(Math.Max(0, colRange.First - padX) * stepX);
		int top = (int)(Math.Max(0, rowRange.First - padY) * stepY);
		int right = (int)(Math.Min(sampleCols - 1, colRange.Last + padX) * stepX) + 1;
		int bottom = (int)(Math.Min(sampleRows - 1, rowRange.Last + padY) * stepY) + 1;

		int width = right - left;
		int height = bottom - top;
		if (width < Math.Max(24, image.Width * minBoundsWidthFraction)) return default;
		if (height < Math.Max(24, image.Height * 0.12)) return default;

		return new TextMetrics(true, left, top, width, height, total / (double)(sampleRows * sampleCols));
	}

	/// <summary>Finds the span of rows/columns whose hit count clears mean + 0.45 sigma.</summary>
	private static (int First, int Last, int Count) ActiveRange(int[] hits)
	{
		var smoothed = new double[hits.Length];
		for (int i = 0; i < hits.Length; i++) {
			double sum = hits[i];
			int n = 1;
			if (i > 0) { sum += hits[i - 1]; n++; }
			if (i < hits.Length - 1) { sum += hits[i + 1]; n++; }
			smoothed[i] = sum / n;
		}

		double mean = 0;
		foreach (var v in smoothed) mean += v;
		mean /= smoothed.Length;

		double variance = 0;
		foreach (var v in smoothed) variance += (v - mean) * (v - mean);
		double sigma = Math.Sqrt(variance / smoothed.Length);
		double threshold = Math.Max(2, mean + (sigma * 0.45));

		int first = -1, last = -1, count = 0;
		for (int i = 0; i < smoothed.Length; i++) {
			if (smoothed[i] < threshold) continue;
			if (first < 0) first = i;
			last = i;
			count++;
		}

		return first < 0 ? (0, 0, 0) : (first, last, count);
	}
}

public readonly record struct RivenRect(double X, double Y, double Width, double Height);

/// <summary>Vertical extent of the stat block inside a card crop, as height fractions.</summary>
public readonly record struct RivenStatBand(double Top, double Bottom);
