using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace framenion.Src.Ocr;

public readonly record struct OcrTextLine(string Text, Rect Box);

public interface IOcrEngine : IDisposable
{
	string Name { get; }

	Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(Mat image);
}
