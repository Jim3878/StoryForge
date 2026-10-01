using Microsoft.AspNetCore.Components.Server.Circuits;

namespace StoryForge.Web;

// 目前有幾個瀏覽器分頁連著後端（Blazor circuit 的連線數）。Singleton：整個程序共用一份。
public sealed class ClientConnectionTracker
{
    private readonly object _gate = new();
    private int _connections;
    private bool _everConnected;
    private DateTime _lastChangeUtc = DateTime.UtcNow;

    public void ConnectionUp()
    {
        lock (_gate)
        {
            _connections++;
            _everConnected = true;
            _lastChangeUtc = DateTime.UtcNow;
        }
    }

    public void ConnectionDown()
    {
        lock (_gate)
        {
            _connections = Math.Max(0, _connections - 1);
            _lastChangeUtc = DateTime.UtcNow;
        }
    }

    public (int Connections, bool EverConnected, DateTime LastChangeUtc) Snapshot()
    {
        lock (_gate)
            return (_connections, _everConnected, _lastChangeUtc);
    }
}

// 每個 circuit 一個實例：連線建立／中斷時通知 ClientConnectionTracker。
// circuit 關閉時若連線還算在線上（例如沒有先收到 ConnectionDown），也補扣一次，避免計數殘留。
public sealed class ClientConnectionCircuitHandler : CircuitHandler
{
    private readonly ClientConnectionTracker _tracker;
    private bool _isUp;

    public ClientConnectionCircuitHandler(ClientConnectionTracker tracker)
    {
        _tracker = tracker;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (!_isUp)
        {
            _isUp = true;
            _tracker.ConnectionUp();
        }

        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        MarkDown();
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        MarkDown();
        return Task.CompletedTask;
    }

    private void MarkDown()
    {
        if (!_isUp)
            return;

        _isUp = false;
        _tracker.ConnectionDown();
    }
}

// 由 Launcher 啟動時（環境變數 STORYFORGE_EXIT_WHEN_NO_CLIENT=1）才啟用：所有瀏覽器連線都中斷超過
// IdleTimeout 就自行結束程序。Chrome 的 app 視窗關閉後有時仍在背景執行，Launcher 會一直等不到它結束，
// 這時靠後端自己收掉，不會再留下來鎖住 publish 資料夾。
// 重新整理頁面會短暫斷線再重連，所以要等一段時間才判定為關閉；啟動後若一直沒有任何連線（例如 Chrome
// 沒能開啟），用較長的 StartupTimeout。用 dotnet watch 開發時沒有這個環境變數，不受影響。
public sealed class IdleShutdownService : BackgroundService
{
    private const string EnableVariable = "STORYFORGE_EXIT_WHEN_NO_CLIENT";
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ClientConnectionTracker _tracker;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<IdleShutdownService> _logger;

    public IdleShutdownService(ClientConnectionTracker tracker, IHostApplicationLifetime lifetime,
        ILogger<IdleShutdownService> logger)
    {
        _tracker = tracker;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
            return;

        _logger.LogInformation("沒有瀏覽器連線 {Idle} 秒後將自動結束（啟動後 {Startup} 秒內必須連線）。",
            IdleTimeout.TotalSeconds, StartupTimeout.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);

            var (connections, everConnected, lastChangeUtc) = _tracker.Snapshot();
            if (connections > 0)
                continue;

            var limit = everConnected ? IdleTimeout : StartupTimeout;
            if (DateTime.UtcNow - lastChangeUtc < limit)
                continue;

            _logger.LogInformation("已 {Seconds} 秒沒有瀏覽器連線，自動結束 StoryForge。", limit.TotalSeconds);
            _lifetime.StopApplication();
            return;
        }
    }
}
