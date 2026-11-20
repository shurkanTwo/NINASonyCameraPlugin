using System.Collections.Generic;
using Sony;

namespace NINA.RetroKiwi.Plugin.SonyCamera.Drivers {
    public interface ISonyCameraBackend {
        IEnumerable<SonyDevice> Cameras();

        /// <summary>
        /// Lets a driver that stopped responding be used again, when it is safe
        /// to. False means only a restart will clear it. Connect asks for this
        /// before opening, so a camera that came back is reachable again.
        /// </summary>
        bool TryResumeAfterFailure();

        SonyCameraInfo OpenCamera(string id);
        void CloseCamera(uint handle);
        bool IsOpen(uint handle);
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
