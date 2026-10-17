using System;
using System.Collections.Generic;
using System.Threading;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;

namespace Sony {
    // Adapted from Robert Hasson's PR #13: camera and focuser share one native
    // entry point. A timeout permanently blocks further native calls, including
    // close/open/enumeration, because the old P/Invoke cannot be interrupted.
    public class SonyDriver {
        private static readonly Lazy<SonyDriver> _instance = new Lazy<SonyDriver>(() => new SonyDriver());
        private static readonly NativeCallExecutor calls = new NativeCallExecutor();
        private readonly SonyMTPCameraDll _sonydll;
        private volatile SonyCameraInfo _camera;
        private int timeoutNotified;

        public static SonyDriver GetInstance() => _instance.Value;

        public SonyDriver() {
            _sonydll = new SonyMTPCameraDll();
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

        private T InvokeCamera<T>(uint handle, string operation, Func<T> action, int timeoutMs = 30000, int lockTimeoutMs = 30000) {
            return Invoke(operation, () => {
                if (_camera == null || _camera.Handle != handle) {
                    throw new SonyException($"Cannot {operation}: the camera handle is no longer open.");
                }
                return action();
            }, timeoutMs, lockTimeoutMs);
        }

        public IEnumerable<SonyDevice> Cameras() => Invoke("enumerate cameras", () => {
            var devices = new List<SonyDevice>();
            uint count = _sonydll.GetSupportedDeviceCount();
            for (uint i = 0; i < count; i++) devices.Add(new SonyDevice(_sonydll.GetSupportedDeviceInfo(i)));
            return devices;
        });

        public SonyCameraInfo CameraInfo => calls.IsUnavailable ? null : _camera;
        public bool IsOpen(uint handle) => !calls.IsUnavailable && _camera != null && _camera.Handle == handle;

        public SonyCameraInfo OpenCamera(string path) => Invoke("open camera", () => {
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
                return camera;
            } catch {
                // Cleanup remains inside the same bounded operation. If this
                // blocks, the executor stays unavailable until NINA restarts.
                _sonydll.CloseDevice(handle);
                throw;
            }
        }, timeoutMs: 60000);

        public void CloseCamera(uint handle) {
            InvokeCamera(handle, "close camera", () => {
                _camera = null;
                _sonydll.DiscardLastImage();
                _sonydll.CloseDevice(handle);
                return true;
            }, timeoutMs: 5000, lockTimeoutMs: 5000);
        }

        public PropertyValue GetProperty(uint handle, uint propertyId) =>
            InvokeCamera(handle, "read camera property", () => _sonydll.GetSinglePropertyValue(handle, propertyId), lockTimeoutMs: 500);
        public void SetProperty(uint handle, uint propertyId, uint value) =>
            InvokeCamera(handle, "set camera property", () => { _sonydll.SetPropertyValue(handle, propertyId, value); return true; });
        public byte[] GetLiveView(uint handle) => InvokeCamera(handle, "download LiveView", () => _sonydll.GetPreviewImage(handle));
        public uint GetCaptureStatus(uint handle) => InvokeCamera(handle, "read capture status", () => _sonydll.GetCaptureStatus(handle));

        public void StartCapture(uint handle, float exposureTime) {
            InvokeCamera(handle, "start capture", () => {
                _sonydll.DiscardLastImage();
                return _sonydll.StartCapture(handle, exposureTime);
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
            InvokeCamera(handle, "attach lens", () => { _sonydll.SetAttachedLens(handle, path); return true; });
        public void SetFocusPosition(uint handle, uint position) =>
            InvokeCamera(handle, "move focus", () => { _sonydll.SetFocusPosition(handle, position); return true; });
        public uint GetFocusPosition(uint handle) =>
            InvokeCamera(handle, "read focus position", () => _sonydll.GetFocusPosition(handle), lockTimeoutMs: 500);
        public uint GetFocusLimit(uint handle) =>
            InvokeCamera(handle, "read focus limit", () => _sonydll.GetFocusLimit(handle), lockTimeoutMs: 500);
    }
}
