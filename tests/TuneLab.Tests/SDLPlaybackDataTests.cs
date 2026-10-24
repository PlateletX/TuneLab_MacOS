using TuneLab.Audio.SDL2;
using Xunit;

namespace TuneLab.Tests;

[Collection("SDL audio")]
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

    [Fact]
    public void FailedDeviceSwitchKeepsTheActiveDeviceValid()
    {
        SDLHost.InitHost();
        var playback = new SDLPlaybackData();
        playback.SetDriver("dummy");

        playback.SetDevice(string.Empty);

        var activeId = playback.devId;
        var activeName = playback.devName;
        Assert.NotEqual(0u, activeId);

        Assert.Throws<System.IO.IOException>(() => playback.SetDevice("missing-audio-device"));

        Assert.Equal(activeId, playback.devId);
        Assert.Equal(activeName, playback.devName);
        playback.SetDevice(SDLGlobal.PLAYBACK_EMPTY_DEVICE);
    }

    [Fact]
    public void FailedDriverSwitchDoesNotLeaveStaleDriverState()
    {
        SDLHost.InitHost();
        var playback = new SDLPlaybackData();
        playback.SetDriver("dummy");

        Assert.Throws<System.IO.IOException>(() => playback.SetDriver("missing-audio-driver"));

        Assert.Equal(string.Empty, playback.driver);
        Assert.Equal(0u, playback.devId);
        Assert.Equal(SDLGlobal.PLAYBACK_EMPTY_DEVICE, playback.devName);
    }
}

[CollectionDefinition("SDL audio", DisableParallelization = true)]
public sealed class SdlAudioCollection { }
