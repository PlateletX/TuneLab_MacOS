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
        var originalDriver = Environment.GetEnvironmentVariable("SDL_AUDIODRIVER");
        Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");

        try
        {
            SDLHost.InitHost();
            var playback = new SDLPlaybackData();
            playback.SetDevice(string.Empty);

            var activeId = playback.devId;
            var activeName = playback.devName;
            Assert.NotEqual(0u, activeId);

            Assert.Throws<System.IO.IOException>(() => playback.SetDevice("missing-audio-device"));

            Assert.Equal(activeId, playback.devId);
            Assert.Equal(activeName, playback.devName);
            playback.SetDevice(SDLGlobal.PLAYBACK_EMPTY_DEVICE);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", originalDriver);
        }
    }
}

[CollectionDefinition("SDL audio", DisableParallelization = true)]
public sealed class SdlAudioCollection { }
