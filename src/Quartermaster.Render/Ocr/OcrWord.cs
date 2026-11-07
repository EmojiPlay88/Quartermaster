namespace Quartermaster.Render.Ocr;

/// <summary>One recognised word with its bounding box in image pixels (origin top-left).</summary>
public readonly record struct OcrWord(string Text, double X, double Y, double W, double H);
