using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Structural tests for <see cref="EquipmentContent.GetProfilePanelSections"/> -- the data-driven
    /// section list that replaces the equipment panel's hardcoded cursor walk (TODO.md:57). Asserts the
    /// header/divider lead-in, the per-OTA loop (one header + four sub-slots per telescope), the
    /// filter-wheel-gated filter table, and the trailing Add-OTA action row.
    /// </summary>
    public class GetProfilePanelSectionsTests
    {
        private static readonly Uri None = NoneDevice.Instance.DeviceUri;

        private static OTAData Ota(string name, Uri? camera = null, Uri? filterWheel = null) =>
            new OTAData(name, 1000,
                Camera: camera, Cover: null, Focuser: null, FilterWheel: filterWheel,
                PreferOutwardFocus: null, OutwardIsPositive: null);

        private static ProfileData Data(params OTAData[] otas) =>
            new ProfileData(Mount: None, Guider: None, OTAs: [.. otas]);

        private readonly EquipmentContent _content = new EquipmentContent();

        [Fact]
        public void Starts_WithProfileHeaderThenSeparator()
        {
            var sections = _content.GetProfilePanelSections(Data());

            sections[0].ShouldBeOfType<PanelSection.ProfileHeader>();
            sections[1].ShouldBeOfType<PanelSection.Separator>();
        }

        [Fact]
        public void OtaHeaderCount_TracksTheNumberOfOtas()
        {
            _content.GetProfilePanelSections(Data(Ota("A")))
                .OfType<PanelSection.OtaHeader>().Count().ShouldBe(1);

            _content.GetProfilePanelSections(Data(Ota("A"), Ota("B")))
                .OfType<PanelSection.OtaHeader>().Count().ShouldBe(2);
        }

        [Fact]
        public void EachOta_EmitsFourSubSlotSections()
        {
            var sections = _content.GetProfilePanelSections(Data(Ota("A"), Ota("B")));

            // Camera/Focuser/FilterWheel/Cover per OTA = 4 OTA-level slot sections each.
            var otaSlots = sections.OfType<PanelSection.Slot>()
                .Count(s => s.Row.Slot is AssignTarget.OTALevel);

            otaSlots.ShouldBe(8);
        }

        [Fact]
        public void FilterTable_OnlyWhenAFilterWheelIsAssigned()
        {
            _content.GetProfilePanelSections(Data(Ota("A")))
                .OfType<PanelSection.FilterTable>().ShouldBeEmpty();

            var fw = new Uri("FilterWheel://FakeDevice/cfw");
            _content.GetProfilePanelSections(Data(Ota("A", filterWheel: fw)))
                .OfType<PanelSection.FilterTable>().Count().ShouldBe(1);
        }

        [Fact]
        public void Ends_WithTheAddOtaActionRow()
        {
            var sections = _content.GetProfilePanelSections(Data(Ota("A")));

            sections[^1].ShouldBeOfType<PanelSection.AddOta>();
        }
    }
}
