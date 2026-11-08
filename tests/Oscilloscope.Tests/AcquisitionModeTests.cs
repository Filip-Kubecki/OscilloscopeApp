using Oscilloscope.Core.Device;
using Xunit;

namespace Oscilloscope.Tests;

public class AcquisitionModeTests
{
    [Theory]
    [InlineData(AcquisitionMode.Auto, 0u)]
    [InlineData(AcquisitionMode.Normal, 1u)]
    [InlineData(AcquisitionMode.Single, 2u)]
    public void Modes_have_the_register_values(AcquisitionMode mode, uint value)
    {
        Assert.Equal(value, (uint)mode);
    }
}
