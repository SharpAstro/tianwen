using Shouldly;
using TianWen.AI.Imaging.RcAstro;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What RC-Astro's CLI says of itself, as far as it is read (<c>rc-astro --json</c>): its version, and the newest model
/// installed of each product, which is the one a run uses. An RC-Astro role's name carries the model version, so a
/// workbench step is keyed by it; the licence line says less ("licensed through ML3" covers ML3.1).
/// </summary>
public class RcAstroInstallTests
{
    // `rc-astro --json` of CLI 2.6.11 as it is shaped, with the account's address replaced and NoiseXTerminator's
    // models listed oldest first, which the CLI does not do, so the order is seen not to matter.
    private const string Described = """
        {
          "schemaVersion": 6,
          "cliVersion": "2.6.11",
          "devices": [ { "id": "auto", "kind": "auto", "device": "gpu", "name": "A GPU" }, { "id": "cpu", "kind": "cpu" } ],
          "account": { "email": "someone@example.com" },
          "products": [
            { "key": "bxt", "name": "RC-Astro BlurXTerminator", "mlVersions": [ 5, 4, 2 ],
              "license": { "status": "permanent", "valid": true, "message": "Permanently licensed through ML5" } },
            { "key": "sxt", "name": "RC-Astro StarXTerminator", "mlVersions": [ 11 ], "license": { "valid": true } },
            { "key": "nxt", "name": "RC-Astro NoiseXTerminator", "mlVersions": [ 2, 3.1 ],
              "license": { "status": "permanent", "valid": true, "message": "Permanently licensed through ML3" } }
          ]
        }
        """;

    private static RcAstroCli.LicenseProbe Granted(string exePath, string productKey) => new(true, 0, "Permanently licensed");

    [Fact]
    public void TheNewestInstalledModelOfEachProductIsReadInTheClisOwnDigits()
    {
        var install = RcAstroCli.ParseInstall(Described).ShouldNotBeNull();

        install.CliVersion.ShouldBe("2.6.11");
        install.ModelVersions["bxt"].ShouldBe("5");
        install.ModelVersions["sxt"].ShouldBe("11");
        // The newest by value whatever the order, and not the "ML3" of the licence line.
        install.ModelVersions["nxt"].ShouldBe("3.1");
        install.ModelVersions.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[ 1, 2 ]")]
    [InlineData("{}")]
    public void TextThatIsNotThatDocumentIsNoDescription(string text)
        => RcAstroCli.ParseInstall(text).ShouldBeNull();

    [Fact]
    public void AProductThatListsNoModelOrHasNoKeyIsLeftOutAndTheClisVersionIsStillRead()
    {
        var install = RcAstroCli.ParseInstall(
            """{ "cliVersion": "2.6.11", "products": [ { "key": "bxt", "mlVersions": [] }, { "key": 7, "mlVersions": [ 1 ] }, { "mlVersions": [ 1 ] }, "sxt" ] }""")
            .ShouldNotBeNull();

        install.CliVersion.ShouldBe("2.6.11");
        install.ModelVersions.ShouldBeEmpty();
    }

    [Fact]
    public void TheCliIsAskedOnceAndNeverWhereItIsNotInstalled()
    {
        var asked = 0;
        string? Describe(string exePath)
        {
            asked++;
            return Described;
        }

        var cli = new RcAstroCli("/fake/rc-astro", Granted, describe: Describe);
        cli.ModelVersion("nxt").ShouldBe("3.1");
        cli.ModelVersion("bxt").ShouldBe("5");
        cli.ModelVersion("no-such-product").ShouldBeNull();
        cli.CliVersion.ShouldBe("2.6.11");
        asked.ShouldBe(1, "the description is kept for the process, as a granted licence is");

        var absent = new RcAstroCli(null, Granted, describe: Describe);
        absent.ModelVersion("nxt").ShouldBeNull();
        absent.CliVersion.ShouldBeNull();
        asked.ShouldBe(1, "nothing is asked of an executable that is not there");

        // A CLI that does not describe itself (an older one, or its answer was not that document): no version.
        var silent = new RcAstroCli("/fake/rc-astro", Granted, describe: static _ => "rc-astro: unknown option --json");
        silent.ModelVersion("nxt").ShouldBeNull();
        silent.CliVersion.ShouldBeNull();
    }

    [Fact]
    public void AnRcAstroEnhancersNameCarriesItsModelVersionAndNotTheClisOwn()
    {
        var cli = new RcAstroCli("/fake/rc-astro", Granted, describe: static _ => Described);

        new RcAstroDeblurrer(cli).Name.ShouldBe("RC-Astro BlurXTerminator (bxt, full-image) ML5");
        new RcAstroNonStellarDeconvolver(cli).Name.ShouldBe("RC-Astro BlurXTerminator (bxt) ML5");
        new RcAstroStarRemover(cli).Name.ShouldBe("RC-Astro StarXTerminator (sxt) ML11");
        new RcAstroDenoiser(cli).Name.ShouldBe("RC-Astro NoiseXTerminator (nxt) ML3.1");

        // Where the CLI says nothing of itself the name is the product alone, as it was before this was asked.
        new RcAstroDenoiser(new RcAstroCli("/fake/rc-astro", Granted)).Name.ShouldBe("RC-Astro NoiseXTerminator (nxt)");
    }
}
