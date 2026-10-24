using TuneLab.Extensions.Effect;
using TuneLab.Foundation;
using Xunit;

namespace TuneLab.Tests;

public class Vst3EffectPropertyTests
{
    [Fact]
    public void ReadParameterValue_UsesNumericDefaultAndPreservesStoredValue()
    {
        var properties = new DataPropertyObject();
        properties.SetValue("vst3_param_00000000", PropertyValue.Create(0.75));

        Assert.Equal(0.75, Vst3EffectEngine.ReadParameterValue(properties, "vst3_param_00000000", 0.5));
        Assert.Equal(0.5, Vst3EffectEngine.ReadParameterValue(properties, "missing", 0.5));
    }

    [Fact]
    public void ReadParameterValue_ClampsStoredValueToVst3Range()
    {
        var properties = new DataPropertyObject();
        properties.SetValue("parameter", PropertyValue.Create(1.5));

        Assert.Equal(1.0, Vst3EffectEngine.ReadParameterValue(properties, "parameter", 0.5));
    }
}
