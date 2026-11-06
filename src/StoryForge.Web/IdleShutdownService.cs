using Microsoft.AspNetCore.Components.Server.Circuits;

namespace StoryForge.Web;

// 目前有幾個瀏覽器分頁還開著（還沒關閉的 Blazor circuit 數）。Singleton：整個程序共用一份。
// 算的是 circuit 而不是連線：電腦睡眠、視窗縮小被 Chrome 節流、網路抖一下都會讓連線暫時中斷，但分頁其實還開著，
// 等一下就會自己重連；只有真的關掉分頁（或重新整理）時 blazor.web.js 才會送出 disconnect 通知讓 circuit 關閉。
public sealed class ClientConnectionTracker
{
    private readonly object _gate = new();
    private int _openCircuits;
    private bool _everConnected;

    public void CircuitOpened()
    {
        lock (_gate)
        {
            _openCircuits++;
            _everConnected = true;
        }
    }

    public void CircuitClosed()
    {
        lock (_gate)
            _openCircuits = Math.Max(0, _openCircuits - 1);
    }

    public (int OpenCircuits, bool EverConnected) Snapshot()
    {
        lock (_gate)
            return (_openCircuits, _everConnected);
    }
}

// 每個 circuit 一個實例：circuit 開啟／關閉時通知 ClientConnectionTracker。
public sealed class ClientConnectionCircuitHandler : CircuitHandler
{
    private readonly ClientConnectionTracker _tracker;
    private bool _isOpen;

    public ClientConnectionCircuitHandler(ClientConnectionTracker tracker)
    {
        _tracker = tracker;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (!_isOpen)
        {
            _isOpen = true;
            _tracker.CircuitOpened();
        }

        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_isOpen)
        {
            _isOpen = false;
            _tracker.CircuitClosed();
        }

        return Task.CompletedTask;
    }
}

// 由 Launcher 啟動時（環境變數 STORYFORGE_EXIT_WHEN_NO_CLIENT=1）才啟用：所有分頁都關閉超過 IdleTimeout
// 就自行結束程序。Chrome 的 app 視窗關閉後有時仍在背景執行，Launcher 會一直等不到它結束，
// 這時靠後端自己收掉，不會再留下來鎖住 publish 資料夾。
// 重新整理頁面會先關掉舊 circuit 再開新的，所以要等一段時間才判定為關閉；啟動後若一直沒有任何連線（例如 Chrome
// 沒能開啟），用較長的 StartupTimeout。用 dotnet watch 開發時沒有這個環境變數，不受影響。
// 閒置時間是一輪一輪累加、每輪最多算 MaxCountedPerPoll，不是拿現在時間去減：電腦睡眠醒來時這個迴圈
// 會一次看到好幾小時過去，若照實算，瀏覽器還來不及重連後端就先自己結束了。
public sealed class IdleShutdownService : BackgroundService
{
    private const string EnableVariable = "STORYFORGE_EXIT_WHEN_NO_CLIENT";
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxCountedPerPoll = TimeSpan.FromSeconds(2);

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

        _logger.LogInformation("所有分頁關閉 {Idle} 秒後將自動結束（啟動後 {Startup} 秒內必須連線）。",
            IdleTimeout.TotalSeconds, StartupTimeout.TotalSeconds);

        var idle = TimeSpan.Zero;
        var lastPollUtc = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);

            var nowUtc = DateTime.UtcNow;
            var elapsed = nowUtc - lastPollUtc;
            lastPollUtc = nowUtc;

            var (openCircuits, everConnected) = _tracker.Snapshot();
            if (openCircuits > 0)
            {
                idle = TimeSpan.Zero;
                continue;
            }

            idle += elapsed < MaxCountedPerPoll ? elapsed : MaxCountedPerPoll;
            var limit = everConnected ? IdleTimeout : StartupTimeout;
            if (idle < limit)
                continue;

            _logger.LogInformation("已 {Seconds} 秒沒有開著的分頁，自動結束 StoryForge。", limit.TotalSeconds);
            _lifetime.StopApplication();
            return;
        }
    }
}
