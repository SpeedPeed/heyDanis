using System.Drawing;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace HeyDanis.Capture;

/// <summary>One recognized word and its exact position in the screenshot (screenshot pixels).</summary>
public sealed record RecognizedWord(string Text, Rectangle Bounds, int LineIndex, int WordIndexInLine);

/// <summary>
/// Windows' built-in OCR (offline, free). Gives the exact position of every word on a
/// screenshot, so the pointer/circles can snap onto the word Claude names instead of
/// relying on Claude's approximate coordinates.
/// </summary>
public static class ScreenTextRecognizer
{
    private static readonly Lazy<OcrEngine?> Engine = new(CreateEngine);

    /// <summary>False if no OCR language is installed (then snapping is simply skipped).</summary>
    public static bool IsAvailable => Engine.Value is not null;

    private static OcrEngine? CreateEngine()
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.TryCreateFromLanguage(new Language("de-DE"))
                ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Recognizes all words on a captured screenshot. Never throws; empty on failure.</summary>
    public static async Task<IReadOnlyList<RecognizedWord>> RecognizeWordsAsync(CapturedScreen screen)
    {
        var engine = Engine.Value;
        if (engine is null) return Array.Empty<RecognizedWord>();

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(screen.ImageBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);

            var words = new List<RecognizedWord>();
            for (var lineIndex = 0; lineIndex < result.Lines.Count; lineIndex++)
            {
                var line = result.Lines[lineIndex];
                for (var wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
                {
                    var word = line.Words[wordIndex];
                    var rect = word.BoundingRect;
                    words.Add(new RecognizedWord(
                        word.Text,
                        new Rectangle((int)Math.Round(rect.X), (int)Math.Round(rect.Y), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height)),
                        lineIndex,
                        wordIndex));
                }
            }
            return words;
        }
        catch
        {
            return Array.Empty<RecognizedWord>();
        }
    }
}
