using PGAssetTool.Core.Import.Audio;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Core.Tests;

public class PreviewSoundTests
{
    private static PreviewSound Clip(int frames, int channels = 1, int frequency = 31000)
    {
        var samples = new short[frames * channels];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(i % 2 == 0 ? 10000 : -10000);
        return new PreviewSound(new PcmSound(samples, channels, frequency));
    }

    [Fact]
    public void EachColumnKeepsTheExtremesOfEverythingUnderIt()
    {
        // Taking one sample per column instead would alias a shot into whatever the sampling
        // happened to land on, and draw a waveform that is not the sound's shape.
        var envelope = Clip(10_000).Envelope(100);

        Assert.Equal(100, envelope.Length);
        Assert.All(envelope, c =>
        {
            Assert.InRange(c.High, 0.30f, 0.31f);
            Assert.InRange(c.Low, -0.31f, -0.30f);
        });
    }

    [Fact]
    public void AColumnAskedForMoreThanThereIsStillGetsOne()
    {
        // A very short clip in a wide pane: more columns than frames, and every one has to answer.
        var envelope = Clip(3).Envelope(64);

        Assert.Equal(64, envelope.Length);
        Assert.Contains(envelope, c => c.High > 0);
    }

    [Fact]
    public void SilenceDrawsFlatRatherThanThrowing()
    {
        var envelope = new PreviewSound(new PcmSound([], 1, 44100)).Envelope(16);

        Assert.Equal(16, envelope.Length);
        Assert.All(envelope, c => Assert.Equal((0f, 0f), c));
    }

    [Fact]
    public void BothSidesOfAStereoClipCountTowardsTheSameColumn()
    {
        // The two channels are the same event; drawing them apart would say something about the mix
        // rather than about the sound.
        var sound = new PreviewSound(new PcmSound([0, 20000, 0, -20000], 2, 44100));
        var envelope = sound.Envelope(1);

        Assert.InRange(envelope[0].High, 0.60f, 0.62f);
        Assert.InRange(envelope[0].Low, -0.62f, -0.60f);
    }

    [Fact]
    public void TheWaveHandedToThePlayerReadsBackAsTheSameSound()
    {
        // What the player is given has to be a wave anything will take. One that declares itself
        // empty reads perfectly well and simply does not play.
        var clip = Clip(1000, channels: 2, frequency: 48000);

        var again = WaveFile.Parse(clip.ToWave(), "the preview");

        Assert.Equal(clip.Sound.Samples, again.Samples);
        Assert.Equal(2, again.Channels);
        Assert.Equal(48000, again.Frequency);
        Assert.Equal(1000, again.Frames);
    }
}
