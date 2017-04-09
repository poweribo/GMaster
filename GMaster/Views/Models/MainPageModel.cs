﻿namespace GMaster.Views
{
    using System;
    using System.Collections.ObjectModel;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Threading.Tasks;
    using Annotations;
    using Camera;
    using Logger;
    using Windows.ApplicationModel;
    using Windows.UI.Core;
    using Windows.UI.Xaml;

    public class MainPageModel : INotifyPropertyChanged
    {
        private readonly CameraViewModel[] allViews =
            { new CameraViewModel(), new CameraViewModel(), new CameraViewModel(), new CameraViewModel() };

        private CameraViewModel[] activeViews;
        private bool? isLandscape;
        private GridLength secondColumnWidth = new GridLength(0, GridUnitType.Star);
        private GridLength secondRowHeight = new GridLength(0);
        private DeviceInfo selectedDevice;
        private SplitMode splitMode;

        public MainPageModel()
        {
            LumixManager = new LumixManager(CultureInfo.CurrentCulture.TwoLetterISOLanguageName);

            LumixManager.DeviceDiscovered += Lumix_DeviceDiscovered;

            var cameraRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            cameraRefreshTimer.Tick += CameraRefreshTimer_Tick;
            cameraRefreshTimer.Start();

            ConnectableDevices.CollectionChanged += ConnectableDevices_CollectionChanged;
            Wifi.AutoconnectAlways = GeneralSettings.WiFiAutoconnectAlways;
            foreach (var ap in GeneralSettings.WiFiAutoconnectAccessPoints.Value)
            {
                Wifi.AutoconnectAccessPoints.Add(ap);
            }

            Wifi.PropertyChanged += Wifi_PropertyChanged;
        }

        public event Action<Lumix> CameraDisconnected;

        public event PropertyChangedEventHandler PropertyChanged;

        public CameraViewModel[] ActiveViews
        {
            get
            {
                if (activeViews == null)
                {
                    activeViews = new[] { View1 };
                }

                return activeViews;
            }

            set
            {
                activeViews = value;

                foreach (var view in allViews.Except(value))
                {
                    view.SelectedCamera = null;
                }
            }
        }

        public ObservableCollection<DeviceInfo> ConnectableDevices { get; } = new ObservableCollection<DeviceInfo>();

        public ObservableCollection<ConnectedCamera> ConnectedCameras { get; } =
            new ObservableCollection<ConnectedCamera>();

        public CoreDispatcher Dispatcher { get; set; }

        public Donations Donations { get; } = new Donations();

        public GeneralSettings GeneralSettings { get; } = new GeneralSettings();

        public ObservableCollection<LutInfo> InstalledLuts { get; } = new ObservableCollection<LutInfo>();

        public object IsDebug => Debugger.IsAttached;

        public bool IsLandscape
        {
            set
            {
                if (isLandscape != value)
                {
                    isLandscape = value;
                    SplitMode = value ? GeneralSettings.LandscapeSplitMode : GeneralSettings.PortraitSplitMode;
                }
            }
        }

        public LumixManager LumixManager { get; }

        public GridLength SecondColumnWidth
        {
            get => secondColumnWidth;
            set
            {
                if (value.Equals(secondColumnWidth))
                {
                    return;
                }

                secondColumnWidth = value;
                OnPropertyChanged();
            }
        }

        public GridLength SecondRowHeight
        {
            get => secondRowHeight;
            set
            {
                if (value.Equals(secondRowHeight))
                {
                    return;
                }

                secondRowHeight = value;
                OnPropertyChanged();
            }
        }

        public DeviceInfo SelectedDevice
        {
            get => selectedDevice;

            set
            {
                selectedDevice = value;
                OnPropertyChanged();
            }
        }

        public SplitMode SplitMode
        {
            get => splitMode;
            set
            {
                splitMode = value;
                switch (value)
                {
                    case SplitMode.One:
                        ActiveViews = new[] { View1 };
                        SecondColumnWidth = new GridLength(0);
                        SecondRowHeight = new GridLength(0);
                        break;

                    case SplitMode.Horizontal:
                        ActiveViews = new[] { View1, View3 };
                        SecondColumnWidth = new GridLength(0);
                        SecondRowHeight = new GridLength(1, GridUnitType.Star);
                        if (View3.SelectedCamera == null && View2.SelectedCamera != null)
                        {
                            View3.SelectedCamera = View2.SelectedCamera;
                            View2.SelectedCamera = null;
                        }

                        FillViews();

                        break;

                    case SplitMode.Vertical:
                        ActiveViews = new[] { View1, View2 };
                        SecondColumnWidth = new GridLength(1, GridUnitType.Star);
                        SecondRowHeight = new GridLength(0);
                        if (View2.SelectedCamera == null && View3.SelectedCamera != null)
                        {
                            View2.SelectedCamera = View3.SelectedCamera;
                            View3.SelectedCamera = null;
                        }

                        FillViews();

                        break;

                    case SplitMode.Four:
                        ActiveViews = new[] { View1, View2, View3, View4 };
                        SecondColumnWidth = new GridLength(1, GridUnitType.Star);
                        SecondRowHeight = new GridLength(1, GridUnitType.Star);
                        FillViews();
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(value), value, null);
                }

                GeneralSettings.LandscapeSplitMode.Value = value;
            }
        }

        public string Version
        {
            get
            {
                var ver = Package.Current.Id.Version;
                return $"v{ver.Major}.{ver.Minor}.{ver.Build}";
            }
        }

        public CameraViewModel View1 => allViews[0];

        public CameraViewModel View2 => allViews[1];

        public CameraViewModel View3 => allViews[2];

        public CameraViewModel View4 => allViews[3];

        public WiFiHelper Wifi { get; } = new WiFiHelper();

        public void AddConnectableDevice(DeviceInfo device)
        {
            ConnectableDevices.Add(device);
            if (SelectedDevice == null)
            {
                SelectedDevice = device;
            }
        }

        public ConnectedCamera AddConnectedDevice(Lumix lumix)
        {
            ConnectableDevices.Remove(lumix.Device);
            if (!GeneralSettings.Cameras.TryGetValue(lumix.Udn, out var settings))
            {
                settings = new CameraSettings(lumix.Udn);
            }

            settings.GeneralSettings = GeneralSettings;

            var connectedCamera = new ConnectedCamera
            {
                Camera = lumix,
                Model = this,
                Settings = settings,
                SelectedLut = InstalledLuts.SingleOrDefault(l => l?.Id == settings.LutId),
                SelectedAspect = settings.Aspect,
                IsAspectAnamorphingVideoOnly = settings.IsAspectAnamorphingVideoOnly
            };

            ConnectedCameras.Add(connectedCamera);
            lumix.Disconnected += Lumix_Disconnected;
            return connectedCamera;
        }

        public async Task ConnectCamera(DeviceInfo modelSelectedDevice)
        {
            var lumix = await LumixManager.ConnectCamera(modelSelectedDevice);
            if (lumix != null)
            {
                var connectedCamera = AddConnectedDevice(lumix);

                ShowCamera(connectedCamera);
            }
        }

        public async Task Init()
        {
            await Wifi.Init();
            await LoadLutsInfo();
        }

        public async Task LoadLutsInfo()
        {
            var lutFolder = await App.GetLutsFolder();

            foreach (var file in (await lutFolder.GetFilesAsync()).Where(f => f.FileType == ".info"))
            {
                InstalledLuts.Add(await LutInfo.LoadfromFile(file));
            }
        }

        public void ShowCamera(ConnectedCamera eClickedItem)
        {
            if (ActiveViews.Any(c => c.SelectedCamera == eClickedItem))
            {
                return;
            }

            var first = ActiveViews.Aggregate((curMin, x) => curMin == null || x.SetTime < curMin.SetTime ? x : curMin);
            if (first != null)
            {
                first.SelectedCamera = eClickedItem;
            }
        }

        public async Task StartListening()
        {
            await LumixManager.StartListening();
            LumixManager.SearchCameras();
        }

        public void StopListening()
        {
            LumixManager.StopListening();
        }

        protected virtual void OnCameraDisconnected(Lumix obj)
        {
            CameraDisconnected?.Invoke(obj);
        }

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void CameraRefreshTimer_Tick(object sender, object e)
        {
            LumixManager.SearchCameras();
        }

        private void ConnectableDevices_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(ConnectableDevices));
        }

        private void FillViews()
        {
            foreach (var view in ActiveViews.Where(v => v.SelectedCamera == null))
            {
                var con = ConnectedCameras.FirstOrDefault(c => ActiveViews.All(v => v.SelectedCamera != c));
                if (con != null)
                {
                    view.SelectedCamera = con;
                }
            }
        }

        private async void Lumix_DeviceDiscovered(DeviceInfo dev)
        {
            try
            {
                await RunAsync(async () =>
                {
                    try
                    {
                        var camerafound = false;
                        var cameraauto = false;
                        if (GeneralSettings.Cameras.TryGetValue(dev.Uuid, out var settings))
                        {
                            cameraauto = settings.Autoconnect;
                            camerafound = true;
                        }

                        if ((camerafound && cameraauto) || (!camerafound && GeneralSettings.Autoconnect))
                        {
                            try
                            {
                                await ConnectCamera(dev);
                            }
                            catch (Exception e)
                            {
                                Log.Error(e);
                            }
                        }
                        else
                        {
                            AddConnectableDevice(dev);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex);
                    }
                });
            }
            catch (Exception e)
            {
                Log.Error(e);
                throw;
            }
        }

        private void Lumix_Disconnected(Lumix lumix, bool stillAvailbale)
        {
            lumix.Disconnected -= Lumix_Disconnected;
            ConnectedCameras.Remove(ConnectedCameras.Single(c => c.Udn == lumix.Udn));
            if (stillAvailbale)
            {
                AddConnectableDevice(lumix.Device);
            }

            OnCameraDisconnected(lumix);
        }

        private async Task RunAsync(Action action)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
        }

        private void Wifi_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(WiFiHelper.AutoconnectAlways):
                    GeneralSettings.WiFiAutoconnectAlways.Value = Wifi.AutoconnectAlways;
                    break;

                case nameof(WiFiHelper.AutoconnectAccessPoints):
                    GeneralSettings.WiFiAutoconnectAccessPoints.Value = Wifi.AutoconnectAccessPoints;
                    break;
            }
        }
    }
}