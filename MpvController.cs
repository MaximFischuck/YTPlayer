using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace YTPlayer
{
    public class MpvController : IDisposable
    {
        private Process? _process;
        private NamedPipeClientStream? _pipe;
        private StreamWriter? _writer;
        private readonly string _mpvPath;
        private readonly string _ytdlpPath;
        private string _pipeName = "ytplayer_mpv_pipe";
        private bool _connected;
        private bool _stopping;

        public event Action<string>? TitleChanged;
        public event Action<double>? DurationChanged;
        public event Action<double>? PositionChanged;
        public event Action? PlaybackEnded;
        public event Action<string>? ErrorOutput;

        public MpvController(string basePath)
        {
            _mpvPath = Path.Combine(basePath, "mpv.exe");
            _ytdlpPath = Path.Combine(basePath, "yt-dlp.exe");
        }

        public bool MpvExists => File.Exists(_mpvPath);
        public bool YtdlpExists => File.Exists(_ytdlpPath);

        public async Task PlayAsync(string url, int volume = 80)
        {
            await StopAsync();

            _pipeName = $"ytplayer_mpv_{Guid.NewGuid():N}";
            _stopping = false;

            ErrorOutput?.Invoke($"Args: --no-video --msg-level=all=warn --script-opts=ytdl_hook-ytdl_path={_ytdlpPath} --volume={volume} --input-ipc-server={_pipeName} {url}");

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _mpvPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true
            };

            _process.StartInfo.ArgumentList.Add("--no-video");
            _process.StartInfo.ArgumentList.Add("--no-terminal");
            _process.StartInfo.ArgumentList.Add("--msg-level=all=warn");
            _process.StartInfo.ArgumentList.Add($"--script-opts=ytdl_hook-ytdl_path={_ytdlpPath}");
            _process.StartInfo.ArgumentList.Add($"--volume={volume}");
            _process.StartInfo.ArgumentList.Add($"--input-ipc-server={_pipeName}");
            _process.StartInfo.ArgumentList.Add(url);

            _process.Exited += (s, e) =>
            {
                ErrorOutput?.Invoke($"mpv exited with code: {_process?.ExitCode}");
                // Не вызываем PlaybackEnded если сами остановили
                if (!_stopping)
                    PlaybackEnded?.Invoke();
            };

            _process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null) ErrorOutput?.Invoke($"[mpv] {e.Data}");
            };

            _process.Start();
            _process.BeginErrorReadLine();

            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                if (_process != null && _process.HasExited)
                    ErrorOutput?.Invoke($"mpv died early! ExitCode={_process.ExitCode}");
                else
                    ErrorOutput?.Invoke("mpv is running OK");
            });

            _ = Task.Run(ConnectPipeAsync);
        }

        private async Task ConnectPipeAsync()
        {
            await Task.Delay(1500);
            for (int i = 0; i < 15; i++)
            {
                if (_stopping) return;
                try
                {
                    _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await _pipe.ConnectAsync(1000);
                    _writer = new StreamWriter(_pipe, Encoding.UTF8) { AutoFlush = true };
                    _connected = true;

                    await SendCommandAsync("observe_property", 1, "media-title");
                    await SendCommandAsync("observe_property", 2, "duration");
                    await SendCommandAsync("observe_property", 3, "time-pos");

                    _ = Task.Run(ReadLoopAsync);
                    return;
                }
                catch
                {
                    _pipe?.Dispose();
                    _pipe = null;
                    await Task.Delay(300);
                }
            }
            ErrorOutput?.Invoke("IPC pipe: не удалось подключиться после 15 попыток");
        }

        private async Task ReadLoopAsync()
        {
            if (_pipe == null) return;
            var reader = new StreamReader(_pipe, Encoding.UTF8);
            try
            {
                while (_connected && _pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;
                    ParseEvent(line);
                }
            }
            catch { }
        }

        private void ParseEvent(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("event", out var ev) && ev.GetString() == "property-change")
                {
                    if (!root.TryGetProperty("name", out var nameProp)) return;
                    var name = nameProp.GetString();
                    if (!root.TryGetProperty("data", out var data)) return;

                    if (name == "media-title" && data.ValueKind == JsonValueKind.String)
                        TitleChanged?.Invoke(data.GetString() ?? "");
                    if (name == "duration" && data.ValueKind == JsonValueKind.Number)
                        DurationChanged?.Invoke(data.GetDouble());
                    if (name == "time-pos" && data.ValueKind == JsonValueKind.Number)
                        PositionChanged?.Invoke(data.GetDouble());
                }
            }
            catch { }
        }

        public Task PauseAsync() => SendCommandAsync("cycle", "pause");
        public Task SeekAsync(double seconds) => SendCommandAsync("seek", seconds, "relative");
        public Task SeekAbsoluteAsync(double seconds) => SendCommandAsync("seek", seconds, "absolute");
        public Task SetVolumeAsync(int vol) => SendCommandAsync("set_property", "volume", vol);

        private async Task SendCommandAsync(params object[] args)
        {
            if (!_connected || _writer == null) return;
            try
            {
                var cmd = new { command = args };
                var json = JsonSerializer.Serialize(cmd);
                await _writer.WriteLineAsync(json);
            }
            catch { _connected = false; }
        }

        public async Task StopAsync()
        {
            _stopping = true;
            _connected = false;

            // Закрываем pipe
            try { _writer?.Close(); } catch { }
            try { _pipe?.Close(); _pipe?.Dispose(); } catch { }
            _writer = null;
            _pipe = null;

            var proc = _process;
            _process = null;

            if (proc != null)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        ErrorOutput?.Invoke($"Killing mpv PID={proc.Id}...");
                        proc.Kill(entireProcessTree: true);

                        // Ждём завершения в фоне с таймаутом — не блокируем UI поток
                        var exited = await Task.Run(() => proc.WaitForExit(3000));
                        if (exited)
                            ErrorOutput?.Invoke("mpv killed OK");
                        else
                            ErrorOutput?.Invoke("mpv kill timeout — продолжаем");
                    }
                    else
                    {
                        ErrorOutput?.Invoke("mpv already exited");
                    }
                }
                catch (Exception ex)
                {
                    ErrorOutput?.Invoke($"Kill failed: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        public bool IsRunning => _process != null && !_process.HasExited;

        public void Dispose()
        {
            _ = StopAsync();
        }
    }
}
