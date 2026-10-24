using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SDL2;
using NAudio.Wave;

namespace TuneLab.Audio.SDL2;

internal class SDLPlaybackData
{
    public enum PlaybackState
    {
        Stopped,
        Playing,
        Paused,
    }

    // 事件委托
    public SDLGlobal.ValueChangeEvent<string>? onDevChanged;

    public SDLGlobal.ValueChangeEvent<PlaybackState>? onStateChanged;

    public SDLGlobal.ValueEvent<int>? onSamplesConsumed;

    public SDLGlobal.VoidEvent? onDevicesUpdated;

    public delegate int BufferEndEvent(float[] buffer, int offset, int count);

    public BufferEndEvent onBufferEnd;

    // 播放信息
    public string driver = string.Empty;

    public PlaybackState state = PlaybackState.Stopped;

    // 当前播放设备
    // (a) 0：无设备
    // (b) 非零：递增的设备 ID
    public uint devId;

    // 当前播放设备名
    // (a) 空：自动
    // (b) SDLGlobal.PLAYBACK_EMPTY_DEVICE：无设备
    // (c) 其他：指定的其他设备
    public string devName = SDLGlobal.PLAYBACK_EMPTY_DEVICE;

    public SDL.SDL_AudioSpec spec;

    // 线程相关
    private Thread producer;

    private Mutex mutex;

    // 控制块
    private struct CallbackBlock
    {
        public IntPtr audio_chunk;
        public int audio_len;
        public int audio_pos;
    }

    private CallbackBlock scb;

    private float[] pcm_buffer;

    // 构造函数
    public SDLPlaybackData()
    {
        // 默认音频配置
        spec.samples = 1024;
        spec.freq = 44100;
        spec.channels = 2;

        spec.userdata = IntPtr.Zero; // 不使用
        spec.callback = AudioSpecCallback;
        spec.format = SDLGlobal.PLAYBACK_FORMAT;
        spec.silence = 0;

        pcm_buffer = null;
        mutex = new Mutex();
    }

    public void Start()
    {
        // A device can disappear between the settings check and the start call.
        // Do not create a producer with a null buffer or an invalid SDL device.
        if (devId == 0 || pcm_buffer == null || pcm_buffer.Length == 0)
        {
            SetState(PlaybackState.Stopped);
            return;
        }

        if (producer != null && producer.IsAlive)
        {
            return;
        }

        SetState(PlaybackState.Playing);

        // 初始化控制块
        scb.audio_chunk = IntPtr.Zero;
        scb.audio_len = 0;
        scb.audio_pos = 0;

        // 启动生产者线程
        producer = new Thread(AudioLoop);
        producer.Start();
    }

    public void Stop()
    {
        var currentProducer = producer;
        if (currentProducer == null)
        {
            SetState(PlaybackState.Stopped);
            return;
        }

        // 结束生产者线程
        NotifyStop();

        // 等待结束
        if (!ReferenceEquals(Thread.CurrentThread, currentProducer))
        {
            currentProducer.Join();
        }

        if (ReferenceEquals(producer, currentProducer))
        {
            producer = null;
        }

        SetState(PlaybackState.Stopped);
    }

    public void SetDriver(string drv)
    {
        if (!string.IsNullOrEmpty(driver))
        {
            if (devId > 0)
            {
                // 停止播放
                if (state == PlaybackState.Playing)
                {
                    Stop();
                }

                SDL.SDL_CloseAudioDevice(devId); // 关闭音频设备

                devId = 0;
                devName = SDLGlobal.PLAYBACK_EMPTY_DEVICE;
            }

            // 关闭上一个驱动
            SDL.SDL_AudioQuit();
            driver = string.Empty;
        }

        if (!string.IsNullOrEmpty(drv))
        {
            // 打开当前驱动
            if (SDL.SDL_AudioInit(drv) < 0)
            {
                throw new IOException($"SDL: failed to open audio driver: {SDL.SDL_GetError()}");
            }

            // 重新检测音频设备
            _ = SDL.SDL_GetNumAudioDevices(0);
            onDevicesUpdated?.Invoke();
        }

        driver = drv;
    }

    public void SetDevice(string dev)
    {
        var orgId = devId;
        var orgName = devName;

        if (dev == orgName)
        {
            return;
        }

        uint newId = 0;
        if (dev != SDLGlobal.PLAYBACK_EMPTY_DEVICE)
        {
            // Open first so a transient device failure does not invalidate the active device.
            if ((newId = SDL.SDL_OpenAudioDevice(
                    string.IsNullOrEmpty(dev) ? null : dev,
                    0,
                    ref spec,
                    out _,
                    0)) == 0)
            {
                throw new IOException($"SDL: failed to open audio device \"{dev}\": {SDL.SDL_GetError()}");
            }
        }

        if (orgId > 0)
        {
            // 停止播放
            if (state == PlaybackState.Playing)
            {
                Stop();
            }

            // 关闭上一个设备
            SDL.SDL_CloseAudioDevice(orgId);
        }

        devId = newId;
        devName = dev;

        if (newId > 0)
        {
            // 初始化临时浮点数组
            var cnt = spec.samples * spec.channels;
            if (cnt == 0)
            {
                pcm_buffer = null;
            }
            else if (pcm_buffer == null || cnt != pcm_buffer.Length)
            {
                pcm_buffer = new float[cnt];
            }
        }
        // 通知音频设备已更改
        if (orgName != devName)
        {
            onDevChanged?.Invoke(devName, orgName);
        }
    }

    public void SetState(PlaybackState newState)
    {
        var orgState = state;
        state = newState;

        // 通知播放状态已更改
        if (state != orgState)
        {
            onStateChanged?.Invoke(state, orgState);
        }
    }

    // 消费者
    private void AudioSpecCallback(IntPtr udata, IntPtr stream, int len)
    {
        // 上锁
        mutex.WaitOne();

        // 缓冲区置为静音
        SDLReimpl.SDL_memset(stream, 0, (IntPtr)len);

        if (scb.audio_len > 0 && scb.audio_chunk != IntPtr.Zero)
        {
            len = Math.Min(len, scb.audio_len);

            // 将缓冲区中的声音写入流
            SDL.SDL_MixAudioFormat(
                stream,
                IntPtr.Add(scb.audio_chunk, scb.audio_pos),
                spec.format,
                (uint)len,
                SDL.SDL_MIX_MAXVOLUME
            );

            scb.audio_pos += len;
            scb.audio_len -= len;

            onSamplesConsumed?.Invoke(len / sizeof(float));

            // 判断是否完毕
            if (scb.audio_len == 0)
            {
                NotifyAudioBufferEnd();
            }
        }

        // 放锁
        mutex.ReleaseMutex();
    }

    // 通知缓冲区已空
    private static void NotifyAudioBufferEnd()
    {
        var e = new SDL.SDL_Event();
        e.type = (SDL.SDL_EventType)SDLGlobal.UserEvent.SDL_EVENT_BUFFER_END;
        SDL.SDL_PushEvent(ref e);
    }

    // 通知暂停
    private static void NotifyStop()
    {
        var e = new SDL.SDL_Event();
        e.type = (SDL.SDL_EventType)SDLGlobal.UserEvent.SDL_EVENT_MANUAL_STOP;
        SDL.SDL_PushEvent(ref e);
    }

    // 生产者
    private void AudioLoop()
    {
        if (pcm_buffer == null || pcm_buffer.Length == 0 || devId == 0)
        {
            if (ReferenceEquals(producer, Thread.CurrentThread))
                producer = null;
            SetState(PlaybackState.Stopped);
            return;
        }

        // 固定缓冲区
        var gch = GCHandle.Alloc(pcm_buffer, GCHandleType.Pinned);

        // 设置暂停标识位
        SDL.SDL_PauseAudioDevice(devId, 0);

        // 第一次事件
        NotifyAudioBufferEnd();

        // 外层循环
        bool devicesChanged = false;
        bool deviceRemoved = false;
        try
        {
            while (true)
            {
                bool over = false;

                // 不停地获取事件
                while (SDL.SDL_PollEvent(out var e) > 0)
                {
                    switch ((int)e.type)
                    {
                        // 缓存用完
                        case (int)SDLGlobal.UserEvent.SDL_EVENT_BUFFER_END:
                        {
                            // 上锁
                            mutex.WaitOne();
                            try
                            {
                                // 从文件中读取数据，剩下的就交给音频设备去完成了
                                // 它播放完一段数据后会执行回调函数，获取等多的数据
                                int samples = onBufferEnd(pcm_buffer, 0, spec.samples * spec.channels);
                                if (samples <= 0)
                                {
                                    // 播放完毕
                                    over = true;
                                }
                                else
                                {
                                    // 重置缓冲区
                                    scb.audio_chunk = Marshal.UnsafeAddrOfPinnedArrayElement(pcm_buffer, 0);
                                    scb.audio_len = samples * sizeof(float); // 长度为读出数据长度，在read_audio_data中做减法
                                    scb.audio_pos = 0; // 设置当前位置为缓冲区头部
                                }
                            }
                            finally
                            {
                                mutex.ReleaseMutex();
                            }
                            break;
                        }

                        // 停止命令
                        case (int)SDLGlobal.UserEvent.SDL_EVENT_MANUAL_STOP:
                        {
                            over = true;
                            break;
                        }

                        // 音频设备插入
                        case (int)SDL.SDL_EventType.SDL_AUDIODEVICEADDED:
                        {
                            devicesChanged = true;
                            break;
                        }

                        // 音频设备移除
                        case (int)SDL.SDL_EventType.SDL_AUDIODEVICEREMOVED:
                        {
                            var dev = e.adevice.which;
                            if (dev == devId)
                            {
                                // 这里运行在 producer 自己的线程，不能调用 SetDevice/Stop：
                                // Stop 会 Join 当前线程并永久死锁。把关闭动作延后到 finally。
                                deviceRemoved = true;
                                over = true;
                            }
                            devicesChanged = true;
                            break;
                        }
                    }
                }

                if (over)
                    break;

                if (devicesChanged)
                {
                    devicesChanged = false;
                    onDevicesUpdated?.Invoke();
                }

                SDL.SDL_Delay(SDLGlobal.PLAYBACK_POLL_INTERVAL);
            }
        }
        finally
        {
            string? removedDeviceName = null;

            // 设置暂停标识位
            if (devId > 0)
                SDL.SDL_PauseAudioDevice(devId, 1);

            if (deviceRemoved && devId > 0)
            {
                removedDeviceName = devName;
                SDL.SDL_CloseAudioDevice(devId);
                devId = 0;
                devName = SDLGlobal.PLAYBACK_EMPTY_DEVICE;
            }

            // 解除固定缓冲区
            gch.Free();
            if (ReferenceEquals(producer, Thread.CurrentThread))
                producer = null;
            SetState(PlaybackState.Stopped);

            // 所有线程状态都已清理后再通知 UI；否则 UI 可能在旧 producer 仍存活时尝试重启，
            // 被 Start 的并发保护误判成“已经在播放”。
            if (removedDeviceName != null && removedDeviceName != devName)
                onDevChanged?.Invoke(devName, removedDeviceName);
            if (deviceRemoved && devicesChanged)
                onDevicesUpdated?.Invoke();
        }
    }
}
