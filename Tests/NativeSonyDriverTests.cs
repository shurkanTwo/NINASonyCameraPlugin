using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sony;
using Xunit;

public class NativeSonyDriverTests {
    private sealed class Camera {
        internal const uint Handle = 7;
        internal readonly SonyDriver Driver;
        internal uint StatusError;
        internal uint StartError;
        internal uint ExposureError = 50;
        internal uint PropertyError;
        internal Func<uint> Shutter = () => 0x00010002u;
        internal int StatusCalls;
        internal int StartCalls;
        internal int ExposureCalls;
        internal int PropertyCalls;
        internal int CloseCalls;

        internal Camera() {
            // Skip only the Windows DLL loader. Replace the P/Invoke delegates,
            // then exercise the actual SonyDriver and SonyMTPCameraDll methods.
            var dll = (SonyMTPCameraDll)RuntimeHelpers.GetUninitializedObject(typeof(SonyMTPCameraDll));
            SetField(dll, "fGetCaptureStatus", new SonyMTPCameraDll.dGetCaptureStatus((uint h, ref ImageInfo image) => {
                StatusCalls++;
                image.Status = 2;
                return StatusError;
            }));
            SetField(dll, "fStartCapture", new SonyMTPCameraDll.dStartCapture((uint h, ref ImageInfo image) => {
                StartCalls++;
                image.Status = 1;
                return StartError;
            }));
            SetField(dll, "fSetExposureTime", new SonyMTPCameraDll.dSetExposureTime((uint h, float seconds, ref PropertyValue value) => {
                ExposureCalls++;
                return ExposureError;
            }));
            SetField(dll, "fGetSinglePropertyValue", new SonyMTPCameraDll.dGetSinglePropertyValue((uint h, uint id, ref PropertyValue value) => {
                PropertyCalls++;
                value.Value = Shutter();
                return PropertyError;
            }));
            SetField(dll, "fCloseDevice", new SonyMTPCameraDll.dCloseDevice(h => { CloseCalls++; return 0; }));
            Driver = new SonyDriver(dll, new NativeCallExecutor());
            uint[] shutters = { 0, 0x001E0001, 0x00010001, 0x00040005, 0x00010002, 0x00010050, 0x00010064 };
            var properties = new List<Sony.PropertyInfo> {
                new Sony.PropertyInfo(new PropertyDescriptor { Id = 0xFFFF, ValueCount = (uint)shutters.Length },
                    shutters.Select(raw => new PropertyValueOption { Value = raw }).ToList())
            };
            SetField(Driver, "_camera", new SonyCameraInfo(Handle, new DeviceInfo(), new CameraInfo(), properties));
        }

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThreeNativeCaptureFailuresDisconnectAndBlockEveryLaterNativeEntry(bool starting) {
        var camera = new Camera { StatusError = 31, StartError = 1223 };
        Action operation = () => {
            if (starting) camera.Driver.StartCapture(Camera.Handle, 1);
            else camera.Driver.GetCaptureStatus(Camera.Handle);
        };
        for (int i = 0; i < 3; i++) {
            Assert.Throws<SonyException>(operation);
            Assert.Equal(i < 2, camera.Driver.IsOpen(Camera.Handle));
        }
        Assert.Null(camera.Driver.CameraInfo);
        Assert.Throws<InvalidOperationException>(operation);
        Assert.Throws<InvalidOperationException>(() => camera.Driver.GetProperty(Camera.Handle, 0xD20D));
        Assert.Throws<InvalidOperationException>(() => camera.Driver.CloseCamera(Camera.Handle));
        Assert.Throws<InvalidOperationException>(() => camera.Driver.OpenCamera("camera"));
        Assert.Throws<InvalidOperationException>(() => camera.Driver.Cameras());
        Assert.Throws<InvalidOperationException>(() => camera.Driver.Lenses());
        Assert.Equal(3, starting ? camera.StartCalls : camera.StatusCalls);
        Assert.Equal(0, camera.CloseCalls);
    }

    [Fact]
    public void ExposureSettingNativeFailuresAlsoTripTheLossGuard() {
        var camera = new Camera { ExposureError = 31 };
        for (int i = 0; i < 3; i++) Assert.Throws<SonyException>(() => camera.Driver.StartCapture(Camera.Handle, 1));
        Assert.False(camera.Driver.IsOpen(Camera.Handle));
        Assert.Equal(0, camera.StartCalls);
    }

    [Fact]
    public void ASuccessfulOperationResetsConsecutiveFailures() {
        var camera = new Camera { StatusError = 31 };
        for (int i = 0; i < 2; i++) Assert.Throws<SonyException>(() => camera.Driver.GetCaptureStatus(Camera.Handle));
        camera.StatusError = 0;
        Assert.Equal(2u, camera.Driver.GetCaptureStatus(Camera.Handle));
        camera.StatusError = 31;
        for (int i = 0; i < 2; i++) Assert.Throws<SonyException>(() => camera.Driver.GetCaptureStatus(Camera.Handle));
        Assert.True(camera.Driver.IsOpen(Camera.Handle));
        Assert.Throws<SonyException>(() => camera.Driver.GetCaptureStatus(Camera.Handle));
        Assert.False(camera.Driver.IsOpen(Camera.Handle));
    }

    [Fact]
    public void NeverSupportedPropertiesDoNotDisconnectTheCamera() {
        var camera = new Camera { PropertyError = 31 };
        for (int i = 0; i < 10; i++) Assert.Throws<SonyException>(() => camera.Driver.GetProperty(Camera.Handle, 0xD20D));
        Assert.True(camera.Driver.IsOpen(Camera.Handle));
        Assert.Equal(2u, camera.Driver.GetCaptureStatus(Camera.Handle));
    }

    [Fact]
    public void FailureOfAPreviouslyWorkingPropertyCountsTowardsLoss() {
        var camera = new Camera();
        camera.Driver.GetProperty(Camera.Handle, 0xD20D);
        camera.PropertyError = 31;
        for (int i = 0; i < 3; i++) Assert.Throws<SonyException>(() => camera.Driver.GetProperty(Camera.Handle, 0xD20D));
        Assert.False(camera.Driver.IsOpen(Camera.Handle));
        Assert.Equal(4, camera.PropertyCalls);
    }

    [Fact]
    public void ExplicitUnsupportedResultsAreExcludedEvenForAKnownProperty() {
        var camera = new Camera();
        camera.Driver.GetProperty(Camera.Handle, 0xD20D);
        camera.PropertyError = 50;
        for (int i = 0; i < 5; i++) Assert.Throws<SonyException>(() => camera.Driver.GetProperty(Camera.Handle, 0xD20D));
        Assert.True(camera.Driver.IsOpen(Camera.Handle));
    }

    [Fact]
    public void InvalidHandlesDoNotCountAsNativeFailures() {
        var camera = new Camera();
        for (int i = 0; i < 5; i++) Assert.Throws<SonyException>(() => camera.Driver.GetCaptureStatus(999));
        Assert.True(camera.Driver.IsOpen(Camera.Handle));
        Assert.Equal(0, camera.StatusCalls);
    }

    [Theory]
    [InlineData(0.011439f, 0x00010064u, 0x00010050u)]
    [InlineData(0.88f, 0x00010001u, 0x00040005u)]
    [InlineData(0.5f, 0u, 0x00010002u)]
    public void NativeStartWaitsForTheExpectedShutterIncludingOldBulb(float requested, uint stale, uint expected) {
        var camera = new Camera { ExposureError = 0 };
        camera.Shutter = () => camera.ExposureCalls == 1 ? stale : expected;
        camera.Driver.StartCapture(Camera.Handle, requested);
        Assert.Equal(2, camera.ExposureCalls);
        Assert.Equal(1, camera.StartCalls);
        Assert.True(camera.Driver.IsOpen(Camera.Handle));
    }

    [Fact]
    public void InvalidShutterReadbackDoesNotStartOrMarkTheCameraLost() {
        var camera = new Camera { ExposureError = 0, Shutter = () => uint.MaxValue };
        for (int i = 0; i < 4; i++) Assert.Throws<InvalidOperationException>(() => camera.Driver.StartCapture(Camera.Handle, 1));
        Assert.Equal(0, camera.StartCalls);
        Assert.True(camera.Driver.IsOpen(Camera.Handle));
    }
}
