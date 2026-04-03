using System.Collections.Generic;
using System.Linq;
using TianWen.Lib.Astrometry.Catalogs;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests
{
    [Collection("Catalog")]
    public class ConstellationFiguresAndEdgesTests
    {
        // ── Figures ──

        [Fact]
        public void AllIAUConstellationsExceptSerpensHaveFigures()
        {
            // Every constellation enum value (except SerpensCaput/SerpensCauda which are sub-parts)
            // should have a figure defined
            var allConstellations = System.Enum.GetValues<Constellation>()
                .Where(c => c is not Constellation.SerpensCaput and not Constellation.SerpensCauda);

            foreach (var constellation in allConstellations)
            {
                constellation.HasFigure.ShouldBeTrue($"{constellation} should have a figure");
            }
        }

        [Fact]
        public void SerpensCaputAndCaudaHaveSeparateFigures()
        {
            Constellation.SerpensCaput.HasFigure.ShouldBeTrue();
            Constellation.SerpensCauda.HasFigure.ShouldBeTrue();

            var caput = Constellation.SerpensCaput.Figure;
            var cauda = Constellation.SerpensCauda.Figure;

            caput.Length.ShouldBe(1, "SerpensCaput should have 1 polyline");
            cauda.Length.ShouldBe(1, "SerpensCauda should have 1 polyline");

            // Stars should be different between head and tail
            var caputStars = caput.SelectMany(p => p).ToHashSet();
            var caudaStars = cauda.SelectMany(p => p).ToHashSet();
            caputStars.Overlaps(caudaStars).ShouldBeFalse("Caput and Cauda should share no stars");
        }

        [Fact]
        public void SerpensComposesFromCaputAndCauda()
        {
            var serpens = Constellation.Serpens.Figure;
            var caput = Constellation.SerpensCaput.Figure;
            var cauda = Constellation.SerpensCauda.Figure;

            serpens.Length.ShouldBe(caput.Length + cauda.Length);
        }

        [Theory]
        [InlineData(Constellation.Orion)]
        [InlineData(Constellation.UrsaMajor)]
        [InlineData(Constellation.Crux)]
        [InlineData(Constellation.Cassiopeia)]
        public void FigurePolylinesContainValidHIPNumbers(Constellation constellation)
        {
            var figure = constellation.Figure;
            figure.Length.ShouldBeGreaterThan(0);

            foreach (var polyline in figure)
            {
                polyline.Length.ShouldBeGreaterThanOrEqualTo(2, "Each polyline needs at least 2 stars");
                foreach (var hip in polyline)
                {
                    hip.ShouldBeGreaterThan(0, "HIP numbers are positive");
                    hip.ShouldBeLessThan(200000, "HIP catalog has ~120k entries");
                }
            }
        }

        [Fact]
        public void OrionHasExpectedStructure()
        {
            // Orion is one of the most recognizable constellations
            var figure = Constellation.Orion.Figure;
            figure.Length.ShouldBeGreaterThanOrEqualTo(3, "Orion has multiple polylines (belt, body, etc.)");

            // Should have a reasonable number of stars
            var totalStars = figure.Sum(p => p.Length);
            totalStars.ShouldBeGreaterThan(10, "Orion should reference >10 stars");
        }

        [Fact]
        public void CruxHasFourStarsInTwoLines()
        {
            // Southern Cross: 2 crossing lines, 4 stars
            var figure = Constellation.Crux.Figure;
            figure.Length.ShouldBe(2);
            figure[0].Length.ShouldBe(2);
            figure[1].Length.ShouldBe(2);
        }

        // ── Edges ──

        [Fact]
        public void EdgeDataContains781Entries()
        {
            ConstellationEdges.Edges.Length.ShouldBe(781);
        }

        [Fact]
        public void AllEdgesHaveValidCoordinates()
        {
            foreach (var edge in ConstellationEdges.Edges)
            {
                edge.RA1.ShouldBeInRange(0.0, 24.0);
                edge.RA2.ShouldBeInRange(0.0, 24.0);
                edge.Dec1.ShouldBeInRange(-90.0, 90.0);
                edge.Dec2.ShouldBeInRange(-90.0, 90.0);
            }
        }

        [Fact]
        public void ParallelEdgesHaveConstantDec()
        {
            foreach (var edge in ConstellationEdges.Edges)
            {
                if (edge.Type == ConstellationEdges.EdgeType.Parallel)
                {
                    edge.Dec1.ShouldBe(edge.Dec2, 0.001, "Parallel edges should have constant Dec");
                }
            }
        }

        [Fact]
        public void MeridianEdgesHaveConstantRA()
        {
            foreach (var edge in ConstellationEdges.Edges)
            {
                if (edge.Type == ConstellationEdges.EdgeType.Meridian)
                {
                    edge.RA1.ShouldBe(edge.RA2, 0.001, "Meridian edges should have constant RA");
                }
            }
        }

        [Fact]
        public void AllIAUConstellationsAppearInEdges()
        {
            // Every IAU constellation (except Serpens parent) should appear in at least one edge
            var edgeConstellations = new HashSet<Constellation>();
            foreach (var edge in ConstellationEdges.Edges)
            {
                edgeConstellations.Add(edge.Con1);
                edgeConstellations.Add(edge.Con2);
            }

            var allConstellations = System.Enum.GetValues<Constellation>()
                .Where(c => c is not Constellation.Serpens); // Serpens parent not in edges, only Caput/Cauda

            foreach (var constellation in allConstellations)
            {
                edgeConstellations.ShouldContain(constellation,
                    $"{constellation} should appear in at least one boundary edge");
            }
        }

        [Fact]
        public void EdgesAreBetweenDifferentConstellations()
        {
            foreach (var edge in ConstellationEdges.Edges)
            {
                edge.Con1.ShouldNotBe(edge.Con2,
                    $"Edge should separate two different constellations, got {edge.Con1} on both sides");
            }
        }

        // ── Integration: figures reference stars that exist, boundaries cover all constellations ──

        [Fact]
        public void FiguresAndBrightestStarsAreConsistent()
        {
            // Every constellation with a figure should also have a brightest star defined
            foreach (var constellation in System.Enum.GetValues<Constellation>())
            {
                if (constellation.HasFigure)
                {
                    // GetBrighestStar should not throw
                    var brightestStar = constellation.GetBrighestStar();
                    brightestStar.ShouldNotBe(default(CatalogIndex),
                        $"{constellation} has a figure but no brightest star");
                }
            }
        }

        [Fact]
        public void BoundaryTableConsistentWithEdges()
        {
            // The boundary table (for point-in-constellation lookup) should reference
            // the same set of constellations as the edge data
            var tableConstellations = ConstellationBoundary.Table
                .Select(b => b.Constellation)
                .ToHashSet();

            var edgeConstellations = ConstellationEdges.Edges
                .SelectMany(e => new[] { e.Con1, e.Con2 })
                .ToHashSet();

            // Both should cover the same IAU constellations (excluding Serpens parent)
            foreach (var c in tableConstellations)
            {
                if (c is not Constellation.Serpens)
                {
                    edgeConstellations.ShouldContain(c,
                        $"{c} is in boundary table but not in edges");
                }
            }
        }
    }
}
