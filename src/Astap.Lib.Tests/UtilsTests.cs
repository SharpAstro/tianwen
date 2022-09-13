using Astap.Lib.Astrometry;
using Shouldly;
using Xunit;

namespace Astap.Lib.Tests
{
    public class UtilsTests
    {

        [Theory]
        [InlineData("05:23:34.5", 80.89375d)]
        [InlineData("23:54:13.2", 358.555d)]
        [InlineData("23:59:59.9", 359.9995833333333d)]
        [InlineData("0:0:0", 0d)]
        public void GivenHMSWHenConvertToDegreesItReturnsDegreesAsDouble(string hms, double expectedDegrees)
        {
            Utils.HMSToDegree(hms).ShouldBe(expectedDegrees);
        }

        [Theory]
        [InlineData("+61:44:24", 72.1d)]
        [InlineData("-12:03:18", -11.175d)]
        [InlineData("+90:0:0", +90.0d)]
        [InlineData("-90:0:0", -90.0d)]
        [InlineData("0:0:0", 0.0d)]
        public void GivenDMSWHenConvertToDegreesItReturnsDegreesAsDouble(string dms, double expectedDegrees)
        {
            Utils.DMSToDegree(dms).ShouldBe(expectedDegrees);
        }
    }
}
