using System;
using Clicky.Audio;
using Xunit;

namespace Clicky.Tests;

public class PcmResamplerTests
{
    [Fact]
    public void ResampleLinearMono_48kTo16k_ProducesOneThirdSampleCount()
    {
        // 100 ms of audio at 48 kHz = 4800 samples should resample to
        // 1600 samples at 16 kHz (= 100 ms at the target rate).
        var input = new float[4800];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (float)Math.Sin(2 * Math.PI * 440 * i / 48_000.0);
        }

        var output = PcmResampler.ResampleLinearMono(input, 48_000, 16_000);

        Assert.Equal(1600, output.Length);
    }

    [Fact]
    public void ResampleLinearMono_SameRate_ReturnsCopyOfInput()
    {
        var input = new float[] { 0.1f, 0.2f, 0.3f };
        var output = PcmResampler.ResampleLinearMono(input, 16_000, 16_000);

        Assert.Equal(input.Length, output.Length);
        Assert.Equal(input, output);
    }

    [Fact]
    public void DownmixToMono_AveragesStereoChannels()
    {
        var stereo = new float[] { 0.5f, -0.5f, 1.0f, 0.0f };
        var mono = PcmResampler.DownmixToMono(stereo, 2);

        Assert.Equal(2, mono.Length);
        Assert.Equal(0f, mono[0], 5);
        Assert.Equal(0.5f, mono[1], 5);
    }

    [Fact]
    public void FloatToPcm16LE_EncodesLittleEndianSignedInt16()
    {
        // 1.0 → 32767 → 0xFF 0x7F
        // 0.0 → 0 → 0x00 0x00
        // -1.0 → -32767 → 0x01 0x80
        var samples = new float[] { 1.0f, 0.0f, -1.0f };
        var pcm = PcmResampler.FloatToPcm16LE(samples);

        Assert.Equal(6, pcm.Length);
        Assert.Equal(0xFF, pcm[0]);
        Assert.Equal(0x7F, pcm[1]);
        Assert.Equal(0x00, pcm[2]);
        Assert.Equal(0x00, pcm[3]);
        Assert.Equal(0x01, pcm[4]);
        Assert.Equal(0x80, pcm[5]);
    }

    [Fact]
    public void FloatToPcm16LE_ClipsOutOfRangeSamples()
    {
        var samples = new float[] { 2.0f, -2.0f };
        var pcm = PcmResampler.FloatToPcm16LE(samples);

        // 2.0 clips to 1.0 → 32767
        Assert.Equal(0xFF, pcm[0]);
        Assert.Equal(0x7F, pcm[1]);
        // -2.0 clips to -1.0 → -32767
        Assert.Equal(0x01, pcm[2]);
        Assert.Equal(0x80, pcm[3]);
    }
}
