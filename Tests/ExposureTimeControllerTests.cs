using System;
using Sony;
using Xunit;

public class ExposureTimeControllerTests {
    [Fact]
    public void BulbBoundaryUsesTheLearnedNumericShutterList() {
        double longest = ExposureTimeController.LongestNonBulb(new uint[] { 0, 0x00040001, 0x00010002 });
        Assert.Equal(4, longest);
        Assert.True(ExposureTimeController.Apply(5, () => 0, () => 0, longestNonBulb: longest));
        Assert.Throws<InvalidOperationException>(() => ExposureTimeController.Apply(
            4.1, () => 0, () => 0x00040001, timeoutMs: 0, longestNonBulb: longest));
    }

    [Fact]
    public void BulbRequestCannotAcceptAStaleNumericShutter() {
        Assert.Throws<InvalidOperationException>(() => ExposureTimeController.Apply(
            31, () => 0, () => 0x001E0001u, timeoutMs: 0));
    }

    [Fact]
    public void BackToBackShutterChangeIsRetriedUntilItTakes() {
        int attempts = 0;
        Assert.True(ExposureTimeController.Apply(0.5, () => { attempts++; return 0; },
            () => attempts < 3 ? 0x00010001u : 0x00010002u, pause: () => { }));
        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData(1.0 / 80, 7.0 / 480)]
    [InlineData(0.6, 0.699)]
    public void NearestSupportedShutterIsAcceptedAcrossIrregularSteps(double actual, double requested) {
        uint raw = actual < 0.1 ? 0x00010050u : 0x0006000Au;
        Assert.True(ExposureTimeController.Apply(requested, () => 0, () => raw, timeoutMs: 0));
    }

    [Fact]
    public void StalledShutterFailsInsteadOfExposingAtTheOldSpeed() {
        var error = Assert.Throws<InvalidOperationException>(() => ExposureTimeController.Apply(
            0.5, () => 0, () => 0x00010001, timeoutMs: 0));
        Assert.Contains("Capture was not started", error.Message);
    }

    [Fact]
    public void APartialMoveDoesNotCountAsSuccessfulSettling() {
        int attempts = 0;
        Assert.Throws<InvalidOperationException>(() => ExposureTimeController.Apply(0.01,
            () => ++attempts == 1 ? 0u : 31u, () => 0x00010002u, pause: () => { }));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void UnsupportedExposureControlIsExplicit() {
        Assert.False(ExposureTimeController.Apply(1, () => 50, () => throw new Exception("No readback expected")));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0xFFFFFFFFu)]
    public void InvalidOrUnexpectedBulbReadbackCannotStartShortExposures(uint raw) {
        Assert.Throws<InvalidOperationException>(() => ExposureTimeController.Apply(1, () => 0, () => raw));
    }

    [Fact]
    public void LongBulbExposureIsSupported() {
        Assert.True(ExposureTimeController.Apply(120, () => 0, () => 0));
    }
}
