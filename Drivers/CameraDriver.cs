using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using FTD2XX_NET;
using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Exceptions;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Equipment.SDK.CameraSDKs.ASTPANSDK;
using NINA.Equipment.Utility;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using Sony;

namespace NINA.RetroKiwi.Plugin.SonyCamera.Drivers {
    public class CameraDriver : BaseINPC, ICamera {
        // Some camera settings we are interested in
        private const uint PROPID_BATTERY = 53784;
        private const uint PROPID_ISO = 0xD21E; // Actual ISO currently set
        private const uint PROPID_ISOS = 0xFFFE; // Registry-backed list of learnt ISOs (may be empty until learnt)

        private readonly IPluginOptionsAccessor _pluginSettings;
        private readonly ISonyCameraBackend _driver;
        private readonly Action<string> _showWarning;
        private readonly CaptureController _capture;
        private readonly object connectionLock = new object();

        private int _lastKnownGain = -1;
        private int _lastKnownBatteryLevel;
        private volatile SonyCameraInfo _camera = null;
        // Set when an exposure fails or the camera stops answering (e.g. USB unplugged);
        // the next exposure reopens the camera first so an unattended sequence can carry on.
        private volatile bool _reopenNeeded;
        private volatile bool _reopening;
        // How long an exposure waits for an unplugged camera to come back before failing.
        // Failing at once would burn through the rest of a sequence in seconds.
        private readonly TimeSpan _reopenWait;
        private static readonly TimeSpan DefaultReopenWait = TimeSpan.FromMinutes(2);
        // Each failed attempt (camera not back yet) already takes about a second.
        private const int ReopenRetryIntervalMs = 1000;
        private const int ReopenWaitPollMs = 250;
        private volatile bool _stopReopenWait;
        private int _requestedGain = -1;
        private const int GainRestoreAttempts = 3;
        private const int IsoReadingWaitMs = 5000;
        private const int IsoReadingPollMs = 250;
        // NINA calls AbortExposure to clean up after a failed exposure too; that is not a
        // user abort, and there is no capture left to wait for, so it must not warn.
        private volatile bool _exposureFailed;
        private volatile bool _starting;
        // An exposure that has not finished after twice its length (Sony long-exposure noise
        // reduction doubles it) plus this margin is treated as lost, so a camera stuck after a
        // USB drop cannot hang the sequence.
        private readonly TimeSpan _exposureTimeoutMargin;
        private static readonly TimeSpan DefaultExposureTimeoutMargin = TimeSpan.FromMinutes(2);
        private double _currentExposureSeconds;
        // When the camera is still finishing an earlier exposure (e.g. the sequence was stopped
        // and restarted), wait for it instead of failing: failing at once lets a "continue on
        // error" sequence run through all of its exposures in a fraction of a second.
        // Null: as long as that exposure could still take; zero: do not wait.
        private readonly TimeSpan? _busyWaitLimit;
        private const int BusyPollMs = 250;
        private double _lastCameraTemperature = double.NaN;
        private SonyDevice _device = null;
        private IProfileService _profileService;
        private readonly IExposureDataFactory _exposureDataFactory;
        private bool _liveViewEnabled;
        private short _readoutModeForSnapImages;
        private short _readoutModeForNormalImages;
        private AsyncObservableCollection<BinningMode> _binningModes;

        public CameraDriver(IProfileService profileService, IExposureDataFactory exposureDataFactory, SonyDevice device, IPluginOptionsAccessor pluginSettings, ISonyCameraBackend driver = null, Action<string> showWarning = null, TimeSpan? reopenWait = null, TimeSpan? exposureTimeoutMargin = null, TimeSpan? busyWaitLimit = null) {
            _reopenWait = reopenWait ?? DefaultReopenWait;
            _exposureTimeoutMargin = exposureTimeoutMargin ?? DefaultExposureTimeoutMargin;
            _busyWaitLimit = busyWaitLimit;
            _profileService = profileService;
            _exposureDataFactory = exposureDataFactory;
            _device = device;
            _pluginSettings = pluginSettings;
            _driver = driver ?? new NativeSonyCameraBackend();
            _showWarning = showWarning ?? Notification.ShowWarning;
            _capture = new CaptureController(
                () => {
                    TryGetCaptureStatus(_driver, out var status, "capture status");
                    return status;
                },
                exposureTime => _driver.StartCapture(_camera.Handle, exposureTime),
                TryNativeCancel,
                () => NativeCancelEnabled);
        }

        #region Internal Helpers

        private PropertyValue GetPropertyValue(uint id) {
            return _driver.GetProperty(_camera.Handle, id);
        }

        // NINA's Equipment > Camera "Default gain" dropdown binds straight to Gains. Handing it a
        // new list on every read (and on every Gains change notification) made WPF rebuild the
        // dropdown and blank the selection, so keep one list per opened camera, and the same list
        // instance across a reopen whose ISO options are unchanged.
        private SonyCameraInfo _isoOptionsCamera;
        private IReadOnlyList<PropertyValueOption> _isoOptions = Array.Empty<PropertyValueOption>();
        private IList<int> _gains = new List<int>();

        private IReadOnlyList<PropertyValueOption> GetAvailableIsoOptions() {
            SonyCameraInfo camera = _camera;
            if (camera == null) {
                return Array.Empty<PropertyValueOption>();
            }
            if (ReferenceEquals(camera, _isoOptionsCamera)) {
                return _isoOptions;
            }

            uint[] propertyCandidates = { PROPID_ISOS, PROPID_ISO };

            foreach (var propertyId in propertyCandidates) {
                try {
                    var options = camera.GetPropertyInfo(propertyId)?.Options()?.Where(o => o.Value > 0 && o.Value < 0x00FFFFFF)
                        .GroupBy(o => o.Value).Select(group => group.First()).OrderBy(o => o.Value).ToList();
                    if (options != null && options.Count > 0) {
                        var gains = options.Select(option => (int)option.Value).ToList();
                        if (!gains.SequenceEqual(_gains)) {
                            _gains = gains;
                        }
                        _isoOptions = options;
                        _isoOptionsCamera = camera;
                        return options;
                    }
                } catch (Exception ex) {
                    Logger.Warning($"Unable to enumerate ISO options for property 0x{propertyId:X}: {ex.Message}");
                }
            }

            Logger.Warning("Camera did not report any ISO options via known properties (registry ISO list may be empty until the camera learns it).");
            return Array.Empty<PropertyValueOption>();
        }

        private void NotifyGainPropertiesChanged() {
            RaisePropertyChanged(nameof(CanGetGain));
            RaisePropertyChanged(nameof(CanSetGain));
            RaisePropertyChanged(nameof(GainMin));
            RaisePropertyChanged(nameof(GainMax));
            RaisePropertyChanged(nameof(Gain));
            RaisePropertyChanged(nameof(Gains));
        }

        private bool NativeCancelEnabled {
            get {
                try {
                    var raw = _pluginSettings.GetValueString(nameof(SonyCamera.EnableNativeCancel), bool.FalseString);
                    return bool.TryParse(raw, out var enabled) && enabled;
                } catch (Exception ex) {
                    Logger.Warning($"EnableNativeCancel read failed; defaulting to false. {ex.Message}");
                    return false;
                }
            }
        }

        private bool TryNativeCancel() {
            try {
                _driver.CancelCapture(_camera.Handle);
                return true;
            } catch (Exception ex) {
                Logger.Error("CancelCapture failed; letting capture finish.", ex);
                return false;
            }
        }

        private bool TryGetCaptureStatus(ISonyCameraBackend driver, out uint status, string reason) {
            try {
                status = driver.GetCaptureStatus(_camera.Handle);
                return true;
            } catch (Exception ex) {
                Logger.Warning($"Unable to get capture status ({reason}); camera may not have responded: {ex.Message}.");
                status = CaptureController.StatusUnknown;
                _reopenNeeded = true;
                return false;
            }
        }

        // Close the unresponsive handle and open the camera again.
        private bool TryReopenCamera(bool firstAttempt) {
            bool reopened = false;
            lock (connectionLock) {
                SonyCameraInfo previous = _camera;
                if (previous == null) {
                    return false;
                }

                _reopening = true;
                try {
                    if (firstAttempt) Logger.Warning("The Sony camera stopped responding; reopening it before the next exposure.");
                    _camera = _driver.ReopenCamera(previous.Handle, _device.Id)
                        ?? throw new SonyException("Reopening the Sony camera returned no camera.");
                    _reopenNeeded = false;
                    reopened = true;
                } catch (Exception ex) {
                    // Expected every few seconds while the camera is unplugged; only log the first.
                    if (firstAttempt) Logger.Warning($"Unable to reopen the Sony camera yet; waiting for it to come back. {ex.Message}");
                    else Logger.Debug($"Sony camera still not reopened: {ex.Message}");
                } finally {
                    _reopening = false;
                }
            }

            if (reopened) {
                Logger.Info("Sony camera reopened.");
                RestoreRequestedGain();
                _showWarning("The Sony camera stopped responding and was reconnected automatically.");
                NotifyGainPropertiesChanged();
            }
            return reopened;
        }

        // Retry the reopen until the camera is back, the wait runs out, or the exposure is aborted.
        // A brief USB drop then just delays this exposure instead of failing it.
        private bool WaitForReopen() {
            _stopReopenWait = false;
            var waited = Stopwatch.StartNew();
            for (int attempt = 0; ; attempt++) {
                if (TryReopenCamera(firstAttempt: attempt == 0)) {
                    return true;
                }

                var nextAttempt = waited.Elapsed + TimeSpan.FromMilliseconds(ReopenRetryIntervalMs);
                while (waited.Elapsed < nextAttempt) {
                    if (_stopReopenWait || _camera == null || waited.Elapsed >= _reopenWait) {
                        return false;
                    }
                    Thread.Sleep(ReopenWaitPollMs);
                }
            }
        }

        // Returns false if the wait was ended by an abort or disconnect.
        private bool WaitWhileCameraBusy() {
            // The previous exposure can take at most as long as its own timeout.
            TimeSpan limit = _busyWaitLimit ??
                TimeSpan.FromSeconds(Math.Max(0, _currentExposureSeconds) * 2) + _exposureTimeoutMargin;
            if (limit <= TimeSpan.Zero) {
                return true;
            }

            _stopReopenWait = false;
            var waited = Stopwatch.StartNew();
            bool logged = false;
            while (waited.Elapsed < limit && _camera != null) {
                if (_stopReopenWait) {
                    return false;
                }
                if (!TryGetCaptureStatus(_driver, out uint status, "waiting for previous exposure") || !CaptureController.IsBusy(status)) {
                    return true;
                }
                if (!logged) {
                    Logger.Info("The Sony camera is still finishing the previous exposure; waiting for it before starting the next one.");
                    logged = true;
                }
                Thread.Sleep(BusyPollMs);
            }
            return true;
        }

        // NINA sets the ISO before the exposure, which happens while the camera is still away;
        // apply it to the reopened camera. Sony ISO changes are relative steps from the value the
        // driver last read, and a just-reopened camera reports 0 until its settings arrive -
        // stepping from that left the wrong ISO. So wait for a real reading, then set and verify.
        private void RestoreRequestedGain() {
            int gain = _requestedGain;
            SonyCameraInfo camera = _camera;
            if (gain <= 0 || camera == null) {
                return;
            }

            uint current = 0;
            for (int attempt = 0; attempt < GainRestoreAttempts; attempt++) {
                current = WaitForIsoReading(camera.Handle);
                if (current == (uint)gain) {
                    Logger.Info($"Restored ISO {gain} on the reopened Sony camera.");
                    _lastKnownGain = gain;
                    return;
                }
                if (current == 0) {
                    break;
                }
                try {
                    _driver.SetProperty(camera.Handle, PROPID_ISO, (uint)gain);
                } catch (Exception ex) {
                    Logger.Warning($"Setting ISO {gain} on the reopened Sony camera failed: {ex.Message}");
                }
            }

            current = WaitForIsoReading(camera.Handle);
            if (current == (uint)gain) {
                Logger.Info($"Restored ISO {gain} on the reopened Sony camera.");
                _lastKnownGain = gain;
            } else {
                Logger.Warning($"Could not restore ISO {gain} on the reopened Sony camera; it reports {(current == 0 ? "no ISO yet" : current.ToString())}.");
            }
        }

        // Returns the camera's ISO once it reports one (0 = not read yet), or 0 if it does not in time.
        private uint WaitForIsoReading(uint handle) {
            var waited = Stopwatch.StartNew();
            while (true) {
                try {
                    uint value = GetIsoValue(handle);
                    if (value != 0) {
                        return value;
                    }
                } catch (Exception ex) {
                    Logger.Debug($"Waiting for the reopened Sony camera to report its ISO: {ex.Message}");
                }
                if (waited.ElapsedMilliseconds >= IsoReadingWaitMs) {
                    return 0;
                }
                Thread.Sleep(IsoReadingPollMs);
            }
        }

        private uint GetIsoValue(uint handle) => _driver.GetProperty(handle, PROPID_ISO).Value;

        #endregion

        #region Supported Properties

        public bool HasShutter => true;

        // Last downloaded ARW's camera reading; unavailable before the first
        // photo or when the current image does not contain a valid reading.
        public double Temperature => _camera == null ? double.NaN : Volatile.Read(ref _lastCameraTemperature);

        public short BinX { get => 1; set => throw new NotImplementedException(); }
        public short BinY { get => 1; set => throw new NotImplementedException(); }

        public string SensorName {
            get {
                if (_camera != null) {
                    return _camera.SensorName;
                } else {
                    return string.Empty;
                }
            }
        }

        public SensorType SensorType { get => SensorType.RGGB; set => throw new NotImplementedException(); }

        public short BayerOffsetX { get => 1; set => throw new NotImplementedException(); }

        public short BayerOffsetY { get => 1; set => throw new NotImplementedException(); }

        public int CameraXSize {
            get {
                if (_camera != null) {
                    return _camera.ImageSize.Width;
                }
                else {
                    return 0;
                }
            }
        }

        public int CameraYSize {
            get {
                if (_camera != null) {
                    return _camera.ImageSize.Height;
                } else {
                    return 0;
                }
            }
        }

        public double ExposureMin {
            get {
                if (_camera != null) {
                    return _camera.ExposureMin;
                } else {
                    return double.NaN;
                }
            }
        }

        public double ExposureMax {
            get {
                if (_camera != null) {
                    return _camera.ExposureMax;
                } else {
                    return double.NaN;
                }
            }
        }

        public short MaxBinX { get => 1; set => throw new NotImplementedException(); }

        public short MaxBinY { get => 1; set => throw new NotImplementedException(); }

        public double PixelSizeX {
            get {
                if (_camera != null) {
                    return _camera.PixelWidth;
                } else {
                    return double.NaN;
                }
            }
        }

        public double PixelSizeY {
            get {
                if (_camera != null) {
                    return _camera.PixelHeight;
                } else {
                    return double.NaN;
                }
            }
        }

        public bool CanSetTemperature => false;

        public CameraStates CameraState => CameraStates.NoState; // TODO

        public bool CanShowLiveView {
            get {
                if (_camera != null) {
                    return _camera.SupportsPreview();
                } else {
                    return false;
                }
            }
        }

        public bool LiveViewEnabled {
            get => _liveViewEnabled;
            set {
                _liveViewEnabled = value;
                RaisePropertyChanged();
            }
        }

        public bool HasBattery => true;

        public int BatteryLevel {
            get {
                if (_camera != null && !_reopenNeeded) {
                    try {
                        _lastKnownBatteryLevel = (int)GetPropertyValue(PROPID_BATTERY).Value;
                    } catch (Exception ex) {
                        Logger.Warning($"Unable to read Sony battery level: {ex.Message}");
                    }
                    return _lastKnownBatteryLevel;
                } else if (_camera != null) {
                    // Waiting to reopen the camera; report the last reading rather than an error.
                    return _lastKnownBatteryLevel;
                } else {
                    return 0;
                }
            }
        }

        public int BitDepth {
            get {
                if (_camera != null) {
                    return _camera.BitsPerPixel;
                } else {
                    return 0;
                }
            }
        }

        public bool CanGetGain => GetAvailableIsoOptions().Any();

        public bool CanSetGain => CanGetGain;

        public int GainMax {
            get {
                var isoOptions = GetAvailableIsoOptions();
                if (!isoOptions.Any()) {
                    if (_camera != null) {
                        Logger.Error("Problem getting gain max: camera did not report ISO options.");
                    }
                    return -1;
                }

                return (int)isoOptions.Last().Value;
            }
        }

        public int GainMin {
            get {
                var isoOptions = GetAvailableIsoOptions();
                if (!isoOptions.Any()) {
                    if (_camera != null) {
                        Logger.Error("Problem getting gain min: camera did not report ISO options.");
                    }
                    return -1;
                }

                return (int)isoOptions.Min(o => o.Value);
            }
        }

        public int Gain {
            get {
                if (_camera != null && _reopenNeeded) {
                    // Waiting to reopen the camera; report the last reading rather than an error.
                    return _lastKnownGain;
                }
                if (_camera != null) {
                    try {
                        PropertyValue value = GetPropertyValue(PROPID_ISO);

                        _lastKnownGain = value.Value > 0 && value.Value < 0x00FFFFFF ? (int)value.Value : -1;
                        return _lastKnownGain;
                    } catch (Exception ex) {
                        Logger.Warning($"Unable to read Sony ISO: {ex.Message}");
                        return _lastKnownGain;
                    }
                } else {
                    return -1;
                }
            }

            set {
                if (_camera != null) {
                    try {
                        if (!Gains.Contains(value)) throw new ArgumentOutOfRangeException(nameof(value), "Select a supported manual ISO value.");
                        _requestedGain = value;
                        if (_reopenNeeded) {
                            // Applied once the camera has been reopened (see RestoreRequestedGain).
                            return;
                        }
                        _driver.SetProperty(_camera.Handle, PROPID_ISO, (uint)value);
                        RaisePropertyChanged(nameof(Gain));
                    } catch (Exception ex) {
                        Logger.Error($"Problem setting gain to {value}", ex);
                    }
                }
            }
        }

        public IList<int> Gains => GetAvailableIsoOptions().Count > 0 ? _gains : new List<int>();

        public string Id => _device.Id == SimulatedSonyCameraBackend.DeviceId ? SimulatedSonyCameraBackend.DeviceId : "Sony";

        public string Name {
            get => _device.Model;
            set => throw new NotImplementedException();
        }

        public string DisplayName {
            get => _device.Model;
            set => throw new NotImplementedException();
        }

        public string Category { get => "Sony"; }

        public bool Connected {
            get {
                SonyCameraInfo camera = _camera;
                if (camera == null) {
                    return false;
                }
                if (_reopening || _driver.IsOpen(camera.Handle)) {
                    return true;
                }
                // The handle was lost (e.g. USB unplugged). Stay connected so NINA keeps
                // the camera in the sequence; the next exposure reopens it. Only report a
                // disconnect once nothing can be reopened (a native call hung).
                if (_driver.CanReopen) {
                    _reopenNeeded = true;
                    return true;
                }
                return false;
            }
        }

        public string Description {
            get {
                if (_camera != null) {
                    return _camera.GetDescription();
                } else {
                    return _device.GetDescription();
                }
            }
        }

        public string DriverInfo => "https://retro.kiwi";

        public string DriverVersion => string.Empty;

        public double TemperatureSetPoint {
            get => double.NaN;

            set {
            }
        }

        public bool CanSubSample => false;

        public bool EnableSubSample { get; set; }

        public int SubSampleX { get; set; }

        public int SubSampleY { get; set; }

        public int SubSampleWidth { get; set; }

        public int SubSampleHeight { get; set; }

        public bool CoolerOn {
            get => false;
            set {
            }
        }

        public double CoolerPower => double.NaN;

        public bool HasDewHeater => false;

        public bool DewHeaterOn {
            get => false;
            set {
            }
        }

        public bool CanSetOffset => false;

        public int Offset { get => -1; set => throw new NotImplementedException(); }

        public int OffsetMin => 0;

        public int OffsetMax => 0;

        public bool CanSetUSBLimit => false;

        public int USBLimit { get => -1; set => throw new NotImplementedException(); }

        public int USBLimitMin => -1;

        public int USBLimitMax => -1;

        public int USBLimitStep => -1;

        public double ElectronsPerADU => double.NaN;

        public IList<string> ReadoutModes => new List<string> { "Default" };

        public short ReadoutMode {
            get => 0;
            set { }
        }

        public short ReadoutModeForSnapImages {
            get => _readoutModeForSnapImages;
            set {
                _readoutModeForSnapImages = value;
                RaisePropertyChanged();
            }
        }

        public short ReadoutModeForNormalImages {
            get => _readoutModeForNormalImages;
            set {
                _readoutModeForNormalImages = value;
                RaisePropertyChanged();
            }
        }

        public AsyncObservableCollection<BinningMode> BinningModes {
            get {
                if (_binningModes == null) {
                    _binningModes = new AsyncObservableCollection<BinningMode>();
                    _binningModes.Add(new BinningMode(1, 1));
                }

                return _binningModes;
            }
        }

        #endregion

        #region Supported Methods

        public void StartLiveView(CaptureSequence sequence) {
            LiveViewEnabled = true;
        }

        public void StopLiveView() {
            LiveViewEnabled = false;
        }

        public Task<bool> Connect(CancellationToken token) {
            return Task.Run<bool>(() => {
                bool connected;
                lock (connectionLock) {
                    token.ThrowIfCancellationRequested();
                    if (Connected) return true;
                    Volatile.Write(ref _lastCameraTemperature, double.NaN);
                    _lastKnownGain = -1;
                    _lastKnownBatteryLevel = 0;
                    try {
                        _camera = _driver.OpenCamera(_device.Id);
                        _reopenNeeded = false;
                    } catch (Exception ex) {
                        Logger.Error(ex);
                        _camera = null;
                    }
                    connected = _camera != null;
                }

                NotifyGainPropertiesChanged();
                return connected;
            }, token);
        }

        public void Disconnect() {
            _stopReopenWait = true;
            lock (connectionLock) {
                if (_camera == null) return;
                try {
                    _driver.CloseCamera(_camera.Handle);
                } catch (Exception ex) {
                    Logger.Error(ex);
                }

                _camera = null;
                _reopenNeeded = false;
                Volatile.Write(ref _lastCameraTemperature, double.NaN);
            }
            NotifyGainPropertiesChanged();
        }

        public Task<IExposureData> DownloadLiveView(CancellationToken token) {
            return Task.Run<IExposureData>(() => {
                using (var memStream = new MemoryStream(_driver.GetLiveView(_camera.Handle))) {
                    memStream.Position = 0;

                    JpegBitmapDecoder decoder =
                        new JpegBitmapDecoder(memStream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);

                    FormatConvertedBitmap bitmap = new FormatConvertedBitmap();
                    bitmap.BeginInit();
                    bitmap.Source = decoder.Frames[0];
                    bitmap.DestinationFormat = System.Windows.Media.PixelFormats.Gray16;
                    bitmap.EndInit();

                    ushort[] outArray = new ushort[bitmap.PixelWidth * bitmap.PixelHeight];
                    bitmap.CopyPixels(outArray, 2 * bitmap.PixelWidth, 0);

                    var metaData = new ImageMetaData();

                    return _exposureDataFactory.CreateImageArrayExposureData(
                        input: outArray,
                        width: bitmap.PixelWidth,
                        height: bitmap.PixelHeight,
                        bitDepth: 16,
                        isBayered: false,
                        metaData: metaData);
                }
            });
        }

        public void SetupDialog() {
            throw new NotImplementedException();
        }

        public void StartExposure(CaptureSequence sequence) {
            if (!Connected) throw new SonyException("Connect a Sony camera before starting an exposure.");
            _exposureFailed = true;   // until the capture has actually started
            _starting = true;
            try {
                StartExposureCore(sequence);
            } finally {
                _starting = false;
            }
        }

        private void StartExposureCore(CaptureSequence sequence) {
            if (_reopenNeeded && !WaitForReopen()) {
                if (_stopReopenWait) {
                    throw new CameraExposureFailedException("Exposure cancelled while waiting for the Sony camera to come back.");
                }
                string message = $"The Sony camera is not responding (it may have been unplugged) and did not come back within {_reopenWait.TotalSeconds:0} s. The next exposure will wait for it again.";
                Logger.Warning(message);
                throw new CameraExposureFailedException(message);
            }
            if (_camera != null) {
                if (!WaitWhileCameraBusy()) {
                    throw new CameraExposureFailedException("Exposure cancelled while waiting for the camera to finish the previous one.");
                }
                try {
                    _currentExposureSeconds = sequence.ExposureTime;
                    _capture.Start((float)sequence.ExposureTime);
                    _exposureFailed = false;
                } catch (InvalidOperationException ex) {
                    Logger.Warning(ex.Message);
                    throw new CameraExposureFailedException(ex.Message, ex);
                } catch (SonyException ex) {
                    _reopenNeeded = true;
                    Logger.Warning(ex.Message);
                    throw new CameraExposureFailedException(ex.Message, ex);
                }
            }
        }

        public void StopExposure() {
            AbortExposure();
        }

        public void AbortExposure() {
            // Also ends a wait for an unplugged camera to come back.
            _stopReopenWait = true;
            if (_camera == null) {
                return;
            }
            if (!_starting && (_exposureFailed || _reopenNeeded)) {
                Logger.Debug("AbortExposure after the exposure had already failed; nothing to cancel.");
                return;
            }

            if (_capture.Abort() == CaptureController.AbortResult.SoftCancelRequested) {
                _showWarning("Abort requested. The camera will finish the current exposure before another can start. Native cancellation is disabled or unavailable.");
                Logger.Info("AbortExposure requested; native cancel unavailable; letting capture finish.");
            }
        }

        public async Task WaitUntilExposureIsReady(CancellationToken token) {
            var limit = TimeSpan.FromSeconds(Math.Max(0, _currentExposureSeconds) * 2) + _exposureTimeoutMargin;
            using (token.Register(AbortExposure))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                timeout.CancelAfter(limit);
                try {
                    await _capture.WaitUntilReady(timeout.Token);
                } catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested) {
                    _exposureFailed = true;
                    _reopenNeeded = true;
                    Logger.Error($"The Sony camera did not finish the exposure within {limit.TotalSeconds:0} s; reopening it before the next exposure.");
                    throw new SonyException("The camera did not finish the exposure in time (see log)");
                } catch (OperationCanceledException) {
                    Logger.Info("WaitUntilExposureIsReady cancelled.");
                    throw;
                } catch (Exception ex) {
                    // A failed capture is how a pulled cable usually shows up; reopen before the next one.
                    _exposureFailed = true;
                    _reopenNeeded = true;
                    Logger.Error("WaitUntilExposureIsReady got exception.", ex);
                    throw new SonyException("Problem while waiting for image to be ready (see log)");
                }
            }
        }

        public Task<IExposureData> DownloadExposure(CancellationToken token) {
            return Task.Run<IExposureData>(() => {
                token.ThrowIfCancellationRequested();
                SonyExposureFrame image = _driver.GetLastImage();
                var metaData = new ImageMetaData();
                double temperature = image.Pixels == null ? SonyCameraTemperature.Read(image.RawBytes) : double.NaN;
                metaData.Camera.Temperature = temperature;
                Volatile.Write(ref _lastCameraTemperature, temperature);
                RaisePropertyChanged(nameof(Temperature));
                if (image.Pixels != null) {
                    return _exposureDataFactory.CreateImageArrayExposureData(
                        input: image.Pixels,
                        width: image.Width,
                        height: image.Height,
                        bitDepth: image.BitDepth,
                        isBayered: true,
                        metaData: metaData);
                }

                return _exposureDataFactory.CreateRAWExposureData(
                    converter: _profileService.ActiveProfile.CameraSettings.RawConverter,
                    rawBytes: image.RawBytes,
                    rawType: "arw",
                    bitDepth: this.BitDepth,
                    metaData: metaData);
            }, token);
        }
        #endregion

        #region Unsupported Methods

        public string Action(string actionName, string actionParameters) {
            throw new NotImplementedException();
        }


        public void SendCommandBlind(string command, bool raw = true) {
            throw new NotImplementedException();
        }

        public bool SendCommandBool(string command, bool raw = true) {
            throw new NotImplementedException();
        }

        public string SendCommandString(string command, bool raw = true) {
            throw new NotImplementedException();
        }

        public void SetBinning(short x, short y) {
            // Ignore
        }
        
        public void UpdateSubSampleArea() {
            if (_camera == null) {
                EnableSubSample = false;
                SubSampleX = 0;
                SubSampleY = 0;
                SubSampleWidth = 0;
                SubSampleHeight = 0;
                return;
            }

            if (EnableSubSample && !CanSubSample) {
                Logger.Warning("Sub-sampling requested but not supported for Sony cameras. Falling back to full frame.");
                EnableSubSample = false;
            }

            // Sony cameras currently expose the entire frame, so always reset to the sensor dimensions.
            SubSampleX = 0;
            SubSampleY = 0;
            SubSampleWidth = _camera.ImageSize.Width;
            SubSampleHeight = _camera.ImageSize.Height;
        }

        #endregion


        // TODO!!! WE NEED ONE
        public bool HasSetupDialog => false;

        public IList<string> SupportedActions => new List<string>();
    }
}
