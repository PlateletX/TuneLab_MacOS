using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TuneLab.Audio.NAudio;

internal static class MacOSMp3Encoder
{
    private const string Library = "libmp3lame";
    private const int SamplesPerBlock = 8192;

    public static void Encode(string path, float[] buffer, int sampleRate, int channelCount, int bitrate)
    {
        if (channelCount is not (1 or 2) || buffer.Length % channelCount != 0)
            throw new ArgumentException("MP3 encoding requires mono or stereo interleaved samples.", nameof(buffer));

        IntPtr encoder = LameInit();
        if (encoder == IntPtr.Zero)
            throw new IOException("Could not initialize the LAME MP3 encoder.");

        try
        {
            Check(LameSetNumChannels(encoder, channelCount), "channel count");
            Check(LameSetInputSampleRate(encoder, sampleRate), "sample rate");
            Check(LameSetBitrate(encoder, bitrate), "bitrate");
            Check(LameSetWriteVbrTag(encoder, 0), "VBR tag");
            Check(LameInitParams(encoder), "encoder parameters");

            using var output = File.Create(path);
            var left = new float[SamplesPerBlock];
            var right = new float[SamplesPerBlock];
            var encoded = new byte[(int)(SamplesPerBlock * 1.25) + 7200];
            int sampleCount = buffer.Length / channelCount;

            for (int offset = 0; offset < sampleCount; offset += SamplesPerBlock)
            {
                int count = Math.Min(SamplesPerBlock, sampleCount - offset);
                for (int i = 0; i < count; i++)
                {
                    left[i] = Math.Clamp(buffer[(offset + i) * channelCount], -1f, 1f);
                    if (channelCount == 2)
                        right[i] = Math.Clamp(buffer[(offset + i) * channelCount + 1], -1f, 1f);
                }

                int bytes = LameEncodeBuffer(encoder, left, channelCount == 2 ? right : left,
                    count, encoded, encoded.Length);
                Check(bytes, "audio block");
                output.Write(encoded, 0, bytes);
            }

            int flushed = LameEncodeFlush(encoder, encoded, encoded.Length);
            Check(flushed, "final audio block");
            output.Write(encoded, 0, flushed);
        }
        finally
        {
            LameClose(encoder);
        }
    }

    private static void Check(int result, string operation)
    {
        if (result < 0)
            throw new IOException($"LAME MP3 encoding failed while setting or writing {operation} (error {result}).");
    }

    [DllImport(Library, EntryPoint = "lame_init")]
    private static extern IntPtr LameInit();

    [DllImport(Library, EntryPoint = "lame_set_num_channels")]
    private static extern int LameSetNumChannels(IntPtr encoder, int channels);

    [DllImport(Library, EntryPoint = "lame_set_in_samplerate")]
    private static extern int LameSetInputSampleRate(IntPtr encoder, int sampleRate);

    [DllImport(Library, EntryPoint = "lame_set_brate")]
    private static extern int LameSetBitrate(IntPtr encoder, int bitrate);

    [DllImport(Library, EntryPoint = "lame_set_bWriteVbrTag")]
    private static extern int LameSetWriteVbrTag(IntPtr encoder, int enabled);

    [DllImport(Library, EntryPoint = "lame_init_params")]
    private static extern int LameInitParams(IntPtr encoder);

    [DllImport(Library, EntryPoint = "lame_encode_buffer_ieee_float")]
    private static extern int LameEncodeBuffer(IntPtr encoder, float[] left, float[] right,
        int samples, byte[] output, int outputSize);

    [DllImport(Library, EntryPoint = "lame_encode_flush")]
    private static extern int LameEncodeFlush(IntPtr encoder, byte[] output, int outputSize);

    [DllImport(Library, EntryPoint = "lame_close")]
    private static extern int LameClose(IntPtr encoder);
}
