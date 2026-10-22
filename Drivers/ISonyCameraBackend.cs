using System.Collections.Generic;
using Sony;

namespace NINA.RetroKiwi.Plugin.SonyCamera.Drivers {
    public interface ISonyCameraBackend {
        IEnumerable<SonyDevice> Cameras();
        SonyCameraInfo OpenCamera(string id);
        // Close the handle if still held and open the camera again (e.g. after USB was unplugged).
        SonyCameraInfo ReopenCamera(uint handle, string id);
        void CloseCamera(uint handle);
        bool IsOpen(uint handle);
        // False once the backend can no longer open anything (a native call hung).
        bool CanReopen { get; }
        PropertyValue GetProperty(uint handle, uint propertyId);
        void SetProperty(uint handle, uint propertyId, uint value);
        byte[] GetLiveView(uint handle);
        uint GetCaptureStatus(uint handle);
        void StartCapture(uint handle, float exposureTime);
        void CancelCapture(uint handle);
        SonyExposureFrame GetLastImage();
    }

    public sealed class SonyExposureFrame {
        public byte[] RawBytes { get; init; }
        public ushort[] Pixels { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int BitDepth { get; init; }
    }
}
