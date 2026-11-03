using System;

namespace Clicky.Audio;

/// <summary>
/// Pure-managed helpers for converting WASAPI capture data into the
/// 16 kHz mono PCM16 little-endian format the AssemblyAI realtime
/// websocket expects. This is what BuddyDictationManager.swift produces
/// on Mac via AVAudioEngine's tap + converter.
///
/// The implementation is intentionally simple (linear interpolation) so
/// it is trivially unit-testable. Audio quality is fine for speech —
/// AssemblyAI does the heavy lifting on their end.
/// </summary>
public static class PcmResampler
{
    /// <summary>
    /// Linear interpolation resample of a mono float buffer from
    /// <paramref name="inputSampleRate"/> to <paramref name="outputSampleRate"/>.
    /// Sample count is <c>input.Length * outputSampleRate / inputSampleRate</c>.
    /// </summary>
    public static float[] ResampleLinearMono(
        ReadOnlySpan<float> input,
        int inputSampleRate,
        int outputSampleRate)
    {
        if (inputSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputSampleRate));
        if (outputSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
        if (input.IsEmpty) return Array.Empty<float>();
        if (inputSampleRate == outputSampleRate) return input.ToArray();

        long outputLength = (long)input.Length * outputSampleRate / inputSampleRate;
        if (outputLength <= 0) return Array.Empty<float>();

        var output = new float[outputLength];
        double step = (double)inputSampleRate / outputSampleRate;
        int lastIndex = input.Length - 1;

        for (int i = 0; i < output.Length; i++)
        {
            double srcIndex = i * step;
            int i0 = (int)Math.Floor(srcIndex);
            if (i0 < 0) i0 = 0;
            if (i0 > lastIndex) i0 = lastIndex;
            int i1 = i0 + 1;
            if (i1 > lastIndex) i1 = lastIndex;
            double frac = srcIndex - i0;
            output[i] = (float)((1.0 - frac) * input[i0] + frac * input[i1]);
        }

        return output;
    }

    /// <summary>
    /// Downmix an interleaved multi-channel float buffer to mono by averaging
    /// across channels. Returns the input unchanged when <paramref name="channels"/> is 1.
    /// </summary>
    public static float[] DownmixToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (channels == 1) return interleaved.ToArray();

        int frameCount = interleaved.Length / channels;
        var mono = new float[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            float sum = 0f;
            int baseIndex = i * channels;
            for (int c = 0; c < channels; c++)
            {
                sum += interleaved[baseIndex + c];
            }
            mono[i] = sum / channels;
        }
        return mono;
    }

    /// <summary>
    /// Convert mono float samples in the range [-1, 1] to signed 16-bit
    /// little-endian PCM bytes. Values outside the range are clipped.
    /// </summary>
    public static byte[] FloatToPcm16LE(ReadOnlySpan<float> samples)
    {
        var output = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float clamped = samples[i];
            if (clamped > 1f) clamped = 1f;
            else if (clamped < -1f) clamped = -1f;
            short pcm = (short)(clamped * 32767f);
            output[i * 2] = (byte)(pcm & 0xFF);
            output[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }
        return output;
    }
}
