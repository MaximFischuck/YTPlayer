using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YTPlayer
{
    public partial class MainWindow : Window
    {
        private readonly MpvController _mpv;
        private bool _isDraggingSlider;
        private double _duration;
        private bool _isStarting;
        private bool _loopEnabled;
        private CancellationTokenSource? _playlistCts;
        private CancellationTokenSource? _searchCts;
        private readonly SemaphoreSlim _playSemaphore = new(1, 1);

        // ─── Очередь ───────────────────────────────────────────────────────
        private readonly ObservableCollection<QueueItem> _queue = new();
        private int _queueIndex = -1;

        // ─── HTTP для превью ───────────────────────────────────────────────
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

        // ─── Трей ──────────────────────────────────────────────────────────
        private NotifyIcon? _trayIcon;
        private ToolStripMenuItem? _trayTitle;
        private ToolStripMenuItem? _trayPause;
        private ToolStripMenuItem? _trayNext;

        // ─── Настройки ─────────────────────────────────────────────────────
        private readonly string _settingsPath;
        private bool _isLoggingEnabled = true;

        public MainWindow()
        {
            InitializeComponent();

            _settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ytplayer_settings.json");

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Log($"BaseDir: {baseDir}");

            _mpv = new MpvController(baseDir);

            Log($"mpv.com exists: {_mpv.MpvExists}");
            Log($"yt-dlp.exe exists: {_mpv.YtdlpExists}");

            if (!_mpv.MpvExists)
                System.Windows.MessageBox.Show("mpv.com не найден в папке приложения!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            if (!_mpv.YtdlpExists)
                System.Windows.MessageBox.Show("yt-dlp.exe не найден в папке приложения!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);

            QueueList.ItemsSource = _queue;
            InitTray();

            _mpv.TitleChanged += title => Dispatcher.Invoke(() =>
            {
                Log($"Title: {title}");
                TitleText.Text = "🎵  " + title;
                Title = title + " — YT Player";
                if (_queueIndex >= 0 && _queueIndex < _queue.Count)
                    _queue[_queueIndex].Title = title;
                if (_trayTitle != null) _trayTitle.Text = title.Length > 40 ? title[..40] + "…" : title;
            });

            _mpv.DurationChanged += dur => Dispatcher.Invoke(() =>
            {
                _duration = dur;
                ProgressSlider.Maximum = dur;
                ProgressSlider.IsEnabled = dur > 0;
                TimeDur.Text = FormatTime(dur);
            });

            _mpv.PositionChanged += pos => Dispatcher.Invoke(() =>
            {
                if (!_isDraggingSlider) ProgressSlider.Value = pos;
                TimePos.Text = FormatTime(pos);
            });

            _mpv.PlaybackEnded += () => Dispatcher.Invoke(() =>
            {
                Log("PlaybackEnded event fired");
                _isStarting = false;
                PlayButton.IsEnabled = true;
                UpdateTrayControls(false);

                if (_loopEnabled)
                {
                    Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        await Task.Delay(200);
                        await PlayQueueItemAsync(_queueIndex);
                    }));
                    return;
                }

                if (_queueIndex + 1 < _queue.Count)
                {
                    _queueIndex++;
                    Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        await Task.Delay(300);
                        await PlayQueueItemAsync(_queueIndex);
                    }));
                }
                else
                {
                    SetStatus("завершено", "#555555");
                    TitleText.Text = "Воспроизведение завершено";
                    PauseButton.Content = "⏸  Пауза";
                    ClearThumbnail();
                    if (_trayTitle != null) _trayTitle.Text = "Ничего не играет";
                }
            });

            _mpv.ErrorOutput += msg => Dispatcher.Invoke(() => Log($"[mpv] {msg}"));

            UrlBox.KeyDown += (s, e) => { if (e.Key == Key.Return && !_isStarting) _ = StartPlayAsync(); };
            SearchBox.KeyDown += (s, e) => { if (e.Key == Key.Return) _ = DoSearchAsync(); };

            // Сворачиваем в трей вместо закрытия
            Closing += (s, e) =>
            {
                e.Cancel = true;
                HideToTray();
            };

            LoadSettings();
        }

        // ─── Трей ──────────────────────────────────────────────────────────
        private void InitTray()
        {
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ytplayer.ico");
            var icon = File.Exists(iconPath)
                ? new Icon(iconPath)
                : SystemIcons.Application;

            _trayIcon = new NotifyIcon
            {
                Icon = icon,
                Text = "YT Player",
                Visible = true
            };

            _trayTitle = new ToolStripMenuItem("Ничего не играет") { Enabled = false };
            _trayPause = new ToolStripMenuItem("⏸  Пауза", null, TrayPause_Click);
            _trayNext = new ToolStripMenuItem("⏭  Следующий", null, TrayNext_Click);

            var menu = new ContextMenuStrip();
            menu.Items.Add(_trayTitle);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_trayPause);
            menu.Items.Add(_trayNext);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("🔲  Открыть", null, TrayOpen_Click));
            menu.Items.Add(new ToolStripMenuItem("⏹  Стоп", null, TrayStop_Click));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("✕  Выйти", null, TrayExit_Click));

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (s, e) => ShowFromTray();

            UpdateTrayControls(false);
        }

        private void HideToTray()
        {
            Hide();
            _trayIcon!.ShowBalloonTip(1500, "YT Player", "Приложение свёрнуто в трей", ToolTipIcon.None);
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void UpdateTrayControls(bool isPlaying)
        {
            if (_trayPause == null || _trayNext == null) return;
            _trayPause.Enabled = isPlaying || _mpv.IsRunning;
            _trayNext.Enabled = _queueIndex + 1 < _queue.Count;
        }

        private void TrayOpen_Click(object? s, EventArgs e) => Dispatcher.Invoke(ShowFromTray);
        private void TrayStop_Click(object? s, EventArgs e) => Dispatcher.Invoke(() => StopButton_Click(this, null!));
        private void TrayPause_Click(object? s, EventArgs e) => Dispatcher.Invoke(() => PauseButton_Click(this, null!));
        private void TrayNext_Click(object? s, EventArgs e) => Dispatcher.Invoke(() =>
        {
            if (_queueIndex + 1 < _queue.Count)
            {
                _queueIndex++;
                _ = PlayQueueItemAsync(_queueIndex);
            }
        });
        private void TrayExit_Click(object? s, EventArgs e) => Dispatcher.Invoke(() =>
        {
            _trayIcon!.Visible = false;
            _playlistCts?.Cancel();
            _searchCts?.Cancel();
            SaveSettings();
            _http.Dispose();
            _ = _mpv.StopAsync().ContinueWith(_ =>
            {
                _mpv.Dispose();
                Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
            });
        });

        // ─── Настройки ─────────────────────────────────────────────────────
        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsPath)) return;
                var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
                if (doc.RootElement.TryGetProperty("volume", out var vol))
                    VolumeSlider.Value = vol.GetDouble();
                if (doc.RootElement.TryGetProperty("loop", out var loop))
                    SetLoop(loop.GetBoolean());
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var json = JsonSerializer.Serialize(new { volume = VolumeSlider.Value, loop = _loopEnabled });
                File.WriteAllText(_settingsPath, json);
            }
            catch { }
        }

        // ─── Зацикливание ──────────────────────────────────────────────────
        private void LoopButton_Click(object sender, RoutedEventArgs e) => SetLoop(!_loopEnabled);

        private void SetLoop(bool enabled)
        {
            _loopEnabled = enabled;
            LoopButton.Content = enabled ? "🔁 Вкл" : "🔁 Цикл";
            LoopButton.Tag = enabled ? "on" : "off";
        }

        // ─── Превью ────────────────────────────────────────────────────────
        private async Task LoadThumbnailAsync(string url)
        {
            try
            {
                var videoId = ExtractVideoId(url);
                if (string.IsNullOrEmpty(videoId)) return;

                var bytes = await _http.GetByteArrayAsync($"https://img.youtube.com/vi/{videoId}/mqdefault.jpg");

                await Dispatcher.InvokeAsync(() =>
                {
                    using var ms = new MemoryStream(bytes);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = ms;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    ThumbnailImage.Source = bmp;
                    ThumbnailPanel.Visibility = Visibility.Visible;
                });
            }
            catch { ClearThumbnail(); }
        }

        private void ClearThumbnail()
        {
            ThumbnailImage.Source = null;
            ThumbnailPanel.Visibility = Visibility.Collapsed;
        }

        private static string ExtractVideoId(string url)
        {
            try
            {
                var uri = new Uri(url);
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                return query["v"] ?? "";
            }
            catch { return ""; }
        }

        // ─── Поиск YouTube ─────────────────────────────────────────────────
        private async void SearchButton_Click(object sender, RoutedEventArgs e) => await DoSearchAsync();

        private async Task DoSearchAsync()
        {
            var query = SearchBox.Text.Trim();
            if (string.IsNullOrEmpty(query)) return;

            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;

            SearchButton.IsEnabled = false;
            SearchButton.Content = "⏳";
            SearchResultsList.Items.Clear();
            SearchResultsList.Items.Add(new SearchResult { Title = "Поиск...", Url = "" });

            try
            {
                var ytdlpPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yt-dlp.exe");
                var results = await Task.Run(() =>
                {
                    var list = new System.Collections.Generic.List<SearchResult>();
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = ytdlpPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                    };
                    psi.ArgumentList.Add("ytsearch5:" + query);
                    psi.ArgumentList.Add("--print");
                    psi.ArgumentList.Add("%(title)s\t%(webpage_url)s\t%(duration_string)s");
                    psi.ArgumentList.Add("--no-playlist");

                    var proc = System.Diagnostics.Process.Start(psi)!;
                    ct.Register(() => { try { proc.Kill(true); } catch { } });

                    while (!proc.StandardOutput.EndOfStream)
                    {
                        if (ct.IsCancellationRequested) break;
                        var line = proc.StandardOutput.ReadLine();
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var parts = line.Split('\t');
                        if (parts.Length >= 2)
                            list.Add(new SearchResult { Title = parts[0], Url = parts[1], Duration = parts.Length >= 3 ? parts[2] : "" });
                    }
                    proc.WaitForExit();
                    return list;
                }, ct);

                if (ct.IsCancellationRequested) return;

                SearchResultsList.Items.Clear();
                foreach (var r in results.Count > 0 ? results : new() { new SearchResult { Title = "Ничего не найдено", Url = "" } })
                    SearchResultsList.Items.Add(r);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    SearchResultsList.Items.Clear();
                    SearchResultsList.Items.Add(new SearchResult { Title = $"Ошибка: {ex.Message}", Url = "" });
                }
            }
            finally
            {
                if (!ct.IsCancellationRequested)
                {
                    SearchButton.IsEnabled = true;
                    SearchButton.Content = "🔍";
                }
            }
        }

        private void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SearchResultsList.SelectedItem is SearchResult r && !string.IsNullOrEmpty(r.Url))
            { UrlBox.Text = r.Url; _ = StartPlayAsync(); }
        }

        private void SearchPlay_Click(object sender, RoutedEventArgs e)
        {
            if (SearchResultsList.SelectedItem is SearchResult r && !string.IsNullOrEmpty(r.Url))
            { UrlBox.Text = r.Url; _ = StartPlayAsync(); }
        }

        private void AddToQueueFromSearch_Click(object sender, RoutedEventArgs e)
        {
            if (SearchResultsList.SelectedItem is SearchResult r && !string.IsNullOrEmpty(r.Url))
                AddToQueue(r.Url, r.Title);
        }

        // ─── Очередь ───────────────────────────────────────────────────────
        private void AddToQueue(string url, string title = "")
        {
            _queue.Add(new QueueItem { Url = url, Title = string.IsNullOrEmpty(title) ? url : title });
            Log($"Добавлено в очередь: {(string.IsNullOrEmpty(title) ? url : title)}");
            UpdateTrayControls(_mpv.IsRunning);
        }

        private void AddCurrentToQueue_Click(object sender, RoutedEventArgs e)
        {
            var url = UrlBox.Text.Trim();
            if (!string.IsNullOrEmpty(url)) AddToQueue(url);
        }

        private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var idx = QueueList.SelectedIndex;
            if (idx < 0 || idx >= _queue.Count) return;
            _queueIndex = idx;
            _ = PlayQueueItemAsync(idx);
        }

        private void QueueRemove_Click(object sender, RoutedEventArgs e)
        {
            var idx = QueueList.SelectedIndex;
            if (idx < 0 || idx >= _queue.Count) return;
            _queue.RemoveAt(idx);
            if (_queueIndex >= idx) _queueIndex--;
            UpdateTrayControls(_mpv.IsRunning);
        }

        private void QueueClear_Click(object sender, RoutedEventArgs e)
        {
            _playlistCts?.Cancel();
            _queue.Clear();
            _queueIndex = -1;
            UpdateTrayControls(_mpv.IsRunning);
        }

        private async Task PlayQueueItemAsync(int idx)
        {
            if (idx < 0 || idx >= _queue.Count) return;
            UrlBox.Text = _queue[idx].Url;
            await StartPlayAsync();
        }

        // ─── Воспроизведение ───────────────────────────────────────────────
        private void Log(string msg)
        {
            if (!_isLoggingEnabled) return;
            var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            LogBox.AppendText(line + "\n");
            if (LogBox.LineCount > 200)
            {
                var text = LogBox.Text;
                var firstNewline = text.IndexOf('\n');
                if (firstNewline >= 0) LogBox.Text = text[(firstNewline + 1)..];
            }
            LogBox.ScrollToEnd();
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e) => await StartPlayAsync();

        private async Task StartPlayAsync()
        {
            var url = UrlBox.Text.Trim();
            if (string.IsNullOrEmpty(url))
            { System.Windows.MessageBox.Show("Вставь ссылку на YouTube видео или плейлист", "Нет ссылки"); return; }

            // Если уже запускается — игнорируем
            if (!_playSemaphore.Wait(0)) return;
            _isStarting = true;
            PlayButton.IsEnabled = false;

            try
            {
                if (_mpv.IsRunning)
                {
                    await _mpv.StopAsync();
                    await Task.Delay(300);
                }

                if (IsPlaylistUrl(url))
                {
                    await LoadPlaylistAsync(url);
                    return;
                }

                var volume = (int)VolumeSlider.Value;
                Log($"Starting playback: {url}");
                SetStatus("загрузка...", "#ffaa00");
                TitleText.Text = "⏳  Загружается...";
                ProgressSlider.Value = 0;
                ProgressSlider.IsEnabled = false;
                TimePos.Text = "0:00";
                TimeDur.Text = "0:00";
                PauseButton.Content = "⏸  Пауза";
                _duration = 0;
                ClearThumbnail();

                _ = LoadThumbnailAsync(url);

                await _mpv.PlayAsync(url, volume);
                Log("PlayAsync returned");
                SetStatus("играет", "#44cc77");
                UpdateTrayControls(true);
            }
            finally
            {
                _isStarting = false;
                PlayButton.IsEnabled = true;
                _playSemaphore.Release();
            }
        }

        private static bool IsPlaylistUrl(string url) => url.Contains("list=");

        private async Task LoadPlaylistAsync(string url)
        {
            _playlistCts?.Cancel();
            _playlistCts = new CancellationTokenSource();
            var ct = _playlistCts.Token;

            Log("Определён плейлист, загружаю треки...");
            SetStatus("загрузка плейлиста...", "#ffaa00");
            TitleText.Text = "⏳  Загружаю плейлист...";

            var ytdlpPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yt-dlp.exe");
            var firstItemIndex = _queue.Count;
            var firstPlayed = false;

            try
            {
                await Task.Run(async () =>
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = ytdlpPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                    };
                    psi.ArgumentList.Add(url);
                    psi.ArgumentList.Add("--print");
                    psi.ArgumentList.Add("%(title)s\t%(webpage_url)s");
                    psi.ArgumentList.Add("--yes-playlist");
                    psi.ArgumentList.Add("--flat-playlist");
                    psi.ArgumentList.Add("--no-warnings");

                    var proc = System.Diagnostics.Process.Start(psi)!;
                    ct.Register(() => { try { proc.Kill(true); } catch { } });

                    while (!proc.StandardOutput.EndOfStream)
                    {
                        if (ct.IsCancellationRequested) break;
                        var line = await proc.StandardOutput.ReadLineAsync();
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var parts = line.Split('\t');
                        if (parts.Length < 2) continue;

                        var item = new QueueItem { Title = parts[0], Url = parts[1] };
                        await Dispatcher.InvokeAsync(() =>
                        {
                            _queue.Add(item);
                            Log($"[плейлист] {_queue.Count - firstItemIndex}. {parts[0]}");
                        });

                        if (!firstPlayed)
                        {
                            firstPlayed = true;
                            await Dispatcher.InvokeAsync(async () =>
                            {
                                _queueIndex = firstItemIndex;
                                UrlBox.Text = item.Url;
                                await StartPlayAsync();
                            });
                        }
                    }
                    proc.WaitForExit();
                }, ct);

                if (!ct.IsCancellationRequested)
                    Log($"Плейлист загружен: {_queue.Count - firstItemIndex} треков");
            }
            catch (OperationCanceledException) { Log("Загрузка плейлиста отменена"); }
            catch (Exception ex)
            {
                Log($"Ошибка загрузки плейлиста: {ex.Message}");
                SetStatus("ошибка", "#cc2222");
                TitleText.Text = "Ошибка загрузки плейлиста";
            }
        }

        // ─── Пауза / Стоп ──────────────────────────────────────────────────
        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_mpv.IsRunning) return;
            _ = _mpv.PauseAsync();
            var isPause = PauseButton.Content.ToString()?.Contains("Пауза") ?? false;
            PauseButton.Content = isPause ? "▶  Играть" : "⏸  Пауза";
            SetStatus(isPause ? "пауза" : "играет", isPause ? "#ffaa00" : "#44cc77");
            if (_trayPause != null) _trayPause.Text = isPause ? "▶  Играть" : "⏸  Пауза";
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _playlistCts?.Cancel();
            TitleText.Text = "Ничего не играет";
            Title = "YT Player";
            ProgressSlider.Value = 0;
            ProgressSlider.IsEnabled = false;
            TimePos.Text = "0:00";
            TimeDur.Text = "0:00";
            PauseButton.Content = "⏸  Пауза";
            SetStatus("остановлено", "#444444");
            ClearThumbnail();
            if (_trayTitle != null) _trayTitle.Text = "Ничего не играет";
            if (_trayPause != null) _trayPause.Text = "⏸  Пауза";
            UpdateTrayControls(false);

            await _mpv.StopAsync();
            _ = Task.Run(() => { GC.Collect(); GC.WaitForPendingFinalizers(); });
        }

        private void Btn_Seek(object sender, RoutedEventArgs e)
        {
            if (!_mpv.IsRunning) return;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tagStr && double.TryParse(tagStr, out var sec))
                _ = _mpv.SeekAsync(sec);
        }

        // ─── Прогресс-бар ──────────────────────────────────────────────────
        private void ProgressSlider_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!ProgressSlider.IsEnabled) return;
            _isDraggingSlider = true;
            var track = ((Slider)sender).Template.FindName("PART_Track", (Slider)sender) as Track;
            if (track == null) return;
            var pos = track.ValueFromPoint(e.GetPosition(track));
            ProgressSlider.Value = pos;
            if (_mpv.IsRunning) _ = _mpv.SeekAbsoluteAsync(pos);
        }

        private void ProgressSlider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingSlider && _mpv.IsRunning) _ = _mpv.SeekAbsoluteAsync(ProgressSlider.Value);
            _isDraggingSlider = false;
        }

        private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDraggingSlider) TimePos.Text = FormatTime(e.NewValue);
        }

        // ─── Громкость ─────────────────────────────────────────────────────
        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var vol = (int)e.NewValue;
            if (VolumeText != null) VolumeText.Text = $"{vol}%";
            if (_mpv?.IsRunning == true) _ = _mpv.SetVolumeAsync(vol);
        }

        private void VolumeSlider_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + (e.Delta > 0 ? 5 : -5), VolumeSlider.Minimum, VolumeSlider.Maximum);
            e.Handled = true;
        }

        // ─── Утилиты ───────────────────────────────────────────────────────
        private void SetStatus(string text, string colorHex)
        {
            StatusText.Text = text;
            StatusDot.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex));
        }

        private static string FormatTime(double seconds)
        {
            if (seconds <= 0) return "0:00";
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes}:{ts.Seconds:D2}";
        }
    }

    public class SearchResult
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Duration { get; set; } = "";
        public override string ToString() => Duration.Length > 0 ? $"[{Duration}] {Title}" : Title;
    }

    public class QueueItem
    {
        public string Url { get; set; } = "";
        public string Title { get; set; } = "";
        public override string ToString() => Title;
    }
}
