using TuneLab.Audio.SDL2;
using Xunit;

namespace TuneLab.Tests;

public class SDLPlaybackDataTests
{
    [Fact]
    public void StartWithoutAudioDeviceRemainsStopped()
    {
        var playback = new SDLPlaybackData();

        playback.Start();

        Assert.Equal(SDLPlaybackData.PlaybackState.Stopped, playback.state);
    }

    [Fact]
    public void StopWithoutProducerIsSafe()
    {
        var playback = new SDLPlaybackData();

        playback.Stop();

        Assert.Equal(SDLPlaybackData.PlaybackState.Stopped, playback.state);
    }
}
