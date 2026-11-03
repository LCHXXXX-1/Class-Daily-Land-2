using ClassDailyLand2.App.Integrations.Disaster;
using Xunit;

namespace ClassDailyLand2.Core.Tests;

/// <summary>
/// 地震预警占用主岛的规则。
///
/// 回归的重点是第一条：**横波还在路上时，红条必须一直显示到抵达**。
/// 之前把"倒计时"和"已到达小震的 5 秒提示"合并成一条规则，倒计时会在第 5 秒凭空消失。
/// </summary>
public sealed class DisasterEmergencyTests
{
    static QuakeEvent MakeEvent() => new(
        Uid: "drill:1",
        Source: "drill",
        EventId: "drill",
        Revision: 1,
        OriginTs: 1000,
        UpdatedTs: 1000,
        Lat: 30.6,
        Lon: 103.0,
        Depth: 10,
        Magnitude: 6.4,
        Epicenter: "演练震中");

    static QuakeEvaluation MakeEvaluation(string phase, string level, double arriveTs, double remainSec = 20) => new(
        Uid: "drill:1",
        DistanceKm: 120,
        LocalIntensity: 4.5,
        Level: level,
        CountdownSec: 20,
        ArriveTs: arriveTs,
        RemainSec: remainSec,
        Phase: phase,
        Triggered: true,
        Displayable: true,
        Event: MakeEvent());

    [Fact]
    public void Incoming_KeepsIsland_EvenAfterSmallHold()
    {
        // 事件出现 60 秒后才到达（远处地震），中途绝不能因为"超过 5 秒"就把红条收掉
        var focus = MakeEvaluation("incoming", QuakeLevels.Yellow, arriveTs: 1060);

        Assert.True(DisasterService.ShouldTakeOverIsland(focus, big: false, now: 1030, seenAt: 1000, smallHoldSec: 5));
        Assert.True(DisasterService.ShouldTakeOverIsland(focus, big: false, now: 1059, seenAt: 1000, smallHoldSec: 5));
    }

    [Fact]
    public void Incoming_IgnoresDisabledSmallHold()
    {
        // 把"小震提示"关掉（smallHoldSec <= 0）也不该影响倒计时
        var focus = MakeEvaluation("incoming", QuakeLevels.Yellow, arriveTs: 1060);

        Assert.True(DisasterService.ShouldTakeOverIsland(focus, big: false, now: 1020, seenAt: 1000, smallHoldSec: 0));
    }

    [Fact]
    public void Arrived_SmallQuake_HoldsFiveSecondsAfterArrival()
    {
        // 到达后 2 秒：还在"已到达"提示窗口里
        var focus = MakeEvaluation("arrived", QuakeLevels.Yellow, arriveTs: 1060, remainSec: 0);

        Assert.True(DisasterService.ShouldTakeOverIsland(focus, big: false, now: 1062, seenAt: 1000, smallHoldSec: 5));
    }

    [Fact]
    public void Arrived_SmallQuake_ReleasesAfterHold()
    {
        // 从"到达时刻"算起超过 5 秒就交还主岛（不是从事件出现算起）
        var focus = MakeEvaluation("arrived", QuakeLevels.Yellow, arriveTs: 1060, remainSec: 0);

        Assert.False(DisasterService.ShouldTakeOverIsland(focus, big: false, now: 1066, seenAt: 1000, smallHoldSec: 5));
    }

    [Fact]
    public void Arrived_RedLevel_KeepsIsland()
    {
        var focus = MakeEvaluation("arrived", QuakeLevels.Red, arriveTs: 1060, remainSec: 0);

        Assert.True(DisasterService.ShouldTakeOverIsland(focus, big: true, now: 9999, seenAt: 1000, smallHoldSec: 5));
    }

    [Fact]
    public void Arrived_SmallQuake_WithZeroHold_ReleasesImmediately()
    {
        var focus = MakeEvaluation("arrived", QuakeLevels.Blue, arriveTs: 1060, remainSec: 0);

        Assert.False(DisasterService.ShouldTakeOverIsland(focus, big: false, now: 1061, seenAt: 1000, smallHoldSec: 0));
    }
}
