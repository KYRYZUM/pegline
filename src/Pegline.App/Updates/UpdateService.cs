using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using Pegline.App.Localization;
using Pegline.Core.Storage;
using Velopack;
using Velopack.Sources;

namespace Pegline.App.Updates;

/// <summary>
/// App updates from GitHub Releases (Velopack). Checks at launch and every hour when allowed; downloading and installing
/// start only when the user chooses. A token for the private repository is kept encrypted for this Windows user (DPAPI).
/// </summary>
internal sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/KYRYZUM/pegline";
    public const string ReleasesUrl = RepositoryUrl + "/releases";

    private static readonly Lazy<bool> Installed = new(() =>
    {
        try
        {
            return new UpdateManager(new GithubSource(RepositoryUrl, null, false)).IsInstalled;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
        {
            return false;
        }
    });

    private readonly SettingsStore _settings;
    private readonly Action _beforeRestart;
    private readonly string _tokenFile;
    private readonly DispatcherTimer _timer;
    private UpdateInfo? _available;
    private string? _notifiedVersion;

    public UpdateService(SettingsStore settings, Dispatcher dispatcher, string dataDirectory, Action beforeRestart)
    {
        _settings = settings;
        _beforeRestart = beforeRestart;
        _tokenFile = Path.Combine(dataDirectory, "github-token.bin");
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromHours(1) };
        _timer.Tick += (_, _) => _ = CheckAsync(manual: false);
        Status = IsInstalled ? Loc.T("Not checked yet") : Loc.T("This copy was built from source, so it does not update itself. The installer version updates from GitHub Releases.");
    }

    /// <summary>True only for a copy installed by Pegline's setup; builds run from source never update themselves.</summary>
    public static bool IsInstalledBuild => Installed.Value;

    public bool IsInstalled => IsInstalledBuild;
    public bool Busy { get; private set; }
    public int? Progress { get; private set; }
    public string Status { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }
    public string? AvailableVersion => _available?.TargetFullRelease.Version.ToString();
    public string? ReleaseNotesUrl => AvailableVersion is { } version ? ReleasesUrl + "/tag/v" + version : null;
    public bool HasToken => File.Exists(_tokenFile);

    /// <summary>Raised on the UI thread whenever anything shown in Settings changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised once per new version found by an automatic check.</summary>
    public event EventHandler<string>? Found;

    /// <summary>Starts the launch check (after the app settles) and the hourly checks.</summary>
    public void Start()
    {
        if (!IsInstalled) return;
        _timer.Start();
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => _timer.Dispatcher.BeginInvoke(() => CheckAsync(manual: false)));
    }

    public async Task CheckAsync(bool manual)
    {
        if (!IsInstalled || Busy || (!manual && !_settings.Current.AutoCheckUpdates)) return;
        Busy = true;
        Error = null;
        Progress = null;
        Status = Loc.T("Checking for updates…");
        Raise();
        try
        {
            var manager = Manager();
            _available = await manager.CheckForUpdatesAsync();
            LastChecked = DateTimeOffset.Now;
            Status = _available is null
                ? Loc.F("Pegline {0} is up to date.", manager.CurrentVersion?.ToString() ?? "")
                : Loc.F("Pegline {0} is available.", AvailableVersion!);
            if (_available is not null && !manual && _notifiedVersion != AvailableVersion)
            {
                _notifiedVersion = AvailableVersion;
                Found?.Invoke(this, AvailableVersion!);
            }
        }
        catch (Exception ex) when (IsNetworkOrFeedFailure(ex))
        {
            App.Log(ex);
            Status = Loc.T("Could not check for updates.");
            Error = Explain(ex);
        }
        finally
        {
            Busy = false;
            Raise();
        }
    }

    /// <summary>Downloads the update the user chose, then quits, installs it and reopens Pegline.</summary>
    public async Task InstallAsync()
    {
        if (!IsInstalled || Busy || _available is not { } update) return;
        Busy = true;
        Error = null;
        Progress = 0;
        Status = Loc.F("Downloading Pegline {0}…", AvailableVersion!);
        Raise();
        try
        {
            var manager = Manager();
            await manager.DownloadUpdatesAsync(update, percent => _timer.Dispatcher.BeginInvoke(() =>
            {
                Progress = percent;
                Raise();
            }));
            Status = Loc.T("Installing and reopening…");
            Raise();
            _beforeRestart();
            manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
        }
        catch (Exception ex) when (IsNetworkOrFeedFailure(ex))
        {
            App.Log(ex);
            Status = Loc.T("The update could not be installed.");
            Error = Explain(ex);
            Busy = false;
            Progress = null;
            Raise();
        }
    }

    public void SaveToken(string token)
    {
        token = token.Trim();
        if (token.Length == 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_tokenFile)!);
        File.WriteAllBytes(_tokenFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
        Raise();
    }

    public void ForgetToken()
    {
        if (File.Exists(_tokenFile)) File.Delete(_tokenFile);
        Raise();
    }

    private string? Token()
    {
        try
        {
            return File.Exists(_tokenFile) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_tokenFile), null, DataProtectionScope.CurrentUser)) : null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            App.Log(ex);
            return null;
        }
    }

    private UpdateManager Manager() => new(new GithubSource(RepositoryUrl, Token(), false));

    private static bool IsNetworkOrFeedFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or InvalidDataException
            or System.Text.Json.JsonException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;

    private string Explain(Exception ex) =>
        ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
            ? Loc.T(HasToken ? "GitHub refused the saved token. Check that it can read this repository." : "The releases are private. Save a GitHub token below to receive updates.")
            : ex is HttpRequestException or TaskCanceledException ? Loc.T("GitHub could not be reached. Pegline will try again later.") : ex.Message;

    private void Raise()
    {
        if (_timer.Dispatcher.CheckAccess()) Changed?.Invoke(this, EventArgs.Empty);
        else _timer.Dispatcher.BeginInvoke(() => Changed?.Invoke(this, EventArgs.Empty));
    }
}
