using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;

namespace Sony {
    // Adapted from @totoantibes's PR #13: camera and focuser share one native
    // entry point. A timeout permanently blocks further native calls, including
    // close/open/enumeration, because the old P/Invoke cannot be interrupted.
    // Repeated native errors (e.g. USB unplugged) only drop the handle: the
    // driver still answers, so the camera can be reopened without a restart.
    public class SonyDriver {
        private static readonly Lazy<SonyDriver> _instance = new Lazy<SonyDriver>(() => new SonyDriver());
        private static readonly NativeCallExecutor sharedCalls = new NativeCallExecutor();
        private readonly NativeCallExecutor calls;
        private readonly SonyMTPCameraDll _sonydll;
        private volatile SonyCameraInfo _camera;
        // Handle dropped after repeated native failures but not yet closed in the
        // native driver; it must be closed once so a reopen does not reuse it.
        private uint lostHandle;
        private string attachedLensPath;
        private int timeoutNotified;
        private int consecutiveFailures;
        private readonly HashSet<uint> knownGoodProperties = new HashSet<uint>();
        private const int LostCameraFailureThreshold = 3;
        // The native close waits up to 20 s for an in-progress camera operation.
        private const int CloseTimeoutMs = 30000;
        private const int OpenTimeoutMs = 60000;

        public static SonyDriver GetInstance() => _instance.Value;

        public SonyDriver() : this(new SonyMTPCameraDll(), sharedCalls) { }

        internal SonyDriver(SonyMTPCameraDll dll, NativeCallExecutor executor) {
            _sonydll = dll;
            calls = executor;
        }

        private T Invoke<T>(string operation, Func<T> action, int timeoutMs = 30000, int lockTimeoutMs = 30000) {
            try {
                return calls.Invoke(action, timeoutMs, lockTimeoutMs);
            } catch (Exception ex) {
                if (calls.IsUnavailable && Interlocked.Exchange(ref timeoutNotified, 1) == 0) {
                    Logger.Error($"Sony driver stopped responding while trying to {operation}. Restart NINA and power-cycle the camera before reconnecting.", ex);
                    try {
                        Notification.ShowError("Sony driver stopped responding. Restart NINA and power-cycle the camera before reconnecting.");
                    } catch (Exception notificationError) {
                        Logger.Warning($"Could not show Sony driver timeout notification: {notificationError.Message}");
                    }
                }
                throw;
            }
        }

        private T InvokeCamera<T>(uint handle, string operation, Func<T> action, int timeoutMs = 30000,
            int lockTimeoutMs = 30000, uint? propertyId = null) {
            return Invoke(operation, () => {
                if (_camera == null || _camera.Handle != handle) {
                    throw new SonyException($"Cannot {operation}: the camera handle is no longer open.");
                }
                try {
                    T result = action();
                    consecutiveFailures = 0;
                    if (propertyId.HasValue) knownGoodProperties.Add(propertyId.Value);
                    return result;
                } catch (SonyException ex) when (ex.ErrorCode.HasValue && ex.ErrorCode != 50 &&
                    (!propertyId.HasValue || knownGoodProperties.Contains(propertyId.Value))) {
                    // Count only returned native errors. An unsupported property,
                    // verification mismatch or lock wait is not a lost device.
                    if (++consecutiveFailures >= LostCameraFailureThreshold) {
                        // The driver answered, so this is not a hang: stop using the
                        // handle, but leave it to be closed and reopened.
                        lostHandle = handle;
                        _camera = null;
                        _sonydll.DiscardLastImage();
                        Logger.Error($"Sony camera failed {consecutiveFailures} consecutive native operations while trying to {operation}. Treating it as disconnected until it is reopened.", ex);
                    }
                    throw;
                }
            }, timeoutMs, lockTimeoutMs);
        }

        public IEnumerable<SonyDevice> Cameras() => Invoke("enumerate cameras", () => {
            var devices = new List<SonyDevice>();
            uint count = _sonydll.GetSupportedDeviceCount();
            for (uint i = 0; i < count; i++) devices.Add(new SonyDevice(_sonydll.GetSupportedDeviceInfo(i)));
            return devices;
        });

        public SonyCameraInfo CameraInfo => calls.IsUnavailable ? null : _camera;
        public bool IsOpen(uint handle) {
            SonyCameraInfo camera = _camera;
            return !calls.IsUnavailable && camera != null && camera.Handle == handle;
        }

        // False only once a native call has hung; then nothing can be reopened until NINA restarts.
        public bool CanReopen => !calls.IsUnavailable;

        public SonyCameraInfo OpenCamera(string path) => Invoke("open camera", () => {
            if (lostHandle != 0) ReleaseHandle(lostHandle);
            return OpenHandle(path);
        }, timeoutMs: OpenTimeoutMs);

        // Close the given handle (if still held, open or lost) and open the camera
        // again, restoring the attached lens so the focuser keeps working.
        public SonyCameraInfo ReopenCamera(uint handle, string path) => Invoke("reopen camera", () => {
            ReleaseHandle(handle);
            if (lostHandle != 0) ReleaseHandle(lostHandle);
            SonyCameraInfo camera = OpenHandle(path);
            if (attachedLensPath != null) {
                try {
                    _sonydll.SetAttachedLens(camera.Handle, attachedLensPath);
                } catch (Exception ex) {
                    Logger.Warning($"Reopened the Sony camera but could not re-attach lens '{attachedLensPath}': {ex.Message}");
                }
            }
            return camera;
        }, timeoutMs: CloseTimeoutMs + OpenTimeoutMs);

        public void CloseCamera(uint handle) {
            Invoke("close camera", () => {
                // A deliberate close ends any pending reopen, even if the handle was already lost.
                attachedLensPath = null;
                if (!ReleaseHandle(handle)) {
                    throw new SonyException("Cannot close camera: the camera handle is no longer open.");
                }
                return true;
            }, timeoutMs: CloseTimeoutMs, lockTimeoutMs: 5000);
        }

        // The camera was lost (not closed) with a lens attached and can still be reopened,
        // so the focuser should stay connected and pick up the new handle afterwards.
        public bool IsAwaitingReopen => CanReopen && _camera == null && attachedLensPath != null;

        // Runs inside Invoke. Closes a handle this driver still holds exactly once;
        // returns false if it was never ours or has already been closed.
        private bool ReleaseHandle(uint handle) {
            if (_camera != null && _camera.Handle == handle) {
                _camera = null;
            } else if (lostHandle != 0 && lostHandle == handle) {
                lostHandle = 0;
            } else {
                return false;
            }
            _sonydll.DiscardLastImage();
            _sonydll.CloseDevice(handle);
            return true;
        }

        // Runs inside Invoke.
        private SonyCameraInfo OpenHandle(string path) {
            if (_camera != null) throw new SonyException("A Sony camera is already open. Disconnect it before opening another camera; restart NINA if the earlier disconnect timed out.");
            uint handle = _sonydll.OpenDevice(path);
            if (handle == 0 || handle == SonyMTPCameraDll.INVALID_HANDLE_VALUE) throw new SonyException("Unable to open the Sony camera.");
            try {
                var properties = new List<PropertyInfo>();
                foreach (PropertyDescriptor descriptor in _sonydll.GetPropertyDescriptors(handle)) {
                    List<PropertyValueOption> options = descriptor.ValueCount > 0
                        ? _sonydll.GetPropertyValueOptions(handle, descriptor) : null;
                    properties.Add(new PropertyInfo(descriptor, options));
                }
                _sonydll.DiscardLastImage();
                var camera = new SonyCameraInfo(handle, _sonydll.GetDeviceInfo(handle), _sonydll.GetCameraInfo(handle, 0), properties);
                // Even a late successful open stays inaccessible after timeout:
                // CameraInfo, IsOpen and every native entry check the sticky gate.
                _camera = camera;
                consecutiveFailures = 0;
                knownGoodProperties.Clear();
                return camera;
            } catch {
                // Cleanup remains inside the same bounded operation. If this
                // blocks, the executor stays unavailable until NINA restarts.
                _sonydll.CloseDevice(handle);
                throw;
            }
        }

        public PropertyValue GetProperty(uint handle, uint propertyId) =>
            InvokeCamera(handle, "read camera property", () => _sonydll.GetSinglePropertyValue(handle, propertyId), lockTimeoutMs: 500, propertyId: propertyId);
        public void SetProperty(uint handle, uint propertyId, uint value) =>
            InvokeCamera(handle, "set camera property", () => { _sonydll.SetPropertyValue(handle, propertyId, value); return true; }, propertyId: propertyId);
        public byte[] GetLiveView(uint handle) => InvokeCamera(handle, "download LiveView", () => _sonydll.GetPreviewImage(handle));
        public uint GetCaptureStatus(uint handle) => InvokeCamera(handle, "read capture status", () => _sonydll.GetCaptureStatus(handle));

        public void StartCapture(uint handle, float exposureTime) {
            InvokeCamera(handle, "start capture", () => {
                _sonydll.DiscardLastImage();
                var shutterSpeeds = _camera.GetPropertyInfo(0xFFFF)?.Options()?.Select(option => option.Value).ToArray();
                return _sonydll.StartCapture(handle, exposureTime, shutterSpeeds);
            }, timeoutMs: 60000);
        }

        public void CancelCapture(uint handle) => InvokeCamera(handle, "cancel capture", () => { _sonydll.CancelCapture(handle); return true; });
        public byte[] GetLastImage() => _sonydll.TakeLastImage()
            ?? throw new SonyException("No completed Sony image is available to download.");

        public IEnumerable<SonyLens> Lenses() => Invoke("enumerate lenses", () => {
            var lenses = new List<SonyLens>();
            int count = _sonydll.GetLensCount();
            for (uint i = 0; i < count; i++) lenses.Add(new SonyLens(_sonydll.GetLensInfo(i)));
            return lenses;
        });

        public bool TryGetFocusPosition(uint handle, int timeoutMs, out uint position) {
            position = 0;
            try {
                position = InvokeCamera(handle, "read focus position", () => _sonydll.GetFocusPosition(handle), lockTimeoutMs: timeoutMs);
                return true;
            } catch (TimeoutException) { return false; }
        }

        public bool TryGetFocusLimit(uint handle, int timeoutMs, out uint limit) {
            limit = 0;
            try {
                limit = InvokeCamera(handle, "read focus limit", () => _sonydll.GetFocusLimit(handle), lockTimeoutMs: timeoutMs);
                return true;
            } catch (TimeoutException) { return false; }
        }

        public void SetAttachedLens(uint handle, string path) =>
            InvokeCamera(handle, "attach lens", () => { _sonydll.SetAttachedLens(handle, path); attachedLensPath = path; return true; });
        public void SetFocusPosition(uint handle, uint position) =>
            InvokeCamera(handle, "move focus", () => { _sonydll.SetFocusPosition(handle, position); return true; });
        public uint GetFocusPosition(uint handle) =>
            InvokeCamera(handle, "read focus position", () => _sonydll.GetFocusPosition(handle), lockTimeoutMs: 500);
        public uint GetFocusLimit(uint handle) =>
            InvokeCamera(handle, "read focus limit", () => _sonydll.GetFocusLimit(handle), lockTimeoutMs: 500);
    }
}
