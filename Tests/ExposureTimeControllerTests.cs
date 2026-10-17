using System;
using Sony;
using Xunit;

public class ExposureTimeControllerTests {
    private static readonly uint[] Shutters = { 0, 0x001E0001, 0x00040001, 0x00010001,
        0x00040005, 0x00030005, 0x00010002, 0x0001003C, 0x00010050, 0x00010064 };

    [Fact]
    public void BulbBoundaryUsesTheLearnedNumericShutterList() {
        uint[] learned = { 0, 0x00040001, 0x00010002 };
        Assert.True(ExposureTimeController.Apply(5, learned, () => 0, () => 0));
        Assert.Throws<InvalidOperationException>(() => ExposureTimeController.Apply(
            4.1, learned, () => 0, () => 0x00040001, timeoutMs: 0));
        Assert.Equal(0x00040001u, ExposureTimeController.SelectShutter(4.000001, learned));
    }

    [Fact]
    public void BulbRequestCannotAcceptAStaleNumericShutter() {
        Assert.Throws<InvalidOperationException>(() => ExposureTimeController.Apply(
            31, Shutters, () => 0, () => 0x001E0001u, timeoutMs: 0));
    }

    [Fact]
    public void BackToBackShutterChangeIsRetriedUntilItTakes() {
        int attempts = 0;
        Assert.True(ExposureTimeController.Apply(0.5, Shutters, () => { attempts++; return 0; },
            () => attempts < 3 ? 0x00010001u : 0x00010002u, pause: () => { }));
        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData(0.011439, 0x00010050u)]
    [InlineData(0.88, 0x00040005u)]
    [InlineData(7.0 / 480, 0x00010050u)]
    [InlineData(0.699, 0x00030005u)]
    [InlineData(0.9, 0x00040005u)]
    public void SelectionMatchesNativeRoundingAndNearestStep(double requested, uint expected) {
        Assert.Equal(expected, ExposureTimeController.SelectShutter(requested, Shutters));
        Assert.True(ExposureTimeController.Apply(requested, Shutters, () => 0, () => expected, timeoutMs: 0));
    }

    [Theory]
    [InlineData(0.011439, 0x00010064u, 0x00010050u)]
    [InlineData(0.88, 0x00010001u, 0x00040005u)]
    public void StaleAdjacentShutterIsRetriedDespiteBeingWithinTheOldRatio(double requested, uint stale, uint expected) {
        int attempts = 0;
        Assert.True(ExposureTimeController.Apply(requested, Shutters,
            () => { attempts++; return 0; }, () => attempts == 1 ? stale : expected, pause: () => { }));
        Assert.Equal(2, attempts);
        Assert.Throws<InvalidOperationException>(() => ExposureTimeController.Apply(
            requested, Shutters, () => 0, () => stale, timeoutMs: 0));
    }

    [Fact]
    public void OldBulbIsRetriedBeforeAShortExposure() {
        int attempts = 0;
        Assert.True(ExposureTimeController.Apply(0.5, Shutters, () => { attempts++; return 0; },
            () => attempts == 1 ? 0u : 0x00010002u, pause: () => { }));
        Assert.Equal(2, attempts);
        Assert.Throws<InvalidOperationException>(() => ExposureTimeController.Apply(
            0.5, Shutters, () => 0, () => 0, timeoutMs: 0));
    }

    [Fact]
    public void EquivalentRationalReadbackIsAccepted() {
        Assert.True(ExposureTimeController.Apply(0.5, Shutters, () => 0, () => 0x00020004u, timeoutMs: 0));
    }

    [Fact]
    public void APartialMoveDoesNotCountAsSuccessfulSettling() {
        int attempts = 0;
        Assert.Throws<SonyException>(() => ExposureTimeController.Apply(0.01, Shutters,
            () => ++attempts == 1 ? 0u : 31u, () => 0x00010002u, pause: () => { }));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void UnsupportedExposureControlRetainsManualFallback() {
        Assert.False(ExposureTimeController.Apply(1, null, () => 50, () => throw new Exception("No readback expected")));
        Assert.Throws<InvalidOperationException>(() => ExposureTimeController.Apply(
            1, null, () => 0, () => 0x00010001u));
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x00010000u)]
    [InlineData(0x00000001u)]
    public void MalformedNonzeroReadbackCannotStartExposures(uint raw) {
        Assert.Throws<InvalidOperationException>(() => ExposureTimeController.Apply(1, Shutters, () => 0, () => raw));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(120)]
    public void BulbExposureIsSupported(double requested) {
        Assert.True(ExposureTimeController.Apply(requested, Shutters, () => 0, () => 0));
    }
}
