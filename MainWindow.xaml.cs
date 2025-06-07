// Copyright (c) 2025 Qz3rK 
// License: MIT (https://opensource.org/licenses/MIT)
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using System.Globalization;
using System.Timers;
using System.ComponentModel;
using System.Windows.Navigation;
using System.Diagnostics;
namespace DefragManager
{
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value == null ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    public class MapInfo : IDisposable
    {
        public string Name { get; set; } = "";
        private ImageSource? _thumbnail;
        public string VQ3Time { get; set; } = "";
        public string CPMTime { get; set; } = "";
        public bool IsFavorite { get; set; }
        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (_thumbnail != value)
                {
                    (_thumbnail as BitmapImage)?.Freeze();
                    _thumbnail = value;
                }
            }
        }
        public void Dispose() => (_thumbnail as IDisposable)?.Dispose();
    }
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private const int DisplayMapsCount = 10;
        private const int MaxThumbnailThreads = 4;
        private const int ThumbnailBatchSize = 5;
        private const int MaxRecentMaps = 10;
        private const int MaxMapCacheSize = 100000;
        private const int DemoCheckInterval = 15000;
        private const int DemoCacheDuration = 300;
        private const int SearchDelay = 500;
        private const int ThumbnailWidth = 200;
        private const int ThumbnailHeight = 100;
        private string _playerName = "";
        private List<MapInfo> _allMaps = new();
        private List<MapInfo> _filteredMaps = new();
        private List<string> _favorites = new();
        private List<string> _recentMaps = new();
        private Dictionary<string, string> _mapThumbnails = new();
        private Dictionary<string, DemoRecord> _demoCache = new();
        private CancellationTokenSource _searchCts = new();
        private DateTime _lastSearchTime = DateTime.MinValue;
        private readonly System.Timers.Timer _demoCheckTimer;
        private CancellationTokenSource _thumbnailLoadingCts = new();
        private readonly SemaphoreSlim _thumbnailLoadSemaphore = new(MaxThumbnailThreads);
        private bool _isLoadingThumbnails = false;
        private bool _isDisposed = false;
        private readonly Random _random = new();
        private DateTime _lastDemoScanTime = DateTime.MinValue;
        private string _enginePath = "oDFe.x64.exe";
        private static readonly Dictionary<string, BitmapImage> _thumbnailCache = new Dictionary<string, BitmapImage>();

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        public class DemoRecord
        {
            public string VQ3Time { get; set; } = "";
            public string CPMTime { get; set; } = "";
            public string DemoFileName { get; set; } = "";
            public DateTime LastUpdate { get; set; }
        }
        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;
            WindowTitle = _playerName;
            CompositionTarget.Rendering += (s, e) =>
            {
                if ((DateTime.Now - _lastActivityCheck).TotalSeconds > 0.5)
                {
                    UpdateActivityState();
                    _lastActivityCheck = DateTime.Now;
                }
            };
            EnsureMgrDataDirectoryExists();
            InitializeDefaultSettings();
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            _demoCheckTimer = new System.Timers.Timer(DemoCheckInterval) { AutoReset = true };
            _demoCheckTimer.Elapsed += DemoCheckTimerElapsed;
            Loaded += OnWindowLoaded;
            Closed += OnWindowClosed;


            SetDarkTheme();
        }
        private async Task DelayedThumbnailLoad(TabItem selectedTab, CancellationToken token)
        {
            if (!_isWindowActive) return;

            try
            {
                await Task.Delay(3000, token);

                if (selectedTab.IsSelected && _isWindowActive && !token.IsCancellationRequested)
                {
                    if (selectedTab == FavoritesTab)
                    {
                        var favorites = _allMaps.Where(m => _favorites.Contains(m.Name)).ToList();
                        await LoadVisibleThumbnails(FavoritesScroll, FavoritesGrid, favorites, token);
                    }
                    else if (selectedTab == AllMapsTab)
                    {
                        await LoadVisibleThumbnails(MapsGridScroll, MapsGrid, _filteredMaps, token);
                    }
                    else if (selectedTab == RecentTab)
                    {
                        var recentMaps = _recentMaps
                            .Select(m => _allMaps.FirstOrDefault(am => am.Name == m))
                            .Where(m => m != null)
                            .ToList();
                        await LoadVisibleThumbnails(RecentScroll, RecentGrid, recentMaps!, token);
                    }
                }
            }
            catch (OperationCanceledException)
            {

            }
        }
        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {

            ScrollViewer scrollViewer = sender as ScrollViewer;
            if (scrollViewer != null)
            {
                if (e.Delta < 0)
                {
                    scrollViewer.LineDown();
                }
                else
                {
                    scrollViewer.LineUp();
                }
                e.Handled = true;
            }
        }
        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LogSettingsMessage("Window loading started");


                LoadSettings();
                LogSettingsMessage($"Player name: '{_playerName}'");


                Dispatcher.Invoke(() =>
                {
                    PlayerNameBox.Text = _playerName;
                    if (string.IsNullOrEmpty(_playerName))
                    {
                        MessageBox.Show("Please enter your player name...",
                                      "Player Name Required",
                                      MessageBoxButton.OK,
                                      MessageBoxImage.Information);
                    }
                });

                if (Directory.Exists("defrag/demos"))
                {
                    _lastDemoCount = Directory.GetFiles("defrag/demos", "*.dm_68", SearchOption.TopDirectoryOnly).Length;
                    LogSettingsMessage($"Initial demo count: {_lastDemoCount}");
                }

                await Task.Run(() =>
                {
                    LogSettingsMessage("Loading maps cache...");
                    LoadCache();

                    LogSettingsMessage("Loading demo cache...");
                    LoadDemoCache();

                    if (!string.IsNullOrEmpty(_playerName))
                    {
                        LogSettingsMessage("Initializing map times...");
                        InitializeMapTimes();

                        LogSettingsMessage("Cleaning up demo cache...");
                        CleanupDemoCache();
                    }
                });

                LogSettingsMessage("Updating UI...");
                UpdateFilteredMaps();
                UpdateFavoritesState();
                RefreshMapTimesUI();

                LogSettingsMessage("Starting background tasks...");
                _ = Task.Run(() => LoadAllThumbnails());
                SetupTimer();

                LogSettingsMessage("Window loaded successfully");

                _tabVisitCounts[AllMapsTab] = 0;
                _tabVisitCounts[FavoritesTab] = 0;
                _tabVisitCounts[RecentTab] = 0;
            }
            catch (Exception ex)
            {
                LogSettingsMessage($"Error in OnWindowLoaded: {ex.Message}");
                MessageBox.Show($"Initialization failed: {ex.Message}",
                              "Error",
                              MessageBoxButton.OK,
                              MessageBoxImage.Error);
                Close();
            }
        }
        private void SaveAllDemoCache()
        {
            try
            {
                var lines = new List<string>();
                foreach (var kv in _demoCache)
                {
                    var parts = kv.Key.Split('|');
                    if (parts.Length == 2)
                    {
                        var mapName = parts[0];
                        var physics = parts[1];
                        var time = physics == "vq3" ? kv.Value.VQ3Time : kv.Value.CPMTime;

                        lines.Add($"{mapName}|{kv.Value.VQ3Time}|{kv.Value.CPMTime}|{kv.Value.DemoFileName}|{kv.Value.LastUpdate.ToBinary()}");
                    }
                }
                File.WriteAllLines(Path.Combine("mgrdata", "democache.dat"), lines);
            }
            catch (Exception ex)
            {
                LogSettingsMessage($"Error saving demo cache: {ex.Message}");
            }
        }
        private async Task LoadAllThumbnails()
        {
            if (!_isWindowActive) return;
            try
            {
                var token = _thumbnailLoadingCts.Token;
                var tasks = new List<Task>();

                foreach (var map in _filteredMaps)
                {
                    if (token.IsCancellationRequested) break;

                    if (map.Thumbnail == null && _mapThumbnails.TryGetValue(map.Name, out var thumbPath))
                    {
                        await _thumbnailLoadSemaphore.WaitAsync(token);
                        tasks.Add(LoadThumbnailAsync(map, thumbPath, token)
                            .ContinueWith(t => _thumbnailLoadSemaphore.Release(), token));
                    }
                }
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                LogThumbnailMessage($"Error loading all thumbnails: {ex.Message}");
            }
        }
        private void LogThumbnailMessage(string message)
        {
            try
            {
                File.AppendAllText(Path.Combine("mgrdata", "thmbn.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n");
            }
            catch { }
        }
        private void LoadDemoCache()
        {
            try
            {
                _demoCache.Clear();

                if (File.Exists(Path.Combine("mgrdata", "democache.dat")))
                {
                    foreach (var line in File.ReadAllLines(Path.Combine("mgrdata", "democache.dat")))
                    {
                        var parts = line.Split('|');
                        if (parts.Length == 5)
                        {
                            var demoFileName = parts[3];
                            var demoName = Path.GetFileNameWithoutExtension(demoFileName);
                            var mapName = ExtractMapNameFromDemo(demoName);

                            if (!string.IsNullOrEmpty(mapName))
                            {
                                var physics = demoName.Contains("[df.cpm]") ? "cpm" : "vq3";
                                var cacheKey = $"{mapName}|{physics}";

                                _demoCache[cacheKey] = new DemoRecord
                                {
                                    VQ3Time = physics == "vq3" ? parts[1] : "",
                                    CPMTime = physics == "cpm" ? parts[2] : "",
                                    DemoFileName = demoFileName,
                                    LastUpdate = DateTime.FromBinary(long.Parse(parts[4]))
                                };
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogSettingsMessage($"Error loading demo cache: {ex.Message}");
            }
        }
        private string ExtractMapNameFromDemo(string demoName)
        {
            try
            {


                int modeStart = demoName.IndexOf('[');
                if (modeStart <= 0) return null;


                string mapName = demoName.Substring(0, modeStart).Trim();


                if (mapName.Contains("[") && mapName.Contains("]"))
                {
                    int bracketStart = mapName.IndexOf('[');
                    if (bracketStart > 0)
                    {
                        mapName = mapName.Substring(0, bracketStart).Trim();
                    }
                }

                return mapName;
            }
            catch
            {
                return null;
            }
        }
        private void SaveDemoCache()
        {
            try
            {
                var lines = new List<string>();
                foreach (var kv in _demoCache)
                {

                    if (!string.IsNullOrEmpty(kv.Value.DemoFileName))
                    {
                        lines.Add($"{kv.Key}|{kv.Value.VQ3Time}|{kv.Value.CPMTime}|{kv.Value.DemoFileName}|{kv.Value.LastUpdate.ToBinary()}");
                    }
                }
                File.WriteAllLines(Path.Combine("mgrdata", "democache.dat"), lines);
            }
            catch { }
        }
        private void OnWindowClosed(object? sender, EventArgs e)
        {
            try
            {
                LogSettingsMessage("Window closing started");


                _playerName = PlayerNameBox?.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(_playerName))
                {
                    File.WriteAllText(Path.Combine("mgrdata", "name.dat"), _playerName);
                }


                SaveAllDemoCache();

                SaveThumbnailCache();

                LogSettingsMessage("Window closed successfully");
            }
            catch (Exception ex)
            {
                LogSettingsMessage($"Error in OnWindowClosed: {ex.Message}");
            }
            finally
            {
                CleanupResources();
            }
        }
        private void CleanupResources()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _demoCheckTimer?.Stop();
            _demoCheckTimer?.Dispose();
            _thumbnailLoadingCts?.Cancel();
            _thumbnailLoadingCts?.Dispose();
            _thumbnailLoadSemaphore?.Dispose();
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            foreach (var map in _allMaps) map.Dispose();
            _allMaps.Clear();
            _filteredMaps.Clear();
            _favorites.Clear();
            _recentMaps.Clear();
            _mapThumbnails.Clear();
            SaveDemoCache();
            SaveThumbnailCache();
            foreach (var bitmap in _thumbnailCache.Values)
            {
                bitmap?.Freeze();
            }
        }
        private void DemoCheckTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            if (!_isWindowActive) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isWindowActive)
                {
                    CheckForNewDemos();
                }
            }), DispatcherPriority.Background);
        }
        private void SetupTimer() => _demoCheckTimer.Start();
        private void LoadSettings()
        {
            try
            {

                var namePath = Path.Combine("mgrdata", "name.dat");
                if (File.Exists(namePath))
                {
                    _playerName = File.ReadAllText(namePath).Trim();
                    WindowTitle = _playerName;
                    LogSettingsMessage($"Loaded player name: '{_playerName}'");
                    Dispatcher.Invoke(() =>
                    {
                        PlayerNameBox.Text = _playerName;
                        OnPropertyChanged(nameof(WindowTitle));
                    });
                }
                else
                {
                    _playerName = "";
                    LogSettingsMessage("No player name file found");
                }

                var enginePath = Path.Combine("mgrdata", "engine.dat");
                if (File.Exists(enginePath))
                {
                    _enginePath = File.ReadAllText(enginePath).Trim();
                    Dispatcher.Invoke(() => EnginePathBox.Text = _enginePath);
                }
            }
            catch (Exception ex)
            {
                LogSettingsMessage($"Error in LoadSettings: {ex}");
                MessageBox.Show($"Failed to load settings: {ex.Message}", "Error",
                              MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        private void SaveAllSettings()
        {
            try
            {
                LogSettingsMessage("SaveAllSettings started");


                _playerName = PlayerNameBox?.Text?.Trim() ?? "";
                WindowTitle = _playerName;
                _enginePath = EnginePathBox?.Text?.Trim() ?? "oDFe.x64.exe";


                if (string.IsNullOrEmpty(_playerName))
                {
                    LogSettingsMessage("Empty player name, showing warning");
                    MessageBox.Show("Please enter a valid player name", "Warning",
                                  MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                OnPropertyChanged(nameof(WindowTitle));
                LogSettingsMessage($"Saving player name: '{_playerName}'");
                LogSettingsMessage($"Saving engine path: '{_enginePath}'");

                Directory.CreateDirectory("mgrdata");


                File.WriteAllText(Path.Combine("mgrdata", "name.dat"), _playerName);
                File.WriteAllText(Path.Combine("mgrdata", "engine.dat"), _enginePath);

                CleanupDemoCache();


                LogSettingsMessage("Updating maps with new settings");
                Task.Run(() =>
                {
                    foreach (var map in _allMaps) UpdateBestTimes(map);
                    Dispatcher.Invoke(() => UpdateFilteredMaps());
                });


                MessageBox.Show("Settings saved successfully!", "Success",
                              MessageBoxButton.OK, MessageBoxImage.Information);
                LogSettingsMessage("Settings successfully saved and UI updated");
            }
            catch (Exception ex)
            {
                LogSettingsMessage($"Error in SaveAllSettings: {ex.Message}");
                MessageBox.Show($"Failed to save settings: {ex.Message}", "Error",
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            SaveAllSettings();
        }
        private void LoadCache()
        {
            try
            {

                var thumbnailCache = LoadThumbnailCache();

                if (File.Exists(Path.Combine("mgrdata", "mapscache.dat")))
                    _allMaps = File.ReadAllLines(Path.Combine("mgrdata", "mapscache.dat")).Take(MaxMapCacheSize)
                        .Select(l => new MapInfo
                        {
                            Name = l,
                            Thumbnail = thumbnailCache.TryGetValue(l, out var thumb) ? thumb : null
                        }).ToList();
                if (File.Exists(Path.Combine("mgrdata", "favorites.dat")))
                    _favorites = File.ReadAllLines(Path.Combine("mgrdata", "favorites.dat")).Take(MaxMapCacheSize).ToList();
                if (File.Exists(Path.Combine("mgrdata", "recent.dat")))
                    _recentMaps = File.ReadAllLines(Path.Combine("mgrdata", "recent.dat")).Take(MaxRecentMaps).ToList();
                if (File.Exists(Path.Combine("mgrdata", "thumbnails.dat")))
                {
                    _mapThumbnails = new Dictionary<string, string>();
                    foreach (var line in File.ReadAllLines(Path.Combine("mgrdata", "thumbnails.dat")).Take(MaxMapCacheSize))
                    {
                        var parts = line.Split(new[] { '|' }, 2);
                        if (parts.Length == 2)
                        {
                            _mapThumbnails[parts[0]] = parts[1];
                        }
                        else
                        {
                            LogThumbnailMessage($"Invalid thumbnail entry format: {line}");
                        }
                    }
                }
                ScanMapsIfNeeded();
                UpdateFilteredMaps();
            }
            catch
            {
                MessageBox.Show("Failed to load cache. Will rescan maps.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                ScanMaps();
                UpdateFilteredMaps();
            }
        }
        private void ScanMapsIfNeeded()
        {
            try
            {
                var baseQ3Count = Directory.Exists("baseq3") ? Directory.GetFiles("baseq3", "*.pk3").Length : 0;
                var defragCount = Directory.Exists("defrag") ? Directory.GetFiles("defrag", "*.pk3").Length : 0;
                var cacheValid = File.Exists(Path.Combine("mgrdata", "mapscache.dat")) && File.Exists(Path.Combine("mgrdata", "scaninfo.dat"));
                if (!cacheValid || File.ReadAllText(Path.Combine("mgrdata", "scaninfo.dat")) != $"{baseQ3Count}|{defragCount}")
                {
                    ScanMaps();
                    File.WriteAllText(Path.Combine("mgrdata", "scaninfo.dat"), $"{baseQ3Count}|{defragCount}");
                }
            }
            catch { }
        }
        private void ScanMaps()
        {
            try
            {
                _allMaps.Clear();
                _mapThumbnails.Clear();
                void ScanPk3(string pk3Path)
                {
                    if (_allMaps.Count >= MaxMapCacheSize) return;
                    try
                    {
                        using var archive = ZipFile.OpenRead(pk3Path);


                        foreach (var entry in archive.Entries.Where(e =>
                            e.FullName.StartsWith("maps/", StringComparison.OrdinalIgnoreCase) &&
                            e.Name.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase)))
                        {
                            if (_allMaps.Count >= MaxMapCacheSize) break;
                            _allMaps.Add(new MapInfo { Name = Path.GetFileNameWithoutExtension(entry.Name) });
                        }

                        foreach (var entry in archive.Entries.Where(e =>
                            (e.FullName.StartsWith("levelshots/", StringComparison.OrdinalIgnoreCase) ||
                             e.FullName.StartsWith("textures/levelshots/", StringComparison.OrdinalIgnoreCase)) &&
                            e.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)))
                        {
                            var mapName = Path.GetFileNameWithoutExtension(entry.Name);
                            var fullPk3Path = Path.GetFullPath(pk3Path);


                            var matchingMap = _allMaps.FirstOrDefault(m =>
                                string.Equals(m.Name, mapName, StringComparison.OrdinalIgnoreCase));

                            if (matchingMap != null && !_mapThumbnails.ContainsKey(matchingMap.Name))
                            {
                                _mapThumbnails[matchingMap.Name] = $"{fullPk3Path}|{entry.FullName.Replace('\\', '/')}";
                                LogThumbnailMessage($"Found thumbnail for {matchingMap.Name} in {fullPk3Path}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogThumbnailMessage($"Error scanning PK3 {pk3Path}: {ex.Message}");
                    }
                }
                if (Directory.Exists("baseq3"))
                {
                    foreach (var pk3 in Directory.GetFiles("baseq3", "*.pk3"))
                    {
                        if (_allMaps.Count >= MaxMapCacheSize) break;
                        ScanPk3(pk3);
                    }
                }
                if (Directory.Exists("defrag"))
                {
                    foreach (var pk3 in Directory.GetFiles("defrag", "*.pk3"))
                    {
                        if (_allMaps.Count >= MaxMapCacheSize) break;
                        ScanPk3(pk3);
                    }
                }
                File.WriteAllLines(Path.Combine("mgrdata", "mapscache.dat"), _allMaps.Select(m => m.Name));
                File.WriteAllLines(Path.Combine("mgrdata", "thumbnails.dat"), _mapThumbnails.Select(kv => $"{kv.Key}|{kv.Value}"));


                var thumbnailCache = LoadThumbnailCache();
                foreach (var map in _allMaps)
                {
                    if (thumbnailCache.TryGetValue(map.Name, out var thumb))
                    {
                        map.Thumbnail = thumb;
                    }
                }


                CleanupOldThumbnails();
                LogThumbnailMessage($"Scan completed. Found {_allMaps.Count} maps and {_mapThumbnails.Count} thumbnails.");
            }
            catch (Exception ex)
            {
                LogThumbnailMessage($"Error in ScanMaps: {ex.Message}");
                MessageBox.Show("Failed to scan maps", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private async Task UpdateFilteredMaps()
        {
            try
            {
                var searchText = SearchBox?.Text?.ToLower() ?? "";
                var filtered = _allMaps.Where(m => m.Name?.ToLower().Contains(searchText) ?? false).ToList();
                _filteredMaps = string.IsNullOrEmpty(searchText)
                    ? filtered.OrderBy(x => _random.Next()).Take(DisplayMapsCount).ToList()
                    : filtered.Take(DisplayMapsCount).ToList();
                foreach (var map in _filteredMaps)
                {
                    map.IsFavorite = _favorites.Contains(map.Name);
                    UpdateBestTimes(map);
                }

                if (string.IsNullOrEmpty(searchText))
                {
                    await Task.Run(() => LoadAllThumbnails());
                    await Task.Delay(500);
                }
                await Dispatcher.Invoke(async () =>
                {
                    MapsGrid.ItemsSource = null;
                    MapsGrid.ItemsSource = _filteredMaps;

                    var favorites = _allMaps.Where(m => _favorites.Contains(m.Name)).ToList();
                    FavoritesGrid.ItemsSource = null;
                    FavoritesGrid.ItemsSource = favorites;

                    RecentGrid.ItemsSource = null;
                    RecentGrid.ItemsSource = _recentMaps.Select(m => _allMaps.FirstOrDefault(am => am.Name == m))
                                                      .Where(m => m != null).ToList();

                    if (!string.IsNullOrEmpty(searchText))
                    {
                        await Task.Delay(400);
                        await LoadMissingThumbnails(MapsGridScroll, MapsGrid, _filteredMaps, _thumbnailLoadingCts.Token);
                    }
                });
            }
            catch (Exception ex)
            {
                LogThumbnailMessage($"Error in UpdateFilteredMaps: {ex.Message}");
            }
        }
        private readonly Dictionary<TabItem, int> _tabVisitCounts = new Dictionary<TabItem, int>();
        private async void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source is TabControl tabControl && tabControl.SelectedItem is TabItem selectedTab)
            {

                if (selectedTab == FavoritesTab || selectedTab == RecentTab)
                {
                    UpdateFavoritesState();
                }

                _thumbnailLoadingCts.Cancel();
                _thumbnailLoadingCts.Dispose();
                _thumbnailLoadingCts = new CancellationTokenSource();
                var token = _thumbnailLoadingCts.Token;
                try
                {

                    int maxUpdates = selectedTab == AllMapsTab ? 2 : 1;


                    if (!_tabVisitCounts.TryGetValue(selectedTab, out var visitCount) || visitCount < maxUpdates)
                    {
                        _tabVisitCounts[selectedTab] = visitCount + 1;

                        Dispatcher.Invoke(() =>
                        {
                            if (selectedTab == FavoritesTab)
                            {
                                var favorites = _allMaps.Where(m => _favorites.Contains(m.Name)).ToList();
                                FavoritesGrid.ItemsSource = null;
                                FavoritesGrid.ItemsSource = favorites;
                            }
                            else if (selectedTab == AllMapsTab)
                            {
                                MapsGrid.ItemsSource = null;
                                MapsGrid.ItemsSource = _filteredMaps;
                            }
                            else if (selectedTab == RecentTab)
                            {
                                var recentMaps = _recentMaps.Select(m => _allMaps.FirstOrDefault(am => am.Name == m))
                                                          .Where(m => m != null).ToList();
                                RecentGrid.ItemsSource = null;
                                RecentGrid.ItemsSource = recentMaps;
                            }
                        });
                    }

                    if (selectedTab == FavoritesTab)
                    {
                        var favorites = _allMaps.Where(m => _favorites.Contains(m.Name)).ToList();
                        await LoadVisibleThumbnails(FavoritesScroll, FavoritesGrid, favorites, token);
                    }
                    else if (selectedTab == AllMapsTab)
                    {
                        await LoadVisibleThumbnails(MapsGridScroll, MapsGrid, _filteredMaps, token);
                    }
                    else if (selectedTab == RecentTab)
                    {
                        var recentMaps = _recentMaps
                            .Select(m => _allMaps.FirstOrDefault(am => am.Name == m))
                            .Where(m => m != null)
                            .ToList();
                        await LoadVisibleThumbnails(RecentScroll, RecentGrid, recentMaps!, token);
                    }


                    if (!token.IsCancellationRequested)
                    {
                        _ = DelayedThumbnailLoad(selectedTab, token);
                    }
                }
                catch (OperationCanceledException)
                {

                }
            }
        }
        private List<MapInfo> GetCurrentlyVisibleMaps(ScrollViewer scrollViewer, DataGrid dataGrid, IList<MapInfo> sourceCollection)
        {
            var visibleMaps = new List<MapInfo>();

            if (scrollViewer == null || dataGrid.ItemsSource == null)
                return visibleMaps;
            try
            {

                var firstVisibleItemIndex = (int)(scrollViewer.VerticalOffset / dataGrid.RowHeight);
                var lastVisibleItemIndex = firstVisibleItemIndex + (int)(scrollViewer.ViewportHeight / dataGrid.RowHeight) + 1;


                lastVisibleItemIndex = Math.Min(lastVisibleItemIndex, sourceCollection.Count - 1);
                firstVisibleItemIndex = Math.Max(0, firstVisibleItemIndex);


                for (int i = firstVisibleItemIndex; i <= lastVisibleItemIndex; i++)
                {
                    if (i >= 0 && i < sourceCollection.Count)
                    {
                        visibleMaps.Add(sourceCollection[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                LogThumbnailMessage($"Error getting visible maps: {ex.Message}");
            }

            return visibleMaps;
        }
        private void UpdateBestTimes(MapInfo map)
        {
            if (map == null) return;

            bool hasVq3 = _demoCache.TryGetValue($"{map.Name}|vq3", out var vq3Record);
            bool hasCpm = _demoCache.TryGetValue($"{map.Name}|cpm", out var cpmRecord);

            map.VQ3Time = hasVq3 ? vq3Record.VQ3Time : "";
            map.CPMTime = hasCpm ? cpmRecord.CPMTime : "";

            if ((!hasVq3 || (DateTime.Now - vq3Record.LastUpdate).TotalSeconds >= DemoCacheDuration) ||
                (!hasCpm || (DateTime.Now - cpmRecord.LastUpdate).TotalSeconds >= DemoCacheDuration))
            {
                ScanDemosForMap(map);
            }
        }
        private void InitializeMapTimes()
        {
            foreach (var map in _allMaps)
            {
                UpdateBestTimes(map);
            }
        }
        private void ScanDemosForMap(MapInfo map)
        {
            if (!Directory.Exists("defrag/demos")) return;
            try
            {
                string vq3Time = null, cpmTime = null;
                string vq3DemoFile = null, cpmDemoFile = null;

                foreach (var demo in Directory.GetFiles("defrag/demos", $"*{map.Name}*.dm_68"))
                {
                    var demoName = Path.GetFileNameWithoutExtension(demo);

                    bool isPlayerDemo = !string.IsNullOrEmpty(_playerName) &&
                        (demoName.EndsWith($"({_playerName})", StringComparison.OrdinalIgnoreCase) ||
                         demoName.Contains($"({_playerName}.", StringComparison.OrdinalIgnoreCase));

                    if (!isPlayerDemo) continue;
                    var realMapName = ExtractMapNameFromDemo(demoName);
                    if (string.IsNullOrEmpty(realMapName) || !string.Equals(realMapName, map.Name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var timePart = demoName.Split(']').LastOrDefault()?.Split('(').FirstOrDefault()?.Trim();
                    if (string.IsNullOrEmpty(timePart)) continue;
                    if (demoName.Contains("[df.cpm]", StringComparison.OrdinalIgnoreCase) ||
                        demoName.Contains("[mdf.cpm]", StringComparison.OrdinalIgnoreCase) ||
                        demoName.Contains("[cpm]", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrEmpty(cpmTime) || string.Compare(timePart, cpmTime) < 0)
                        {
                            cpmTime = timePart;
                            cpmDemoFile = Path.GetFileName(demo);
                        }
                    }
                    else if (demoName.Contains("[df.vq3]", StringComparison.OrdinalIgnoreCase) ||
                             demoName.Contains("[mdf.vq3]", StringComparison.OrdinalIgnoreCase) ||
                             demoName.Contains("[vq3]", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrEmpty(vq3Time) || string.Compare(timePart, vq3Time) < 0)
                        {
                            vq3Time = timePart;
                            vq3DemoFile = Path.GetFileName(demo);
                        }
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    if (!string.IsNullOrEmpty(vq3Time))
                    {
                        map.VQ3Time = vq3Time;
                        _demoCache[$"{map.Name}|vq3"] = new DemoRecord
                        {
                            VQ3Time = vq3Time,
                            CPMTime = "",
                            DemoFileName = vq3DemoFile,
                            LastUpdate = DateTime.Now
                        };
                    }
                    if (!string.IsNullOrEmpty(cpmTime))
                    {
                        map.CPMTime = cpmTime;
                        _demoCache[$"{map.Name}|cpm"] = new DemoRecord
                        {
                            VQ3Time = "",
                            CPMTime = cpmTime,
                            DemoFileName = cpmDemoFile,
                            LastUpdate = DateTime.Now
                        };
                    }
                });
            }
            catch (Exception ex)
            {
                LogThumbnailMessage($"Error scanning demos for {map.Name}: {ex.Message}");
            }
        }
        private void RefreshMapTimesUI()
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    MapsGrid.Items.Refresh();
                    FavoritesGrid.Items.Refresh();
                    RecentGrid.Items.Refresh();
                });
                LogSettingsMessage("UI refreshed successfully");
            }
            catch (Exception ex)
            {
                LogSettingsMessage($"Error refreshing UI: {ex.Message}");
            }
        }
        private void CleanupDemoCache()
        {
            try
            {
                var keysToRemove = new List<string>();

                foreach (var kv in _demoCache)
                {
                    if (!string.IsNullOrEmpty(kv.Value.DemoFileName))
                    {
                        var demoName = Path.GetFileNameWithoutExtension(kv.Value.DemoFileName);
                        bool isPlayerDemo = demoName.EndsWith($"({_playerName})", StringComparison.OrdinalIgnoreCase) ||
                                           demoName.Contains($"({_playerName}.", StringComparison.OrdinalIgnoreCase);

                        if (!isPlayerDemo)
                        {
                            keysToRemove.Add(kv.Key);
                        }
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _demoCache.Remove(key);
                }
            }
            catch { }
        }
        private int _lastDemoCount = 0;
        private DateTime _lastDemoCheckTime = DateTime.MinValue;
        private int _lastDemoFileCount = -1;
        private readonly object _demoCheckLock = new object();
        private void CheckForNewDemos()
        {
            if (!_isWindowActive) return;
            lock (_demoCheckLock)
            {
                try
                {
                    string demosPath = "defrag/demos";
                    if (!Directory.Exists(demosPath))
                    {
                        LogSettingsMessage("Demos directory not found");
                        return;
                    }

                    int currentCount = Directory.GetFiles(demosPath, "*.dm_68", SearchOption.TopDirectoryOnly).Length;


                    if (currentCount == _lastDemoFileCount)
                    {
                        LogSettingsMessage($"Demo count unchanged: {currentCount} files");
                        return;
                    }
                    LogSettingsMessage($"Demo count changed from {_lastDemoFileCount} to {currentCount}, updating...");
                    _lastDemoFileCount = currentCount;
                    _lastDemoScanTime = DateTime.Now;

                    Task.Run(() =>
                    {
                        try
                        {

                            var demoFiles = Directory.GetFiles(demosPath, "*.dm_68", SearchOption.TopDirectoryOnly);


                            var affectedMaps = demoFiles
                                .Select(f => ExtractMapNameFromDemo(Path.GetFileNameWithoutExtension(f)))
                                .Where(name => !string.IsNullOrEmpty(name))
                                .Distinct()
                                .ToList();

                            foreach (var mapName in affectedMaps)
                            {
                                var map = _allMaps.FirstOrDefault(m =>
                                    string.Equals(m.Name, mapName, StringComparison.OrdinalIgnoreCase));
                                if (map != null)
                                {
                                    UpdateBestTimes(map);
                                }
                            }

                            SaveDemoCache();

                            Dispatcher.Invoke(() =>
                            {
                                MapsGrid.Items.Refresh();
                                FavoritesGrid.Items.Refresh();
                                RecentGrid.Items.Refresh();
                                LogSettingsMessage($"UI refreshed for {affectedMaps.Count} affected maps");
                            });
                        }
                        catch (Exception ex)
                        {
                            LogSettingsMessage($"Error processing demos: {ex.Message}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    LogSettingsMessage($"Error checking demo count: {ex.Message}");
                }
            }
        }
        private void InitializeDemoFolderState()
        {
            try
            {
                var demosPath = "defrag/demos";
                if (Directory.Exists(demosPath))
                {
                    _lastDemoFileCount = Directory.GetFiles(demosPath, "*.dm_68", SearchOption.TopDirectoryOnly).Length;
                    LogSettingsMessage($"Initial demo file count: {_lastDemoFileCount}");
                }
            }
            catch (Exception ex)
            {
                LogSettingsMessage($"Error initializing demo state: {ex.Message}");
            }
        }
        private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isWindowActive) return;
            _searchCts.Cancel();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;
            var currentText = SearchBox?.Text;
            try
            {

                await Task.Delay(400, token);

                if (!token.IsCancellationRequested && SearchBox?.Text == currentText)
                {
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        await UpdateFilteredMaps();


                        await Task.Delay(100);


                        if (!string.IsNullOrEmpty(currentText))
                        {
                            await LoadVisibleThumbnails(
                                FavoritesTab.IsSelected ? FavoritesScroll : MapsGridScroll,
                                FavoritesTab.IsSelected ? FavoritesGrid : MapsGrid,
                                FavoritesTab.IsSelected ?
                                    _allMaps.Where(m => _favorites.Contains(m.Name)).ToList() :
                                    _filteredMaps,
                                _searchCts.Token
                            );
                        }
                    }, DispatcherPriority.Background);
                }
            }
            catch (TaskCanceledException)
            {

            }
            catch (Exception ex)
            {
                LogThumbnailMessage($"Error in search: {ex.Message}");
            }
        }
        private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).DataContext is MapInfo map)
            {
                try
                {
                    map.IsFavorite = !map.IsFavorite;
                    if (map.IsFavorite && !_favorites.Contains(map.Name))
                        _favorites.Add(map.Name);
                    else
                        _favorites.Remove(map.Name);
                    File.WriteAllLines(Path.Combine("mgrdata", "favorites.dat"), _favorites);
                    Dispatcher.BeginInvoke(() =>
                    {
                        UpdateFilteredMaps();
                    }, DispatcherPriority.Background);
                }
                catch { }
            }
        }
        private void LaunchMap_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).DataContext is MapInfo map && ((Button)sender).Tag is string physics)
            {
                try
                {
                    _recentMaps.Remove(map.Name);
                    _recentMaps.Insert(0, map.Name);
                    if (_recentMaps.Count > MaxRecentMaps)
                        _recentMaps.RemoveRange(MaxRecentMaps, _recentMaps.Count - MaxRecentMaps);
                    File.WriteAllLines(Path.Combine("mgrdata", "recent.dat"), _recentMaps);
                    Dispatcher.BeginInvoke(() =>
                    {
                        UpdateFilteredMaps();
                    }, DispatcherPriority.Background);
                    KillExistingProcess();
                    System.Diagnostics.Process.Start(_enginePath, $"+{physics} {map.Name}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to launch game: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        private void PlayDemo_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).DataContext is MapInfo map && ((Button)sender).Tag is string physics)
            {
                try
                {

                    var cacheKey = $"{map.Name}|{physics}";

                    if (_demoCache.TryGetValue(cacheKey, out var demoRecord) && !string.IsNullOrEmpty(demoRecord.DemoFileName))
                    {
                        KillExistingProcess();

                        System.Diagnostics.Process.Start(_enginePath, $"+demo {demoRecord.DemoFileName}");
                    }
                    else
                    {

                        var time = physics == "vq3" ? map.VQ3Time : map.CPMTime;
                        if (!string.IsNullOrEmpty(time))
                        {
                            KillExistingProcess();
                            var demoName = $"{map.Name}[df.{physics}]{time}({_playerName}).dm_68";
                            System.Diagnostics.Process.Start(_enginePath, $"+demo {demoName}");
                        }
                        else
                        {
                            MessageBox.Show("No demo found for this time and physics mode", "Error",
                                          MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to play demo: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        private void MinimizeWindow(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }
        private void MaximizeWindow(object sender, RoutedEventArgs e)
        {
            this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        private async void RescanMaps_Click(object sender, RoutedEventArgs e)
        {
            var rescanButton = (Button)sender;
            rescanButton.IsEnabled = false;
            try
            {
                await Task.Run(() =>
                {
                    ScanMaps();
                    Dispatcher.BeginInvoke(() =>
                    {
                        UpdateFilteredMaps();
                        rescanButton.IsEnabled = true;
                    }, DispatcherPriority.Background);
                });
            }
            catch (Exception ex)
            {
                LogThumbnailMessage($"Error rescanning maps: {ex.Message}");
                Dispatcher.BeginInvoke(() =>
                {
                    MessageBox.Show("Failed to rescan maps", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    rescanButton.IsEnabled = true;
                });
            }
        }
        private async void MapsGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isLoadingThumbnails || MapsGridScroll == null) return;
            if (e.VerticalChange != 0 || e.ExtentHeightChange != 0)
            {
                await LoadVisibleThumbnails(MapsGridScroll, MapsGrid, _filteredMaps, _thumbnailLoadingCts.Token);
            }
        }
        private async Task LoadVisibleThumbnails(ScrollViewer scrollViewer, DataGrid dataGrid,
            IList<MapInfo> sourceCollection, CancellationToken token)
        {
            if (!_isWindowActive || _isLoadingThumbnails || scrollViewer == null || dataGrid == null)
                return;

            _isLoadingThumbnails = true;

            try
            {
                var visibleMaps = GetCurrentlyVisibleMaps(scrollViewer, dataGrid, sourceCollection);


                var batches = visibleMaps
                    .Where(m => m.Thumbnail == null && _mapThumbnails.ContainsKey(m.Name))
                    .Select((map, index) => new { map, index })
                    .GroupBy(x => x.index / ThumbnailBatchSize)
                    .Select(g => g.Select(x => x.map).ToList());

                foreach (var batch in batches)
                {
                    if (token.IsCancellationRequested) break;

                    var tasks = batch.Select(map =>
                    {
                        if (!_mapThumbnails.TryGetValue(map.Name, out var thumbPath))
                            return Task.CompletedTask;

                        return _thumbnailLoadSemaphore.WaitAsync(token)
                            .ContinueWith(async _ =>
                            {
                                try
                                {
                                    await LoadThumbnailAsync(map, thumbPath, token);
                                }
                                finally
                                {
                                    _thumbnailLoadSemaphore.Release();
                                }
                            }, token);
                    }).ToList();

                    await Task.WhenAll(tasks);


                    Dispatcher.Invoke(() =>
                    {
                        dataGrid.Items.Refresh();
                    }, DispatcherPriority.Background);

                    if (token.IsCancellationRequested) break;
                    await Task.Delay(100, token);
                }
            }
            catch (OperationCanceledException)
            {

            }
            catch (Exception ex)
            {
                LogThumbnailMessage($"Error in LoadVisibleThumbnails: {ex.Message}");
            }
            finally
            {
                _isLoadingThumbnails = false;
            }
        }
        private async void RecentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isLoadingThumbnails || !RecentTab.IsSelected) return;
            if (e.VerticalChange != 0 || e.ExtentHeightChange != 0)
            {
                var recentMaps = _recentMaps.Select(m => _allMaps.FirstOrDefault(am => am.Name == m))
                                          .Where(m => m != null).ToList();
                await LoadVisibleThumbnails(RecentScroll, RecentGrid, recentMaps, _thumbnailLoadingCts.Token);
            }
        }
        private async void FavoritesScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isLoadingThumbnails || !FavoritesTab.IsSelected) return;

            var token = _thumbnailLoadingCts.Token;
            var favorites = _allMaps.Where(m => _favorites.Contains(m.Name)).ToList();
            await LoadVisibleThumbnails(FavoritesScroll, FavoritesGrid, favorites, token);
        }
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }
        private void CloseWindow(object sender, RoutedEventArgs e)
        {
            Close();
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        private void ResizeWindow(object sender, MouseButtonEventArgs e, int direction)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                ReleaseCapture();
                var helper = new WindowInteropHelper(this);
                SendMessage(helper.Handle, 0x112, 0xF000 + direction, 0);
            }
        }
        private void ResizeWindow_Left(object sender, MouseButtonEventArgs e) => ResizeWindow(sender, e, 1);
        private void ResizeWindow_Right(object sender, MouseButtonEventArgs e) => ResizeWindow(sender, e, 2);
        private void ResizeWindow_Top(object sender, MouseButtonEventArgs e) => ResizeWindow(sender, e, 3);
        private void ResizeWindow_Bottom(object sender, MouseButtonEventArgs e) => ResizeWindow(sender, e, 6);
        private void ResizeWindow_TopLeft(object sender, MouseButtonEventArgs e) => ResizeWindow(sender, e, 4);
        private void ResizeWindow_TopRight(object sender, MouseButtonEventArgs e) => ResizeWindow(sender, e, 5);
        private void ResizeWindow_BottomLeft(object sender, MouseButtonEventArgs e) => ResizeWindow(sender, e, 7);
        private void ResizeWindow_BottomRight(object sender, MouseButtonEventArgs e) => ResizeWindow(sender, e, 8);
        private async Task LoadThumbnailAsync(MapInfo map, string thumbPath, CancellationToken token)
        {
            if (token.IsCancellationRequested || map.Thumbnail != null || string.IsNullOrEmpty(map.Name))
                return;

            try
            {

                if (_thumbnailCache.TryGetValue(map.Name, out var cachedBitmap))
                {
                    map.Thumbnail = cachedBitmap;
                    return;
                }
                var parts = thumbPath.Split(new[] { '|' }, 2);
                if (parts.Length != 2) return;
                var pk3Path = parts[0];
                var entryPath = parts[1].Replace('\\', '/');
                if (!File.Exists(pk3Path)) return;

                var tempFile = Path.GetTempFileName();
                try
                {
                    using (var archive = ZipFile.OpenRead(pk3Path))
                    {
                        var entry = archive.GetEntry(entryPath) ??
                                 archive.Entries.FirstOrDefault(e =>
                                     string.Equals(e.FullName, entryPath, StringComparison.OrdinalIgnoreCase));

                        if (entry == null) return;
                        entry.ExtractToFile(tempFile, overwrite: true);
                    }
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested || map.Thumbnail != null) return;

                        try
                        {
                            using (var fileStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read))
                            {
                                var originalBitmap = new BitmapImage();
                                originalBitmap.BeginInit();
                                originalBitmap.CacheOption = BitmapCacheOption.OnLoad;
                                originalBitmap.StreamSource = fileStream;
                                originalBitmap.EndInit();


                                var resizedBitmap = new TransformedBitmap(
                                    originalBitmap,
                                    new ScaleTransform(
                                        ThumbnailWidth / (double)originalBitmap.PixelWidth,
                                        ThumbnailHeight / (double)originalBitmap.PixelHeight));

                                var finalBitmap = new BitmapImage();
                                using (var memoryStream = new MemoryStream())
                                {
                                    var encoder = new JpegBitmapEncoder();
                                    encoder.Frames.Add(BitmapFrame.Create(resizedBitmap));
                                    encoder.Save(memoryStream);

                                    memoryStream.Position = 0;
                                    finalBitmap.BeginInit();
                                    finalBitmap.CacheOption = BitmapCacheOption.OnLoad;
                                    finalBitmap.StreamSource = memoryStream;
                                    finalBitmap.EndInit();
                                }

                                finalBitmap.Freeze();
                                map.Thumbnail = finalBitmap;
                                _thumbnailCache[map.Name] = finalBitmap;

                                if (MapsGrid.ItemContainerGenerator.ContainerFromItem(map) is DataGridRow container)
                                    container.InvalidateVisual();
                            }
                        }
                        catch (Exception ex)
                        {
                            LogThumbnailMessage($"Error creating bitmap for {map.Name}: {ex.Message}");
                        }
                    });
                }
                finally
                {

                    Task.Delay(5000).ContinueWith(_ =>
                    {
                        try { File.Delete(tempFile); }
                        catch { }
                    });
                }
            }
            catch (Exception ex)
            {
                LogThumbnailMessage($"Error loading thumbnail for {map.Name}: {ex.Message}");
            }
        }
        private void EnsureMgrDataDirectoryExists()
        {
            try
            {
                if (!Directory.Exists("mgrdata"))
                {
                    Directory.CreateDirectory("mgrdata");
                }
            }
            catch (Exception ex)
            {
                LogThumbnailMessage($"Error creating mgrdata directory: {ex.Message}");
            }
        }
        private void SetDarkTheme()
        {
            try
            {
                Resources["BackgroundColor"] = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                Resources["TextColor"] = Brushes.White;
                Resources["ButtonBackground"] = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
                Resources["AlternatingRowColor"] = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
                Resources["ThumbnailBackground"] = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
                Resources["TextBoxBackground"] = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
            }
            catch (Exception ex)
            {
                LogThumbnailMessage($"Error setting dark theme: {ex.Message}");
            }
        }
        private void LogSettingsMessage(string message)
        {
            try
            {
                File.AppendAllText(Path.Combine("mgrdata", "settings.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n");
            }
            catch { }
        }
        private void InitializeDefaultSettings()
        {
            Directory.CreateDirectory("mgrdata");

            if (!File.Exists(Path.Combine("mgrdata", "name.dat")))
                File.WriteAllText(Path.Combine("mgrdata", "name.dat"), "SET_YOUR_DF_NAME");
        }
        private string _windowTitle;
        public string WindowTitle
        {
            get => _windowTitle;
            set
            {
                if (_windowTitle != value)
                {
                    _windowTitle = value;
                    OnPropertyChanged(nameof(WindowTitle));
                }
            }
        }
        private bool _isWindowActive = true;
        private DateTime _lastActivityCheck = DateTime.Now;
        private void UpdateActivityState()
        {
            bool newState = this.IsActive && this.WindowState != WindowState.Minimized;
            if (_isWindowActive != newState)
            {
                _isWindowActive = newState;
                if (_isWindowActive)
                {

                    _demoCheckTimer.Start();
                    _lastActivityCheck = DateTime.Now;
                }
                else
                {

                    _demoCheckTimer.Stop();
                }
            }
        }
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            UpdateActivityState();
        }
        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            UpdateActivityState();
        }
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            UpdateActivityState();
        }

        private async Task LoadMissingThumbnails(ScrollViewer scrollViewer, DataGrid dataGrid,
            IList<MapInfo> sourceCollection, CancellationToken token)
        {
            if (!_isWindowActive || _isLoadingThumbnails || scrollViewer == null || dataGrid == null)
                return;

            _isLoadingThumbnails = true;

            try
            {
                var visibleMaps = GetCurrentlyVisibleMaps(scrollViewer, dataGrid, sourceCollection);


                var mapsToLoad = visibleMaps
                    .Where(m => m.Thumbnail == null && _mapThumbnails.ContainsKey(m.Name))
                    .ToList();

                if (!mapsToLoad.Any()) return;
                var batches = mapsToLoad
                    .Select((map, index) => new { map, index })
                    .GroupBy(x => x.index / ThumbnailBatchSize)
                    .Select(g => g.Select(x => x.map).ToList());

                foreach (var batch in batches)
                {
                    if (token.IsCancellationRequested) break;

                    var tasks = batch.Select(map =>
                    {
                        if (!_mapThumbnails.TryGetValue(map.Name, out var thumbPath))
                            return Task.CompletedTask;

                        return _thumbnailLoadSemaphore.WaitAsync(token)
                            .ContinueWith(async _ =>
                            {
                                try
                                {
                                    await LoadThumbnailAsync(map, thumbPath, token);
                                }
                                finally
                                {
                                    _thumbnailLoadSemaphore.Release();
                                }
                            }, token);
                    }).ToList();

                    await Task.WhenAll(tasks);


                    Dispatcher.Invoke(() =>
                    {
                        dataGrid.Items.Refresh();
                    }, DispatcherPriority.Background);

                    if (token.IsCancellationRequested) break;
                    await Task.Delay(100, token);
                }
            }
            catch (OperationCanceledException)
            {

            }
            catch (Exception ex)
            {
                LogThumbnailMessage($"Error in LoadMissingThumbnails: {ex.Message}");
            }
            finally
            {
                _isLoadingThumbnails = false;
            }
        }
        private void SaveThumbnailCache()
        {
            try
            {

                var existingCache = new Dictionary<string, string>();
                if (File.Exists(Path.Combine("mgrdata", "thumbnail_cache.dat")))
                {
                    foreach (var line in File.ReadAllLines(Path.Combine("mgrdata", "thumbnail_cache.dat")))
                    {
                        var parts = line.Split(new[] { '|' }, 2);
                        if (parts.Length == 2)
                        {
                            existingCache[parts[0]] = parts[1];
                        }
                    }
                }

                foreach (var kv in _thumbnailCache)
                {
                    existingCache[kv.Key] = Convert.ToBase64String(ImageToBytes(kv.Value));
                }

                var cacheLines = existingCache.Select(kv => $"{kv.Key}|{kv.Value}");
                File.WriteAllLines(Path.Combine("mgrdata", "thumbnail_cache.dat"), cacheLines);
            }
            catch (Exception ex)
            {
                LogThumbnailMessage($"Error saving thumbnail cache: {ex.Message}");
            }
        }
        private Dictionary<string, BitmapImage> LoadThumbnailCache()
        {
            var cache = new Dictionary<string, BitmapImage>();
            try
            {
                if (File.Exists(Path.Combine("mgrdata", "thumbnail_cache.dat")))
                {
                    foreach (var line in File.ReadAllLines(Path.Combine("mgrdata", "thumbnail_cache.dat")))
                    {
                        var parts = line.Split(new[] { '|' }, 2);
                        if (parts.Length == 2)
                        {
                            try
                            {
                                var bytes = Convert.FromBase64String(parts[1]);
                                var bitmap = BytesToImage(bytes);
                                bitmap.Freeze();
                                cache[parts[0]] = bitmap;
                            }
                            catch (Exception ex)
                            {
                                LogThumbnailMessage($"Error loading thumbnail for {parts[0]}: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogThumbnailMessage($"Error loading thumbnail cache: {ex.Message}");
            }
            return cache;
        }
        private byte[] ImageToBytes(BitmapImage image)
        {
            using (var ms = new MemoryStream())
            {
                var encoder = new JpegBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(ms);
                return ms.ToArray();
            }
        }
        private BitmapImage BytesToImage(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.DecodePixelWidth = ThumbnailWidth;
                bitmap.DecodePixelHeight = ThumbnailHeight;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                return bitmap;
            }
        }
        private void CleanupOldThumbnails()
        {
            try
            {
                if (!File.Exists(Path.Combine("mgrdata", "thumbnail_cache.dat"))) return;
                var lines = File.ReadAllLines(Path.Combine("mgrdata", "thumbnail_cache.dat"));
                var activeMaps = new HashSet<string>(_allMaps.Select(m => m.Name));

                var updatedLines = lines
                    .Where(line =>
                    {
                        var parts = line.Split(new[] { '|' }, 2);
                        return parts.Length == 2 && activeMaps.Contains(parts[0]);
                    })
                    .ToList();
                if (updatedLines.Count < lines.Length)
                {
                    File.WriteAllLines(Path.Combine("mgrdata", "thumbnail_cache.dat"), updatedLines);
                }
            }
            catch (Exception ex)
            {
                LogThumbnailMessage($"Error cleaning up thumbnail cache: {ex.Message}");
            }
        }
        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось открыть ссылку: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void KillExistingProcess()
        {
            try
            {
                var processName = Path.GetFileNameWithoutExtension(_enginePath);
                var processes = Process.GetProcessesByName(processName);

                foreach (var process in processes)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(1000);
                    }
                    catch (Exception ex)
                    {
                        LogSettingsMessage($"Error killing process {processName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogSettingsMessage($"Error in KillExistingProcess: {ex.Message}");
            }
        }
        private void UpdateFavoritesState()
        {
            try
            {

                foreach (var map in _allMaps)
                {
                    map.IsFavorite = _favorites.Contains(map.Name);
                }
            }
            catch (Exception ex)
            {
                LogSettingsMessage($"Error updating favorites state: {ex.Message}");
            }
        }
    }
}
