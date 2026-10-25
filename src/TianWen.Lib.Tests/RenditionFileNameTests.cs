using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The name "Save image as displayed" suggests (<see cref="RenditionFileName.Suffix"/>): the stretch on show always, and a
/// code for each setting moved off its default, so a save under other settings does not land on the first.
/// </summary>
public class RenditionFileNameTests
{
    [Fact]
    public void DefaultSettingsNameOnlyTheStretch()
    {
        RenditionFileName.Suffix(new ViewerState(), StretchMode.Linked, enhanced: false, starSplitShown: false)
            .ShouldBe("_linked");
    }

    [Fact]
    public void DefaultSettingsWithAStarSplitNameNoStarCodes()
    {
        RenditionFileName.Suffix(new ViewerState(), StretchMode.Luma, enhanced: true, starSplitShown: true)
            .ShouldBe("_enh_luma");
    }

    [Fact]
    public void EverySettingMovedOffItsDefaultIsNamedInOrder()
    {
        var state = new ViewerState
        {
            StretchParameters = new StretchParameters(0.25, -3.5),
            ColorCalibrationEnabled = false,
            BackgroundNeutralizationStrength = 0.5f,
            BackgroundSkyLevel = 0.09f,
            CurvesBoost = 0.3f,
            HdrAmount = 0.4f,
            FinishContrast = 0.2f,
            FinishSaturation = 1.24f,
            StarStretch = 302f,
            StarSaturation = 1.1f,
        };

        RenditionFileName.Suffix(state, StretchMode.Luma, enhanced: true, starSplitShown: true)
            .ShouldBe("_enh_luma_f0.25_clip-3.5_nocal_bns0.5_sky0.09_boost0.3_hdr0.4_con0.2_sat1.24_ss302_ssat1.1");
    }

    [Fact]
    public void StarCodesAreLeftOutWhereNoSplitIsShown()
    {
        var state = new ViewerState { StarStretch = 302f, StarSaturation = 1.1f };

        RenditionFileName.Suffix(state, StretchMode.Linked, enhanced: false, starSplitShown: false).ShouldBe("_linked");
    }

    [Fact]
    public void SetisStarStretchIsNamedByItsAmount()
    {
        var state = new ViewerState { StarMethod = StarStretchMethod.StarStretch, StarAmount = 6f, StarStretch = 302f };

        RenditionFileName.Suffix(state, StretchMode.Linked, enhanced: false, starSplitShown: true).ShouldBe("_linked_sst6");
    }

    [Fact]
    public void ASettingThatDoesNotReachThePictureIsNotNamed()
    {
        // The sky level only counts while the reduce runs, the neutralisation's strength only while it is on, and the
        // stretch parameters not at all on a linear view.
        var state = new ViewerState
        {
            BackgroundReduceEnabled = false,
            BackgroundSkyLevel = 0.05f,
            BackgroundNeutralizationEnabled = false,
            BackgroundNeutralizationStrength = 0.5f,
            StretchParameters = new StretchParameters(0.25, -3.5),
        };

        RenditionFileName.Suffix(state, StretchMode.None, enhanced: false, starSplitShown: false)
            .ShouldBe("_linear_nobn_nored");
    }

    [Fact]
    public void AManualWhiteBalanceNamesAllThreeGains()
    {
        var state = new ViewerState { ManualWhiteBalance = (1.2f, 1f, 0.85f) };

        RenditionFileName.Suffix(state, StretchMode.Unlinked, enhanced: false, starSplitShown: false)
            .ShouldBe("_unlinked_wb1.2-1-0.85");
    }
}
