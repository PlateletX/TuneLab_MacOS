using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace TuneLab.Extensions.Effect;

// The SDK context intentionally exposes a read-only value view, so it does not
// distinguish an omitted property from a value equal to the engine default.
// TuneLab's built-in effect context supplies this extra host-side fact to avoid
// sending every plugin parameter on every render block.
internal interface IExplicitEffectSynthesisContext
{
    bool HasExplicitProperty(string key);
    bool HasExplicitAutomation(string key);
}

internal sealed class Vst3EffectEngine : IEffectSynthesisEngine
{
    const int MaximumFrames = 512;
    const int AutomationBlockFrames = 64;
    const double MetadataSampleRate = 44100;
    const int CanAutomate = 1 << 0;
    const int IsReadOnly = 1 << 1;
    const int IsHidden = 1 << 4;
    const int IsProgramChange = 1 << 15;

    readonly Component mComponent;
    Parameter[]? mParameters;
    ObjectConfig? mPropertyConfig;
    OrderedMap<PropertyKey, AutomationConfig>? mAutomationConfigs;

    internal Vst3EffectEngine(Component component) => mComponent = component;

    public string Type => mComponent.Type;

    public static IReadOnlyList<Component> Enumerate()
    {
        if (Native.Scan() != 0)
            throw new InvalidOperationException("VST3 plugin scan failed.");

        nuint count = Native.PluginCount();
        var plugins = new List<Component>((int)Math.Min(count, 4096));
        for (nuint i = 0; i < count; i++)
        {
            var info = new Native.PluginInfo
            {
                ClassId = new byte[64],
                Name = new byte[256],
                Vendor = new byte[256],
                Path = new byte[1024],
            };
            if (Native.PluginGet(i, ref info) != 0)
                continue;

            string classId = Decode(info.ClassId);
            string name = Decode(info.Name);
            string vendor = Decode(info.Vendor);
            if (string.IsNullOrWhiteSpace(classId) || string.IsNullOrWhiteSpace(name))
                continue;
            plugins.Add(new Component(classId, name, vendor, Decode(info.Path)));
        }
        return plugins;
    }

    public ObjectConfig GetPropertyConfig(IEffectSynthesisPropertyContext context)
    {
        EnsureMetadata();
        return mPropertyConfig!;
    }

    public IReadOnlyOrderedMap<PropertyKey, AutomationConfig> GetAutomationConfigs(IEffectSynthesisPropertyContext context)
    {
        EnsureMetadata();
        return mAutomationConfigs!;
    }

    public IReadOnlyOrderedMap<PropertyKey, AutomationConfig> GetSynthesizedParameterConfigs(IEffectSynthesisPropertyContext context)
        => EmptyAutomationConfigs;

    public void Init() => EnsureMetadata();
    public void Destroy() { }

    public IEffectSynthesisSession CreateSession(IEffectSynthesisContext context)
    {
        EnsureInitializationSupported();
        EnsureMetadata();
        IntPtr handle = Native.Create(mComponent.ClassId, context.Input.SampleRate, MaximumFrames, out int error);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"Could not create VST3 effect '{mComponent.Name}' (error {error}).");
        return new Session(context, mParameters!, mComponent.Name, handle);
    }

    void EnsureMetadata()
    {
        if (mParameters != null)
            return;

        EnsureInitializationSupported();

        IntPtr handle = Native.Create(mComponent.ClassId, MetadataSampleRate, MaximumFrames, out int error);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"Could not inspect VST3 effect '{mComponent.Name}' (error {error}).");

        try
        {
            nuint count = Native.ParameterCount(handle);
            var parameters = new List<Parameter>((int)Math.Min(count, 4096));
            for (nuint i = 0; i < count; i++)
            {
                var info = new Native.ParameterInfo { Name = new byte[256], Units = new byte[128] };
                if (Native.ParameterGet(handle, i, ref info, out double current) != 0 ||
                    !double.IsFinite(info.DefaultValue) || !double.IsFinite(current) ||
                    (info.Flags & (IsReadOnly | IsHidden | IsProgramChange)) != 0)
                    continue;
                string key = $"vst3_param_{info.Id:x8}";
                string name = Decode(info.Name);
                parameters.Add(new Parameter(info.Id, key,
                    string.IsNullOrWhiteSpace(name) ? key : name,
                    Math.Clamp(info.DefaultValue, 0.0, 1.0), (info.Flags & CanAutomate) != 0));
            }

            var properties = new OrderedMap<PropertyKey, IControllerConfig>();
            var automations = new OrderedMap<PropertyKey, AutomationConfig>();
            foreach (var parameter in parameters)
            {
                properties.Add((parameter.Key, parameter.Name),
                    SliderConfig.Linear(parameter.DefaultValue, 0.0, 1.0));
                if (parameter.CanAutomate)
                {
                    automations.Add((parameter.Key, parameter.Name),
                        AutomationConfig.Create(0.0, 1.0).WithDefault(parameter.DefaultValue));
                }
            }
            mParameters = parameters.ToArray();
            mPropertyConfig = ObjectConfig.Create(properties);
            mAutomationConfigs = automations;
        }
        finally
        {
            Native.Destroy(handle);
        }
    }

    void EnsureInitializationSupported()
    {
        // Current UADx VST3 bundles block in their component initialization when
        // hosted by TuneLab's offline, UI-free VST3 path. Keep them discoverable so
        // users can see installed effects, but fail fast instead of hanging the app.
        if (mComponent.Vendor.Contains("Universal Audio", StringComparison.OrdinalIgnoreCase) ||
            mComponent.Path.Contains("/uaudio_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Universal Audio effect '{mComponent.Name}' was discovered but cannot be initialized by the current macOS VST3 host.");
    }

    static string Decode(byte[]? bytes)
        => bytes == null ? string.Empty : Encoding.UTF8.GetString(bytes).TrimEnd('\0');

    static readonly OrderedMap<PropertyKey, AutomationConfig> EmptyAutomationConfigs = new();

    internal readonly record struct Component(string ClassId, string Name, string Vendor, string Path)
    {
        public string Type => $"vst3-{ClassId}";
    }

    sealed record Parameter(uint Id, string Key, string Name, double DefaultValue, bool CanAutomate);

    sealed class Session : IEffectSynthesisSession
    {
        readonly IEffectSynthesisContext mContext;
        readonly Parameter[] mParameters;
        readonly string mPluginName;
        readonly IntPtr mHandle;
        readonly double[] mValues;
        readonly double[][] mAutomationValues;
        readonly bool[] mParameterActive;
        bool mDisposed;

        public Session(IEffectSynthesisContext context, Parameter[] parameters, string pluginName, IntPtr handle)
        {
            mContext = context;
            mParameters = parameters;
            mPluginName = pluginName;
            mHandle = handle;
            mValues = new double[parameters.Length];
            mAutomationValues = new double[parameters.Length][];
            mParameterActive = new bool[parameters.Length];
        }

        public Task Process(CancellationToken cancellation = default)
        {
            if (mDisposed || cancellation.IsCancellationRequested)
                return Task.CompletedTask;

            var input = mContext.Input;
            int sampleRate = input.SampleRate;
            long sampleOffset = input.SampleOffset;
            int sampleCount = input.SampleCount;
            var source = new float[sampleCount];
            input.Read(0, source);
            var output = new float[sampleCount];

            for (int parameterIndex = 0; parameterIndex < mParameters.Length; parameterIndex++)
            {
                var parameter = mParameters[parameterIndex];
                var explicitContext = mContext as IExplicitEffectSynthesisContext;
                bool hasProperty = explicitContext?.HasExplicitProperty(parameter.Key) ?? true;
                bool hasAutomation = parameter.CanAutomate &&
                    (explicitContext?.HasExplicitAutomation(parameter.Key) ?? true);
                mParameterActive[parameterIndex] = hasProperty || hasAutomation;
                if (!mParameterActive[parameterIndex])
                {
                    mValues[parameterIndex] = parameter.DefaultValue;
                    mAutomationValues[parameterIndex] = Array.Empty<double>();
                    continue;
                }

                PropertyValue raw = mContext.Properties.GetValue(parameter.Key, PropertyValue.Null);
                mValues[parameterIndex] = raw.ToDouble(out double value)
                    ? Math.Clamp(value, 0.0, 1.0)
                    : parameter.DefaultValue;
                var values = new double[(sampleCount + AutomationBlockFrames - 1) / AutomationBlockFrames];
                if (hasAutomation &&
                    mContext.Automations.TryGetValue(parameter.Key, out var automation) && values.Length > 0)
                {
                    var times = new double[values.Length];
                    for (int block = 0; block < times.Length; block++)
                        times[block] = (double)(sampleOffset + block * AutomationBlockFrames) / sampleRate;
                    automation.Evaluate(times, values);
                }
                else
                {
                    Array.Fill(values, mValues[parameterIndex]);
                }
                mAutomationValues[parameterIndex] = values;
            }

            if (sampleCount == 0)
                return Task.CompletedTask;
            return RenderAndCommit(source, output, sampleOffset, sampleRate, cancellation);
        }

        async Task RenderAndCommit(float[] source, float[] output, long sampleOffset, int sampleRate,
            CancellationToken cancellation)
        {
            bool rendered = await Task.Run(() => Render(source, output, cancellation), CancellationToken.None);
            if (!rendered || cancellation.IsCancellationRequested)
                return;

            var segment = mContext.CreateAudioSegment(sampleOffset, output.Length, sampleRate);
            segment.Write(0, output);
            segment.Commit();
        }

        bool Render(float[] source, float[] output, CancellationToken cancellation)
        {
            int resetStatus = Native.Reset(mHandle);
            if (resetStatus != 0)
                throw new InvalidOperationException($"Could not reset VST3 effect '{mPluginName}' (error {resetStatus}).");

            var input = new float[MaximumFrames];
            var rendered = new float[MaximumFrames];
            for (int offset = 0; offset < source.Length; offset += MaximumFrames)
            {
                if (cancellation.IsCancellationRequested)
                    return false;
                int frameCount = Math.Min(MaximumFrames, source.Length - offset);
                Array.Copy(source, offset, input, 0, frameCount);
                for (int blockOffset = 0; blockOffset < frameCount; blockOffset += AutomationBlockFrames)
                {
                    int automationIndex = (offset + blockOffset) / AutomationBlockFrames;
                    for (int i = 0; i < mParameters.Length; i++)
                    {
                        if (!mParameterActive[i])
                            continue;
                        double value = mAutomationValues[i].Length > automationIndex
                            ? mAutomationValues[i][automationIndex]
                            : mValues[i];
                        int status = Native.ParameterSetAt(mHandle, mParameters[i].Id, value, blockOffset);
                        if (status != 0)
                            throw new InvalidOperationException($"Could not set VST3 parameter '{mParameters[i].Name}' (error {status}).");
                    }
                }

                int processStatus = Native.Process(mHandle, input, rendered, frameCount);
                if (processStatus != 0)
                    throw new InvalidOperationException($"VST3 processing failed for '{mPluginName}' (error {processStatus}).");
                Array.Copy(rendered, 0, output, offset, frameCount);
            }
            return true;
        }

        public IReadOnlyMap<string, SynthesizedParameter> SynthesizedParameters => EmptySynthesizedParameters;
        public IActionEvent SynthesizedParametersChanged => ActionEvent.Empty;
        public IReadOnlyList<SynthesisStatusSegment> Status => Array.Empty<SynthesisStatusSegment>();
        public IActionEvent StatusChanged => ActionEvent.Empty;

        public void Dispose()
        {
            if (mDisposed)
                return;
            mDisposed = true;
            Native.Destroy(mHandle);
        }
    }

    static readonly IReadOnlyMap<string, SynthesizedParameter> EmptySynthesizedParameters = new Map<string, SynthesizedParameter>();

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct PluginInfo
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[]? ClassId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[]? Name;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[]? Vendor;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1024)] public byte[]? Path;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ParameterInfo
        {
            public uint Id;
            public double Minimum;
            public double Maximum;
            public double DefaultValue;
            public int Flags;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[]? Name;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[]? Units;
        }

        [DllImport("TuneLabVst3Host", EntryPoint = "tl_vst3_scan", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Scan();
        [DllImport("TuneLabVst3Host", EntryPoint = "tl_vst3_plugin_count", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint PluginCount();
        [DllImport("TuneLabVst3Host", EntryPoint = "tl_vst3_plugin_get", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int PluginGet(nuint index, ref PluginInfo result);
        [DllImport("TuneLabVst3Host", EntryPoint = "tl_vst3_create", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern IntPtr Create(string classId, double sampleRate, int maximumFrames, out int error);
        [DllImport("TuneLabVst3Host", EntryPoint = "tl_vst3_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(IntPtr handle);
        [DllImport("TuneLabVst3Host", EntryPoint = "tl_vst3_reset", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Reset(IntPtr handle);
        [DllImport("TuneLabVst3Host", EntryPoint = "tl_vst3_parameter_count", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint ParameterCount(IntPtr handle);
        [DllImport("TuneLabVst3Host", EntryPoint = "tl_vst3_parameter_get", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ParameterGet(IntPtr handle, nuint index, ref ParameterInfo result, out double currentValue);
        [DllImport("TuneLabVst3Host", EntryPoint = "tl_vst3_parameter_set_at", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ParameterSetAt(IntPtr handle, uint parameterId, double value, int sampleOffset);
        [DllImport("TuneLabVst3Host", EntryPoint = "tl_vst3_process", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Process(IntPtr handle, [In] float[] input, [Out] float[] output, int frameCount);
    }
}
