using System.Globalization;
using System.Numerics;
using Shouldly;
using TianWen.Cli;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// <c>tianwen image flatten --exclude</c> takes two shapes on one option, and the whole reason a
    /// caller reaches for it is to keep the fit off an object it can see and the fit cannot. A spec
    /// that parses into the WRONG region is the failure that matters: the run still succeeds, the
    /// model is still plausible, and the object is still in the fit.
    /// </summary>
    public class FlattenExclusionParsingTests
    {
        [Theory]
        [InlineData("100,200,400,300")]
        [InlineData(" 100 , 200 , 400 , 300 ")]
        [InlineData("400,300,100,200")]  // corners in the other order name the same rectangle
        [InlineData("400,200,100,300")]
        public void ARectangleIsFourNumbersInAnyCornerOrder(string spec)
        {
            ImageSubCommand.TryParseExclusion(spec, out var polygon).ShouldBeTrue();
            polygon.ShouldNotBeNull();
            polygon.Vertices.Length.ShouldBe(4);
            polygon.Contains(250f, 250f).ShouldBeTrue();
            polygon.Contains(50f, 250f).ShouldBeFalse();
            polygon.Contains(250f, 500f).ShouldBeFalse();
        }

        [Fact]
        public void APolygonIsSemicolonSeparatedPairsAndKeepsItsVerticesInOrder()
        {
            ImageSubCommand.TryParseExclusion("0,0;100,0;100,100;0,100", out var polygon).ShouldBeTrue();
            polygon.ShouldNotBeNull();
            polygon.Vertices.ShouldBe(new[]
            {
                new Vector2(0f, 0f), new Vector2(100f, 0f), new Vector2(100f, 100f), new Vector2(0f, 100f),
            });
            polygon.Contains(50f, 50f).ShouldBeTrue();
            polygon.Contains(150f, 50f).ShouldBeFalse();
        }

        /// <summary>
        /// The two shapes are told apart by the SEPARATOR, not by counting numbers. A four-vertex
        /// polygon has eight numbers and a rectangle has four, but a THREE-vertex polygon also has six
        /// and a triangle is the shape a user is most likely to draw by hand; counting would have to
        /// guess. This is what pins that decision.
        /// </summary>
        [Fact]
        public void AThreeVertexPolygonIsAPolygonAndNotAMalformedRectangle()
        {
            ImageSubCommand.TryParseExclusion("0,0;100,0;50,100", out var polygon).ShouldBeTrue();
            polygon.ShouldNotBeNull();
            polygon.Vertices.Length.ShouldBe(3);
            polygon.Contains(50f, 50f).ShouldBeTrue();
            polygon.Contains(5f, 90f).ShouldBeFalse();
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("1,2,3")]              // three numbers is neither shape
        [InlineData("1,2,3,4,5")]
        [InlineData("0,0;100,0")]          // two vertices cannot bound anything
        [InlineData("0,0;100;50,100")]     // a vertex that is not a pair
        [InlineData("0,0;abc,0;50,100")]
        [InlineData("nope")]
        public void AnythingElseIsRefusedRatherThanGuessedAt(string spec)
        {
            ImageSubCommand.TryParseExclusion(spec, out var polygon).ShouldBeFalse();
            polygon.ShouldBeNull();
        }

        /// <summary>
        /// Coordinates are parsed with the invariant culture, so a spec written on a comma-decimal
        /// machine reads the same as one written here. The separator IS a comma, so "1,5" can only
        /// mean two coordinates, and a decimal comma has to be a parse failure rather than a silent
        /// re-interpretation.
        /// </summary>
        [Fact]
        public void CoordinatesAreInvariantCultureSoADecimalPointAlwaysMeansOne()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                ImageSubCommand.TryParseExclusion("10.5,20.5,40.5,30.5", out var polygon).ShouldBeTrue();
                polygon.ShouldNotBeNull();
                polygon.Vertices[0].X.ShouldBe(10.5f);
                polygon.Vertices[0].Y.ShouldBe(20.5f);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }
}
