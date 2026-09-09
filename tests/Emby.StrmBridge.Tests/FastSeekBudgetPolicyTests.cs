using Emby.StrmBridge.Playback;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class FastSeekBudgetPolicyTests
{
    private const int MiB = 1024 * 1024;

    [TestMethod]
    [DataRow(188)]
    [DataRow(192)]
    [DataRow(204)]
    public void MediaWindowScalesWithContainerByteRateWithinAlignedBounds(int packetStride)
    {
        var low = FastSeekBudgetPolicy.CalculateTargetScanBytes(MiB, 100, packetStride);
        var medium = FastSeekBudgetPolicy.CalculateTargetScanBytes(100L * MiB, 100, packetStride);
        var high = FastSeekBudgetPolicy.CalculateTargetScanBytes(600L * MiB, 100, packetStride);
        var huge = FastSeekBudgetPolicy.CalculateTargetScanBytes(long.MaxValue, 1, packetStride);

        Assert.AreEqual(Align(512 * 1024, packetStride), low);
        Assert.AreEqual(Align(4 * MiB, packetStride), medium);
        Assert.AreEqual(Align(24 * MiB, packetStride), high);
        Assert.AreEqual(high, huge);
        Assert.IsTrue(low < medium && medium < high);
    }

    [TestMethod]
    public void PreparationBudgetTracksFourWindowsWithoutOverflowingItsSafetyLimit()
    {
        Assert.AreEqual(512 * 1024 + 4 * 512 * 1024,
            FastSeekBudgetPolicy.CalculatePreparationBytes(512 * 1024));
        Assert.AreEqual(512 * 1024 + 4 * 4 * MiB,
            FastSeekBudgetPolicy.CalculatePreparationBytes(4 * MiB));
        Assert.AreEqual(32 * MiB, FastSeekBudgetPolicy.CalculatePreparationBytes(24 * MiB));
        Assert.AreEqual(32 * MiB, FastSeekBudgetPolicy.CalculatePreparationBytes(int.MaxValue));
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    [DataRow(0d)]
    [DataRow(-1d)]
    [DataRow(double.Epsilon)]
    public void InvalidMediaTimingCannotProduceAnUnboundedWindow(double durationSeconds) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            FastSeekBudgetPolicy.CalculateTargetScanBytes(long.MaxValue, durationSeconds, 192));

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void InvalidMediaLengthsAndBudgetsAreRejected(int bytes)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            FastSeekBudgetPolicy.CalculateTargetScanBytes(bytes, 100, 192));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            FastSeekBudgetPolicy.CalculatePreparationBytes(bytes));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(190)]
    public void UnsupportedPacketStrideIsRejected(int packetStride)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            FastSeekBudgetPolicy.CalculateTargetScanBytes(MiB, 100, packetStride));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            FastSeekBudgetPolicy.CalculateRequestBytes(MiB, packetStride, TimeSpan.FromSeconds(7), null, null));
    }

    [TestMethod]
    [DataRow(188)]
    [DataRow(192)]
    [DataRow(204)]
    public void NetworkThroughputAndSetupTimeControlOnlyTheNextRange(int packetStride)
    {
        var remaining = TimeSpan.FromSeconds(3);
        var setup = TimeSpan.FromSeconds(1);
        var fast = FastSeekBudgetPolicy.CalculateRequestBytes(8 * MiB, packetStride,
            remaining, setup, 16d * MiB);
        var slow = FastSeekBudgetPolicy.CalculateRequestBytes(8 * MiB, packetStride,
            remaining, setup, MiB);

        Assert.AreEqual(Align(8 * MiB, packetStride), fast);
        Assert.AreEqual(Align(MiB, packetStride), slow);
        Assert.AreEqual(TimeSpan.FromSeconds(3), remaining);
    }

    [TestMethod]
    public void ReducedRemainingTimeShrinksTheRangeWithoutRestartingTheDeadline()
    {
        var earlier = FastSeekBudgetPolicy.CalculateRequestBytes(8 * MiB, 192,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), MiB);
        var later = FastSeekBudgetPolicy.CalculateRequestBytes(8 * MiB, 192,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), MiB);

        Assert.AreEqual(Align(2 * MiB, 192), earlier);
        Assert.AreEqual(Align(512 * 1024, 192), later);
    }

    [TestMethod]
    [DataRow(188)]
    [DataRow(192)]
    [DataRow(204)]
    public void PoorTimingEstimateStillAllowsAMinimumAttemptWithinTheCandidate(int packetStride)
    {
        var minimum = FastSeekBudgetPolicy.CalculateRequestBytes(8 * MiB, packetStride,
            TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1), 1);
        var tail = FastSeekBudgetPolicy.CalculateRequestBytes(64 * 1024, packetStride,
            TimeSpan.Zero, TimeSpan.FromSeconds(1), 1);
        var overdue = FastSeekBudgetPolicy.CalculateRequestBytes(MiB, packetStride,
            TimeSpan.MinValue, TimeSpan.MaxValue, double.Epsilon);

        Assert.AreEqual(Align(512 * 1024, packetStride), minimum);
        Assert.AreEqual(Align(64 * 1024, packetStride), tail);
        Assert.AreEqual(minimum, overdue);
        Assert.AreEqual(0, FastSeekBudgetPolicy.CalculateRequestBytes(5 * packetStride - 1,
            packetStride, TimeSpan.FromSeconds(7), null, null));
        Assert.AreEqual(5 * packetStride, FastSeekBudgetPolicy.CalculateRequestBytes(5 * packetStride,
            packetStride, TimeSpan.Zero, TimeSpan.FromSeconds(1), 1));
    }

    [TestMethod]
    public void MissingOrInvalidNetworkObservationsPreserveTheCandidate()
    {
        double?[] absentOrInvalidBodyRates = { null, 0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity };
        foreach (var bodyBytesPerSecond in absentOrInvalidBodyRates)
            Assert.AreEqual(Align(8 * MiB, 192), FastSeekBudgetPolicy.CalculateRequestBytes(8 * MiB, 192,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), bodyBytesPerSecond));

        Assert.AreEqual(Align(8 * MiB, 192), FastSeekBudgetPolicy.CalculateRequestBytes(8 * MiB, 192,
            TimeSpan.FromSeconds(2), null, MiB));
        Assert.AreEqual(Align(8 * MiB, 192), FastSeekBudgetPolicy.CalculateRequestBytes(8 * MiB, 192,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(-1), MiB));
    }

    [TestMethod]
    public void ExtremeFiniteNetworkMeasurementsCannotOverflowTheRequestBound()
    {
        Assert.AreEqual(Align(8 * MiB, 204), FastSeekBudgetPolicy.CalculateRequestBytes(int.MaxValue, 204,
            TimeSpan.MaxValue, TimeSpan.Zero, double.MaxValue));
        Assert.AreEqual(Align(8 * MiB, 204), FastSeekBudgetPolicy.CalculateRequestBytes(int.MaxValue, 204,
            TimeSpan.FromSeconds(7), null, null));
        Assert.AreEqual(0, FastSeekBudgetPolicy.CalculateRequestBytes(0, 204,
            TimeSpan.FromSeconds(7), null, null));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FastSeekBudgetPolicy.CalculateRequestBytes(-1, 204,
            TimeSpan.FromSeconds(7), null, null));
    }

    private static int Align(int bytes, int packetStride) => bytes / packetStride * packetStride;
}
