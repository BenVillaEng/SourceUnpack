using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SourceUnpack.Core.Services;

public class SteamCmdService : IDisposable
{
    private Process? _process;
    public event EventHandler<string>? OutputReceived;
    public bool IsRunning => _process != null && !_process.HasExited;

    private string? _customPath;

    public void SetCustomPath(string path)
    {
        _customPath = path;
    }

    private string FindExePath()
    {
        string? exePath = _customPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            string appRoot = AppDomain.CurrentDomain.BaseDirectory;
            string subPath = Path.Combine(appRoot, "steamcmd", "steamcmd.exe");
            string localPath = Path.Combine(appRoot, "steamcmd.exe");

            if (File.Exists(subPath)) exePath = subPath;
            else if (File.Exists(localPath)) exePath = localPath;
            else throw new FileNotFoundException("steamcmd.exe not found in root or ./steamcmd/");
        }
        return exePath;
    }

    /// <summary>
    /// Start SteamCMD in interactive mode with its own visible console window.
    /// SteamCMD does NOT output to redirected stdout — it writes directly to the
    /// console handle, so a visible window is required to see its output.
    /// </summary>
    public void Start(string arguments = "")
    {
        if (IsRunning) return;

        string exePath = FindExePath();

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(exePath)
        };

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.Exited += (s, e) =>
        {
            OutputReceived?.Invoke(this, "[System] SteamCMD window closed.");
            _process = null;
        };

        try
        {
            _process.Start();
            OutputReceived?.Invoke(this, $"[System] SteamCMD opened in its own window.");
            OutputReceived?.Invoke(this, "[Info] Check your taskbar for the SteamCMD console window.");
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke(this, $"[Error] Failed to start SteamCMD: {ex.Message}");
            _process = null;
        }
    }

    /// <summary>
    /// Run a one-shot batch download. Starts SteamCMD with +login anonymous
    /// and the given command, then +quit. Shows in its own console window.
    /// </summary>
    public void RunBatch(string command)
    {
        if (IsRunning)
        {
            OutputReceived?.Invoke(this, "[Warning] SteamCMD is already running. Stop it first.");
            return;
        }

        string args = $"+login anonymous {command} +quit";
        OutputReceived?.Invoke(this, $"[System] Running: steamcmd {args}");
        Start(args);
    }

    public void SendCommand(string command)
    {
        if (!IsRunning || _process == null)
        {
            OutputReceived?.Invoke(this, "[Error] SteamCMD is not running.");
            return;
        }

        try
        {
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
            OutputReceived?.Invoke(this, $"> {command}");
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke(this, $"[Error] Failed to send command: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_process != null && !_process.HasExited)
        {
            try
            {
                _process.StandardInput.WriteLine("quit");
                _process.StandardInput.Flush();
                if (!_process.WaitForExit(3000))
                    _process.Kill();
                _process.Dispose();
            }
            catch { }
        }
        _process = null;
        OutputReceived?.Invoke(this, "[System] SteamCMD stopped.");
    }

    public void Dispose()
    {
        Stop();
    }
}
