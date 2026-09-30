using DXVKCompanion.Utils;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public class CompanionVersionTests
    {
        [Theory]
        [InlineData("1.0.0", "v1.0.0", false)]
        [InlineData("1.0.0", "v1.0.1", true)]
        [InlineData("1.0.9", "v1.0.10", true)]
        [InlineData("1.1.0", "v1.0.0", false)]
        [InlineData("2.0.0", "v1.9.9", false)]
        [InlineData("1.0.0", "V1.0.0", false)]
        public void IsOutdatedComparedTo_SpecificationContractExamples_MatchExpected(
            string localVersion, string githubTag, bool expected)
        {
            var result = CompanionVersion.IsOutdatedComparedTo(localVersion, githubTag);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("1.0.0", "1.0.0")]
        [InlineData("2.5.1", "2.5.1")]
        [InlineData("0.0.1", "0.0.1")]
        [InlineData("1.0.0", "v1.0.0")]
        [InlineData("1.0.0", "V1.0.0")]
        [InlineData("v1.0.0", "1.0.0")]
        [InlineData("V1.0.0", "1.0.0")]
        [InlineData("v1.0.0", "v1.0.0")]
        [InlineData("V1.0.0", "V1.0.0")]
        public void IsOutdatedComparedTo_EqualVersions_ReturnsFalse(string localVersion, string githubTag)
        {
            Assert.False(CompanionVersion.IsOutdatedComparedTo(localVersion, githubTag));
        }

        [Theory]
        [InlineData("1.0.0", "1.0.1")]
        [InlineData("1.0.0", "v1.0.1")]
        [InlineData("1.0.0", "V1.0.1")]
        [InlineData("1.0.0", "1.1.0")]
        [InlineData("1.0.0", "v1.1.0")]
        [InlineData("1.0.0", "2.0.0")]
        [InlineData("1.0.0", "v2.0.0")]
        [InlineData("0.9.9", "1.0.0")]
        [InlineData("1.2.3", "1.2.4")]
        [InlineData("1.2.3", "1.3.0")]
        [InlineData("1.2.3", "2.0.0")]
        [InlineData("v1.0.0", "v1.0.1")]
        [InlineData("V1.0.0", "V1.0.1")]
        public void IsOutdatedComparedTo_OlderLocalVersion_ReturnsTrue(string localVersion, string githubTag)
        {
            Assert.True(CompanionVersion.IsOutdatedComparedTo(localVersion, githubTag));
        }

        [Theory]
        [InlineData("1.0.1", "1.0.0")]
        [InlineData("1.0.1", "v1.0.0")]
        [InlineData("1.0.1", "V1.0.0")]
        [InlineData("1.1.0", "1.0.0")]
        [InlineData("1.1.0", "v1.0.9")]
        [InlineData("2.0.0", "1.9.9")]
        [InlineData("2.0.0", "v1.9.9")]
        [InlineData("1.2.4", "1.2.3")]
        [InlineData("1.3.0", "1.2.9")]
        [InlineData("3.0.0", "2.99.99")]
        public void IsOutdatedComparedTo_NewerLocalVersion_ReturnsFalse(string localVersion, string githubTag)
        {
            Assert.False(CompanionVersion.IsOutdatedComparedTo(localVersion, githubTag));
        }

        [Theory]
        [InlineData("1.0.9", "1.0.10", true)]
        [InlineData("1.0.10", "1.0.9", false)]
        [InlineData("1.9.0", "1.10.0", true)]
        [InlineData("1.10.0", "1.9.0", false)]
        [InlineData("9.0.0", "10.0.0", true)]
        [InlineData("10.0.0", "9.0.0", false)]
        [InlineData("1.0.99", "1.0.100", true)]
        [InlineData("1.0.100", "1.0.99", false)]
        public void IsOutdatedComparedTo_NumericOrdering_DoesNotUseLexicographicComparison(
            string localVersion, string githubTag, bool expected)
        {
            Assert.Equal(expected, CompanionVersion.IsOutdatedComparedTo(localVersion, githubTag));
        }

        [Theory]
        [InlineData("1.0.0", "v1.0.1", true)]
        [InlineData("1.0.0", "V1.0.1", true)]
        [InlineData("v1.0.0", "1.0.1", true)]
        [InlineData("V1.0.0", "1.0.1", true)]
        [InlineData("v1.0.0", "V1.0.1", true)]
        [InlineData("V1.0.0", "v1.0.1", true)]
        [InlineData("1.0.0", "v1.0.0", false)]
        [InlineData("1.0.0", "V1.0.0", false)]
        public void IsOutdatedComparedTo_LeadingVPrefix_HandlesCaseInsensitively(
            string localVersion, string githubTag, bool expected)
        {
            Assert.Equal(expected, CompanionVersion.IsOutdatedComparedTo(localVersion, githubTag));
        }

        [Theory]
        [InlineData(null, "1.0.0")]
        [InlineData("1.0.0", null)]
        [InlineData(null, null)]
        [InlineData("", "1.0.0")]
        [InlineData("1.0.0", "")]
        [InlineData("", "")]
        [InlineData("   ", "1.0.0")]
        [InlineData("1.0.0", "   ")]
        [InlineData("1.0", "1.0.0")]
        [InlineData("1.0.0", "1.0")]
        [InlineData("1", "1.0.0")]
        [InlineData("1.0.0", "1")]
        [InlineData("1.0.0.0", "1.0.0")]
        [InlineData("1.0.0", "1.0.0.0")]
        [InlineData("1.0.0-beta", "1.0.0")]
        [InlineData("1.0.0", "1.0.1-rc1")]
        [InlineData("1.0.0", "1.0.1+build123")]
        [InlineData("1.0.a", "1.0.0")]
        [InlineData("1.0.0", "v1.0.x")]
        [InlineData("-1.0.0", "1.0.0")]
        [InlineData("1.0.0", "v-1.0.0")]
        [InlineData("+1.0.0", "1.0.0")]
        [InlineData("1.0.0", "+1.0.0")]
        [InlineData("vv1.0.0", "1.0.1")]
        [InlineData("1.0.0", "vv1.0.1")]
        [InlineData("v", "1.0.0")]
        [InlineData("1.0.0", "v")]
        [InlineData("1..0", "1.0.0")]
        [InlineData("1.0.0", "1..0")]
        [InlineData("unknown", "1.0.0")]
        [InlineData("1.0.0", "latest")]
        public void IsOutdatedComparedTo_MalformedOrUnsupportedInput_ReturnsFalseQuietly(
            string? localVersion, string? githubTag)
        {
            Assert.False(CompanionVersion.IsOutdatedComparedTo(localVersion!, githubTag!));
        }

        [Fact]
        public void Current_ReturnsNonEmptyVersion()
        {
            var current = CompanionVersion.Current;
            Assert.NotNull(current);
            Assert.NotEmpty(current);
        }
    }
}
