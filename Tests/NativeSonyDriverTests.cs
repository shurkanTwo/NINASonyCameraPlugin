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
        internal bool OpenFails;
        internal readonly List<(uint, string)> AttachedLenses = new List<(uint, string)>();
        private readonly SonyMTPCameraDll dll;

        internal Camera() {
            // Skip only the Windows DLL loader. Replace the P/Invoke delegates,
            // then exercise the actual SonyDriver and SonyMTPCameraDll methods.
            dll = (SonyMTPCameraDll)RuntimeHelpers.GetUninitializedObject(typeof(SonyMTPCameraDll));
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
            SetField(dll, "fSetAttachedLens", new SonyMTPCameraDll.dSetAttachedLens((uint h, string lens) => {
                AttachedLenses.Add((h, lens));
                return 0;
            }));
            Driver = new SonyDriver(dll, new NativeCallExecutor());
            uint[] shutters = { 0, 0x001E0001, 0x00010001, 0x00040005, 0x00010002, 0x00010050, 0x00010064 };
            var properties = new List<Sony.PropertyInfo> {
                new Sony.PropertyInfo(new PropertyDescriptor { Id = 0xFFFF, ValueCount = (uint)shutters.Length },
                    shutters.Select(raw => new PropertyValueOption { Value = raw }).ToList())
            };
            SetField(Driver, "_camera", new SonyCameraInfo(Handle, new DeviceInfo(), new CameraInfo(), properties));
        }

        // Stub the native open path: an empty property list and default device/camera info.
        internal void UseOpenDevice(uint newHandle) {
            SetField(dll, "fOpenDevice", new SonyMTPCameraDll.dOpenDevice(path => OpenFails ? SonyMTPCameraDll.INVALID_HANDLE_VALUE : newHandle));
            SetField(dll, "fGetPropertyList", new SonyMTPCameraDll.dGetPropertyList((uint h, IntPtr list, ref uint size) => {
                size = 0;
                return list == IntPtr.Zero ? SonyMTPCameraDll.ERROR_RETRY : 0;
            }));
            SetField(dll, "fGetDeviceInfo", new SonyMTPCameraDll.dGetDeviceInfo((uint h, ref DeviceInfo info) => 0));
            SetField(dll, "fGetCameraInfo", new SonyMTPCameraDll.dGetCameraInfo((uint h, ref CameraInfo info, uint flags) => 0));
        }

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThreeNativeCaptureFailuresDropTheHandleWhichIsThenClosedExactlyOnce(bool starting) {
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
        // The lost handle no longer reaches the camera, but the driver is still usable.
        Assert.True(camera.Driver.CanReopen);
        Assert.Throws<SonyException>(operation);
        Assert.Throws<SonyException>(() => camera.Driver.GetProperty(Camera.Handle, 0xD20D));
        Assert.Equal(3, starting ? camera.StartCalls : camera.StatusCalls);
        Assert.Equal(0, camera.CloseCalls);
        // It is still released in the native driver, once, so a reconnect starts clean.
        camera.Driver.CloseCamera(Camera.Handle);
        Assert.Equal(1, camera.CloseCalls);
        Assert.Throws<SonyException>(() => camera.Driver.CloseCamera(Camera.Handle));
        Assert.Equal(1, camera.CloseCalls);
    }

    [Fact]
    public void ReopenReleasesTheOldHandleOnceOpensANewOneAndReattachesTheLens() {
        var camera = new Camera { StatusError = 31 };
        camera.UseOpenDevice(newHandle: 8);
        camera.Driver.SetAttachedLens(Camera.Handle, "lens-1");
        for (int i = 0; i < 3; i++) Assert.Throws<SonyException>(() => camera.Driver.GetCaptureStatus(Camera.Handle));
        Assert.True(camera.Driver.IsAwaitingReopen);

        camera.OpenFails = true;
        Assert.Throws<SonyException>(() => camera.Driver.ReopenCamera(Camera.Handle, "camera"));
        Assert.Equal(1, camera.CloseCalls);
        Assert.True(camera.Driver.IsAwaitingReopen);

        camera.OpenFails = false;
        var reopened = camera.Driver.ReopenCamera(Camera.Handle, "camera");
        Assert.Equal(8u, reopened.Handle);
        Assert.True(camera.Driver.IsOpen(8));
        Assert.False(camera.Driver.IsAwaitingReopen);
        Assert.Equal(1, camera.CloseCalls);
        Assert.Equal(new[] { (Camera.Handle, "lens-1"), (8u, "lens-1") }, camera.AttachedLenses);

        camera.Driver.CloseCamera(8);
        Assert.Equal(2, camera.CloseCalls);
        Assert.False(camera.Driver.IsAwaitingReopen);
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
