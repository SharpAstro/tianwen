using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using DIR.Lib;
using SharpAstro.Png;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Who may command a rig, on its Home card (P6b of docs/plans/hardware-in-the-server.md, decision 13, #1021): each card says
/// who may; a rig this client only watches offers to ask; and the rig on show, when this client may manage it, has its
/// Sharing panel under the board, every action of which posts its signal for that rig. Through the real Home tab.
/// </summary>
[Collection("UI")]
public class HomeSharingTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Observatory = Guid.Parse("7b3ae2c4-19e0-4e0c-a9a5-3c1d1b6e2f10");

    private static readonly NodeAccessDto Busy = new NodeAccessDto
    {
        Shared = true,
        Listening = true,
        Pending = new PendingControlRequestDto { Id = "request-1", Label = "Tablet", Address = "192.168.1.21" },
        Grants = [new GrantDto { Id = "grant-1", Label = "Laptop, TianWen", GrantedAt = Now }],
        AppsAllowed = [new AllowedAppDto { Address = "192.168.1.30", Host = "nina.lan" }],
        HostsAlwaysAllowed = ["sgp.lan"],
        Refused = [new RefusedAppDto { Address = "192.168.1.40", Host = "touch.lan", UserAgent = "TouchNStars/2.1", Protocol = AppProtocol.NinaV2,
            What = "GET /v2/api/equipment/mount/park", FirstAt = Now, LastAt = Now, Attempts = 3 }],
    };

    private static RigCard Card(string title, bool local, bool viewed, RigSharing? sharing, Guid? binding = null) =>
        new RigCard(title, "Test", IsLocal: local, IsOnline: true, SessionPhase.NotStarted, "Idle", null, 0, null, null, null,
            IsViewed: viewed, BindingId: binding, Sharing: sharing);

    private static (HomeTab<RgbaImage> Tab, List<object> Posted, SignalBus Bus) Render(RgbaImageRenderer renderer, ImmutableArray<RigCard> cards)
    {
        var bus = new SignalBus();
        var posted = new List<object>();
        bus.Subscribe<AskForControlSignal>(s => posted.Add(s));
        bus.Subscribe<CancelControlAskSignal>(s => posted.Add(s));
        bus.Subscribe<AnswerControlRequestSignal>(s => posted.Add(s));
        bus.Subscribe<RevokeGrantSignal>(s => posted.Add(s));
        bus.Subscribe<AllowAppSignal>(s => posted.Add(s));
        bus.Subscribe<RevokeAppSignal>(s => posted.Add(s));
        bus.Subscribe<RevokeHostSignal>(s => posted.Add(s));
        bus.Subscribe<IgnoreRefusedAppSignal>(s => posted.Add(s));
        bus.Subscribe<SetLanShareSignal>(s => posted.Add(s));
        var tab = new HomeTab<RgbaImage>(renderer) { DpiScale = 1f, FontPath = FontResolver.ResolveSystemFont(), Bus = bus };
        tab.Render(new GuiAppState { HomeCards = cards }, new RectF32(0, 0, renderer.Width, renderer.Height), Now);
        return (tab, posted, bus);
    }

    /// <summary>Presses the region named <paramref name="hit"/> the way a click arrives, through the router.</summary>
    private static void Press(HomeTab<RgbaImage> tab, SignalBus bus, string hit)
    {
        var region = tab.GetRegisteredRegions().Single(r => r.Result is HitResult.ButtonHit { Action: var action } && action == hit);
        var (x, y) = (region.X + region.Width / 2f, region.Y + region.Height / 2f);
        var router = new InputRouter(tab.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => [tab] };
        router.Handle(new InputEvent.MouseDown(x, y));
        router.Handle(new InputEvent.MouseUp(x, y));
        bus.ProcessPending();
    }

    [Fact]
    public void ACardSaysWhoMayCommandItsRig()
    {
        new RigSharing(true, ControlAsk.None, Busy).Describe(isLocal: true)
            .ShouldBe("Shared on the LAN, 1 client granted control, Tablet asks for control, 1 application refused");
        new RigSharing(true, ControlAsk.None, new NodeAccessDto()).Describe(isLocal: true).ShouldBe("Not shared on the LAN");
        new RigSharing(true, ControlAsk.None, new NodeAccessDto()).Describe(isLocal: false).ShouldBe("This computer controls it");
        new RigSharing(false, ControlAsk.None, null).Describe(isLocal: false).ShouldBe("Watching: this computer may not command it");
        new RigSharing(false, new ControlAsk(ControlAskState.Asking, null), null).Describe(isLocal: false).ShouldBe("Asking its owner for control");
        new RigSharing(false, new ControlAsk(ControlAskState.Declined, "Its owner declined"), null).Describe(isLocal: false)
            .ShouldBe("Watching: its owner declined control");
    }

    /// <summary>
    /// The setting against what the node does now, stated in the present only. It used to promise the node would
    /// stop listening "at its next start", which a node run by hand with a LAN address on its command line never does.
    /// </summary>
    [Theory]
    [InlineData(true, true, "Shared on the LAN")]
    [InlineData(true, false, "Shared, but not listening on the LAN now")]
    [InlineData(false, true, "Not shared, but listening on the LAN now")]
    [InlineData(false, false, "Not shared on the LAN")]
    public void TheSharingLineSaysWhatTheNodeDoesNow(bool shared, bool listening, string expected)
    {
        RigSharing.DescribeLan(new NodeAccessDto { Shared = shared, Listening = listening }).ShouldBe(expected);
    }

    [Fact]
    public void ARigThisComputerOnlyWatchesOffersToAskAndToStopAsking()
    {
        using var renderer = new RgbaImageRenderer(1600, 1000);
        ImmutableArray<RigCard> watching =
        [
            Card("This computer", local: true, viewed: false, new RigSharing(true, ControlAsk.None, new NodeAccessDto())),
            Card("Observatory", local: false, viewed: true, new RigSharing(false, ControlAsk.None, null), Observatory),
        ];
        var (tab, posted, bus) = Render(renderer, watching);

        Press(tab, bus, "HomeAsk:Observatory");

        posted.ShouldBe([new AskForControlSignal(Observatory)], "and not a select of the card under the button");
        tab.GetRegisteredRegions().Where(r => r.Result is HitResult.ButtonHit { Action: var a } && a.StartsWith("Sharing:"))
            .ShouldBeEmpty("a rig this client only watches has no Sharing panel: who may command it is its owner's");

        ImmutableArray<RigCard> asking = [watching[0], watching[1] with { Sharing = new RigSharing(false, new ControlAsk(ControlAskState.Asking, null), null) }];
        (tab, posted, bus) = Render(renderer, asking);
        Press(tab, bus, "HomeAsk:Observatory");
        posted.ShouldBe([new CancelControlAskSignal(Observatory)]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSharingPanelOfTheRigOnShowPostsEachActionForThatRig(bool local)
    {
        using var renderer = new RgbaImageRenderer(1600, 1000);
        var title = local ? "This computer" : "Observatory";
        Guid? binding = local ? null : Observatory;
        ImmutableArray<RigCard> cards = local
            ? [Card(title, local: true, viewed: true, new RigSharing(true, ControlAsk.None, Busy))]
            : [Card("This computer", local: true, viewed: false, new RigSharing(true, ControlAsk.None, new NodeAccessDto())),
               Card(title, local: false, viewed: true, new RigSharing(true, ControlAsk.None, Busy), Observatory)];

        (string Hit, object Posted)[] actions =
        [
            ("Share", new SetLanShareSignal(false, binding)),
            ("AllowRequest", new AnswerControlRequestSignal("request-1", true, binding)),
            ("DeclineRequest", new AnswerControlRequestSignal("request-1", false, binding)),
            ("Revoke:grant-1", new RevokeGrantSignal("grant-1", binding)),
            ("RevokeApp:192.168.1.30", new RevokeAppSignal("192.168.1.30", binding)),
            ("RevokeHost:sgp.lan", new RevokeHostSignal("sgp.lan", binding)),
            ("AllowApp:192.168.1.40", new AllowAppSignal("192.168.1.40", false, binding)),
            ("AlwaysAllowApp:192.168.1.40", new AllowAppSignal("192.168.1.40", true, binding)),
            ("Ignore:192.168.1.40", new IgnoreRefusedAppSignal("192.168.1.40", binding)),
        ];

        foreach (var (hit, want) in actions)
        {
            var (tab, posted, bus) = Render(renderer, cards);
            Press(tab, bus, $"Sharing:{title}:{hit}");
            posted.ShouldBe([want], hit);
        }
    }

    [Fact]
    public void TheSharingPanelSitsUnderTheBoardAndClearOfTheCards()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        ImmutableArray<RigCard> cards = [Card("This computer", local: true, viewed: true, new RigSharing(true, ControlAsk.None, Busy))];
        var (tab, _, _) = Render(renderer, cards);

        var card = tab.GetRegisteredRegions().Single(r => r.Result is HitResult.ButtonHit { Action: "HomeRig:This computer" });
        var panel = tab.GetRegisteredRegions().Where(r => r.Result is HitResult.ButtonHit { Action: var a } && a.StartsWith("Sharing:")).ToArray();
        panel.Length.ShouldBe(9);
        panel.ShouldAllBe(button => button.Y >= card.Y + card.Height, "the panel is under the cards, not over them");
        panel.ShouldAllBe(button => button.Y + button.Height <= renderer.Height && button.X + button.Width <= renderer.Width);

        // For a look: the board as drawn.
        var path = Path.Combine(SharedTestData.CreateTempTestOutputDir(), "home_sharing.png");
        File.WriteAllBytes(path, PngWriter.Encode(renderer.Surface.Pixels, (int)renderer.Width, (int)renderer.Height));
    }
}
