using System.Globalization;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PlayerokBridge.Core;
using PlayerokBridge.Playerok;
using static PlayerokBridge.Services.EventFormatter;

namespace PlayerokBridge.Services;

/// <summary>
/// «Двигатель» приложения: подключается к PlayerOK и Telegram, гоняет события,
/// отправляет автоответы и уведомления, принимает команды администратора.
/// </summary>
public sealed class AppHost : IDisposable
{
    public AppSettings Settings { get; private set; }
    public NotifySettings Notify { get; }
    public ProductReplyStore Products { get; }
    public QuickReplyStore Quick { get; }
    public ReviewSettings Review { get; }
    public AfkSettings Afk { get; }
    public StaleDealSettings Stale { get; }

    public event Action<string>? Log;
    private Process? _pythonBot;
    private PlayerokClient? _uiClient;
    public bool IsRunning => _pythonBot is { HasExited: false };

    // Legacy Telegram runtime fields are retained for the legacy command handlers.
    // Explicit null initializers document that the Python bot owns that runtime.
    private PlayerokClient? _client = null;
    private TelegramApi? _tg = null;
    private AutoReplyService? _auto = null;

    private readonly Dictionary<long, string> _replyMap = new(); // id сообщения в Telegram -> id чата PlayerOK
    private readonly Queue<long> _replyOrder = new();

    public AppHost()
    {
        Settings = AppSettings.Load();
        Notify = NotifySettings.Load();
        Products = new ProductReplyStore();
        Quick = new QuickReplyStore();
        Review = ReviewSettings.Load();
        Afk = AfkSettings.Load();
        Stale = StaleDealSettings.Load();
    }

    private void Say(string message) => Log?.Invoke($"{DateTime.Now:HH:mm:ss}  {message}");

    public void ReloadSettings() => Settings = AppSettings.Load();

    // ---------- Запуск и остановка ----------

    public void Start()
    {
        if (IsRunning) return;
        if (!Settings.IsComplete)
        {
            Say("❌ Заполните токены на вкладке «Настройки» и сохраните их.");
            return;
        }

        var script = Path.Combine(AppContext.BaseDirectory, "bot.py");
        if (!File.Exists(script))
        {
            Say($"❌ Не найден bot.py рядом с EXE: {script}");
            return;
        }

        var info = new ProcessStartInfo
        {
            FileName = "python",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = JsonStore.DataDir,
        };
        info.ArgumentList.Add(script);
        SetBotEnvironment(info);

        try
        {
            _pythonBot = Process.Start(info);
        }
        catch (Exception first) when (first is System.ComponentModel.Win32Exception || first is FileNotFoundException)
        {
            // На Windows Python Launcher часто доступен как py, даже если python не добавлен в PATH.
            info.FileName = "py";
            info.ArgumentList.Insert(0, "-3");
            try { _pythonBot = Process.Start(info); }
            catch (Exception second)
            {
                Say($"❌ Не удалось запустить Python. Установите Python 3 и добавьте его в PATH. {second.Message}");
                return;
            }
        }

        if (_pythonBot is null)
        {
            Say("❌ Не удалось создать процесс Python.");
            return;
        }
        // Локальная копия: обработчик Exited может сработать после повторного запуска или Dispose, когда поле
        // _pythonBot уже другое (или null) — тогда ExitCode читался бы не у того процесса либо падал с NRE.
        var bot = _pythonBot;
        bot.EnableRaisingEvents = true;
        bot.OutputDataReceived += (_, e) => { if (e.Data is not null) Say("[BOT] " + e.Data); };
        bot.ErrorDataReceived += (_, e) => { if (e.Data is not null) Say("[BOT] " + e.Data); };
        bot.Exited += (_, _) =>
        {
            int code;
            try { code = bot.ExitCode; } catch { code = -1; }
            Say($"Python-бот завершил работу (код {code}).");
        };
        bot.BeginOutputReadLine();
        bot.BeginErrorReadLine();
        Say("▶ Запущен именно bot.py (Python).");
    }


    // ---------- Режим самостоятельного приложения и встроенные чаты ----------

    private bool _stoppedByUser;

    /// <summary>
    /// Подключает окно к PlayerOK и (если пользователь сам не нажимал «Остановить») запускает фоновый монитор:
    /// автообновление чатов, метки о новых сообщениях и автоответ по ключевым словам.
    /// </summary>
    public async Task ConnectAppOnlyAsync(CancellationToken ct, bool explicitStart = false)
    {
        ReloadSettings();
        if (string.IsNullOrWhiteSpace(Settings.PlayerokToken))
            throw new InvalidOperationException("Сначала укажите токен PlayerOK на вкладке «Настройки».");
        if (explicitStart) _stoppedByUser = false;
        var fresh = new PlayerokClient(Settings);
        try { await fresh.ConnectAsync(ct); }
        catch { fresh.Dispose(); throw; }
        var old = _uiClient;
        _uiClient = fresh;
        old?.Dispose();
        Say($"Подключено к PlayerOK как {fresh.Username}.");
        if (!_stoppedByUser) { StartMonitor(); StartStaleWatcher(); }
        AppConnected?.Invoke();
    }

    /// <summary>Отключает приложение от PlayerOK и останавливает монитор (кнопка «Остановить»).</summary>
    public void DisconnectApp()
    {
        _stoppedByUser = true;
        StopMonitor();
        StopStaleWatcher();
        var old = _uiClient;
        _uiClient = null;
        old?.Dispose();
    }

    // ---------- Фоновый монитор: автообновление чатов и автоответ ----------

    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private readonly SemaphoreSlim _autoGate = new(1, 1);
    private readonly Dictionary<string, string> _partnerByChat = new();
    private AutoReplyService? _appAuto;

    /// <summary>Свежая первая страница чатов (приходит каждые несколько секунд). Вызывается не из UI-потока.</summary>
    public event Action<IReadOnlyList<ChatSummary>>? ChatsUpdated;
    /// <summary>Новое событие PlayerOK (сообщение, покупка, подтверждение…). Вызывается не из UI-потока.</summary>
    public event Action<PlayerokEvent>? ChatEvent;
    /// <summary>Приложение (заново) подключилось к PlayerOK. Вызывается не из UI-потока.</summary>
    public event Action? AppConnected;

    /// <summary>Автоответ в приложении работает, только если Python-бот не запущен (иначе ответили бы дважды).</summary>
    public bool AppAutoReplyActive => Settings.AppAutoReply && !IsRunning;

    private void StartMonitor()
    {
        StopMonitor();
        var cts = new CancellationTokenSource();
        _monitorCts = cts;
        _monitorTask = Task.Run(() => MonitorAsync(cts.Token));
    }

    private void StopMonitor()
    {
        var cts = _monitorCts;
        _monitorCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        var task = _monitorTask;
        _monitorTask = null;
        if (task is null) { cts.Dispose(); return; }
        _ = task.ContinueWith(_ => cts.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        PlayerokClient? client = null;
        try
        {
            // Отдельный клиент: опрос не мешает загрузке чатов в окне и наоборот.
            client = new PlayerokClient(Settings);
            while (true)
            {
                try { await client.ConnectAsync(ct); break; }
                catch (UnauthorizedException) { Say("❌ Монитор чатов: токен PlayerOK недействителен."); return; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Say($"⚠️ Монитор чатов не подключился: {ex.Message}. Повтор через 15 с.");
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                }
            }

            var auto = new AutoReplyService(client, Products, Review, Afk, Notify,
                (text, chatId) => { Say(text.Replace("\n", " ")); return Task.CompletedTask; });
            _appAuto = auto;

            var caughtUp = false;
            client.ChatsPolled += chats =>
            {
                lock (_partnerByChat) foreach (var c in chats) _partnerByChat[c.Id] = c.PartnerName;
                try { ChatsUpdated?.Invoke(chats); } catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("AppHost.cs:221", logEx); }
                // Догон пропущенных покупок запускаем после первого опроса, когда состояние уже запомнено.
                if (!caughtUp)
                {
                    caughtUp = true;
                    _ = Task.Run(() => CatchUpMissedDealsAsync(client, auto, ct));
                }
            };
            client.PollError += message => Say("⚠️ " + message);

            Say("🔄 Чаты обновляются автоматически. " + (AppAutoReplyActive
                ? "Автоответ по ключевым словам включён."
                : IsRunning ? "Автоответ выполняет Python-бот." : "Автоответ в приложении выключен."));

            await foreach (var e in client.ListenAsync(ct))
            {
                try
                {
                    try { ChatEvent?.Invoke(e); } catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("AppHost.cs:239", logEx); }
                    if (e is NewDealEvent nd)
                    {
                        Say($"🛒 Новая покупка: {nd.Deal.ItemName ?? "без названия"} · {nd.Deal.Price:0.##} ₽ · {nd.Deal.BuyerName}");
                        await RunAutoReplyAsync(auto, nd, ct);
                    }
                    else if (e is DealConfirmedEvent dc && AppAutoReplyActive)
                    {
                        await auto.OnDealConfirmedAsync(dc, ct);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Say($"⚠️ Ошибка обработки события: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { /* остановлено */ }
        catch (Exception ex) { Say($"⚠️ Монитор чатов остановился: {ex.Message}"); }
        finally
        {
            try { client?.Dispose(); } catch { }
        }
    }

    private async Task RunAutoReplyAsync(AutoReplyService auto, NewDealEvent e, CancellationToken ct)
    {
        if (!AppAutoReplyActive) return;
        var deal = e.Deal;
        // Если PlayerOK не отдал ник покупателя, берём имя собеседника из списка чатов — иначе в тексте остался бы {buyer}.
        if (string.IsNullOrWhiteSpace(deal.BuyerName) && (e.ChatId ?? deal.ChatId) is { } cid)
        {
            string? partner;
            lock (_partnerByChat) _partnerByChat.TryGetValue(cid, out partner);
            if (!string.IsNullOrWhiteSpace(partner)) deal = deal with { BuyerName = partner };
        }
        await _autoGate.WaitAsync(ct);
        try
        {
            if (AppAutoReplyActive) await auto.OnNewDealAsync(new NewDealEvent(e.ChatId, deal), ct);
        }
        finally { _autoGate.Release(); }
    }

    /// <summary>
    /// Покупки, оплаченные, пока приложение было закрыто: ищем среди последних продаж за сутки те, на которые
    /// ещё не отвечали, и отвечаем по ключевым словам. Если продавец уже писал в чат после оплаты — пропускаем.
    /// </summary>
    private async Task CatchUpMissedDealsAsync(PlayerokClient client, AutoReplyService auto, CancellationToken ct)
    {
        try
        {
            if (!AppAutoReplyActive) return;
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            // Захватываем более широкий хвост: 2 страницы (до ~48 записей) теряли покупки при всплесках продаж.
            var deals = await client.GetRecentSalesAsync(50, ct);
            var missed = deals
                .Where(d => (d.Status == DealStatus.Paid || d.Status == DealStatus.Pending)
                            && !string.IsNullOrEmpty(d.ChatId) && d.CreatedAt is { } t && t >= cutoff
                            && !auto.WasReplied(d.Id))
                .OrderBy(d => d.CreatedAt).ToList();
            foreach (var deal in missed)
            {
                if (ct.IsCancellationRequested || !AppAutoReplyActive) return;
                var answered = false;
                try
                {
                    var messages = await client.GetChatMessagesAsync(deal.ChatId!, 24, ct);
                    answered = messages.Any(m => m.AuthorId == client.UserId && !EventFormatter.IsSystemMarker(m.Text)
                                                 && m.CreatedAt is { } mt && deal.CreatedAt is { } dt && mt >= dt);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("AppHost.cs:308", logEx); /* не удалось проверить — отвечаем, лучше лишнее приветствие, чем молчание */ }

                if (answered) { auto.MarkReplied(deal.Id); continue; }
                Say($"🛒 Пропущенная покупка (пока приложение было закрыто): {deal.ItemName ?? "без названия"}");
                await RunAutoReplyAsync(auto, new NewDealEvent(deal.ChatId, deal), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Say($"⚠️ Не удалось проверить пропущенные покупки: {ex.Message}"); }
    }

    private readonly SemaphoreSlim _connectGate = new(1, 1);

    /// <summary>Клиент для окна; при первом обращении подключается сам (одновременные вызовы подключаются один раз).</summary>
    private async Task<PlayerokClient> EnsureUiClientAsync(CancellationToken ct)
    {
        var current = _uiClient;
        if (current is not null) return current;
        await _connectGate.WaitAsync(ct);
        try
        {
            if (_uiClient is null) await ConnectAppOnlyAsync(ct);
            return _uiClient ?? throw new InvalidOperationException("Нет подключения к PlayerOK.");
        }
        finally { _connectGate.Release(); }
    }

    public async Task<IReadOnlyList<ChatSummary>> GetChatsForUiAsync(CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        return await client.GetAllChatsAsync(ct);
    }

    public async Task<(IReadOnlyList<ChatMessage> Messages, string? BeforeCursor, bool HasOlder)> GetMessagesPageForUiAsync(
        string chatId, string? beforeCursor, CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        return await client.GetChatMessagesPageAsync(chatId, 24, beforeCursor, ct);
    }

    /// <summary>Отмечает чат прочитанным на сайте (в фоне, ошибки не важны).</summary>
    public async Task MarkChatReadForUiAsync(string chatId)
    {
        try
        {
            var client = await EnsureUiClientAsync(CancellationToken.None);
            await client.MarkChatReadAsync(chatId, CancellationToken.None);
        }
        catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("AppHost.cs:356", logEx); /* не критично: метка «прочитано» повторится при следующем открытии */ }
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesForUiAsync(string chatId, CancellationToken ct)
    {
        var page = await GetMessagesPageForUiAsync(chatId, null, ct);
        return page.Messages;
    }

    public async Task SendImageFromUiAsync(string chatId, byte[] image, string fileName, string? text, CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        await client.SendImageAsync(chatId, image, fileName, text, ct);
    }

    private static readonly Dictionary<string, byte[]> ImageCache = new();
    private static readonly SemaphoreSlim ImageGate = new(4, 4);
    private HttpClient? _imageHttp;

    /// <summary>Скачивает картинку по ссылке (с кэшем в памяти). Возвращает null при ошибке.</summary>
    public async Task<byte[]?> GetImageAsync(string url, CancellationToken ct)
    {
        lock (ImageCache) if (ImageCache.TryGetValue(url, out var cached)) return cached;
        await ImageGate.WaitAsync(ct);
        try
        {
            // До 4 загрузок идут параллельно: без замка каждая могла создать свой HttpClient, а все, кроме последнего, терялись.
            HttpClient http;
            lock (ImageCache)
            {
                if (_imageHttp is null)
                {
                    _imageHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    _imageHttp.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", Settings.UserAgent);
                    _imageHttp.DefaultRequestHeaders.TryAddWithoutValidation("referer", "https://playerok.com/");
                }
                http = _imageHttp;
            }
            var bytes = await http.GetByteArrayAsync(url, ct);
            lock (ImageCache) { if (ImageCache.Count > 300) ImageCache.Clear(); ImageCache[url] = bytes; }
            return bytes;
        }
        catch { return null; }
        finally { ImageGate.Release(); }
    }

    public async Task SendChatMessageFromUiAsync(string chatId, string message, CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        await client.SendMessageAsync(chatId, message, ct);
    }

    public async Task<Deal> UpdateDealStatusFromUiAsync(string dealId, DealStatus status, CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        return await client.UpdateDealStatusAsync(dealId, status, ct);
    }

    /// <summary>Точный статус одной сделки (нужен фильтрам чатов, когда список чатов не отдал сделку).</summary>
    public async Task<Deal?> GetDealForUiAsync(string dealId, CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        return await client.GetDealAsync(dealId, ct);
    }

    public async Task<IReadOnlyList<Deal>> GetSalesForUiAsync(CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        return await client.GetAllSalesAsync(ct);
    }

    /// <summary>Только первые страницы продаж (быстро): для регулярного обновления статусов без полной перезагрузки истории.</summary>
    public async Task<IReadOnlyList<Deal>> GetRecentSalesForUiAsync(int pages, CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        return await client.GetRecentSalesAsync(pages, ct);
    }

    // ---------- Напоминание о зависших сделках ----------

    /// <summary>Есть оплаченные сделки, по которым товар не выдан дольше заданного времени. Вызывается не из UI-потока.</summary>
    public event Action<IReadOnlyList<StaleDealInfo>>? StaleDeals;

    private CancellationTokenSource? _staleCts;
    private Task? _staleTask;
    private readonly Dictionary<string, DateTimeOffset> _staleReminded = new();
    private static readonly TimeSpan StaleMaxAge = TimeSpan.FromDays(3); // совсем старые «хвосты» не напоминаем

    private void StartStaleWatcher()
    {
        StopStaleWatcher();
        var cts = new CancellationTokenSource();
        _staleCts = cts;
        _staleTask = Task.Run(() => StaleLoopAsync(cts.Token));
    }

    private void StopStaleWatcher()
    {
        var cts = _staleCts;
        _staleCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        var task = _staleTask;
        _staleTask = null;
        if (task is null) { cts.Dispose(); return; }
        _ = task.ContinueWith(_ => cts.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task StaleLoopAsync(CancellationToken ct)
    {
        string? lastError = null;
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cfg = Stale;
                var client = _uiClient;
                if (cfg.Enabled && client is not null)
                {
                    var recent = await client.GetRecentSalesAsync(2, ct);
                    CheckStale(recent, cfg);
                }
                lastError = null;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (ex.Message != lastError) Say($"⚠️ Проверка невыданных заказов не удалась: {ex.Message}");
                lastError = ex.Message;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private void CheckStale(IReadOnlyList<Deal> deals, StaleDealSettings cfg)
    {
        var now = DateTimeOffset.UtcNow;
        var threshold = TimeSpan.FromMinutes(cfg.Minutes);
        var repeat = TimeSpan.FromMinutes(cfg.RepeatMinutes);
        var due = new List<StaleDealInfo>();
        foreach (var d in deals)
        {
            // Оплачена, но не выдана и без спора. Любой другой статус сбрасывает напоминание.
            if (d.Status != DealStatus.Paid || d.HasProblem || d.CreatedAt is null) { _staleReminded.Remove(d.Id); continue; }
            var age = now - d.CreatedAt.Value;
            if (age < threshold || age > StaleMaxAge) continue;
            if (_staleReminded.TryGetValue(d.Id, out var last) && (cfg.RepeatMinutes <= 0 || now - last < repeat)) continue;
            _staleReminded[d.Id] = now;
            due.Add(new StaleDealInfo(d, age));
        }
        if (due.Count == 0) return;
        foreach (var x in due)
            Say($"⏰ Не выдан заказ уже {FormatAge(x.Age)}: {x.Deal.ItemName ?? "без названия"} · {x.Deal.Price:0.##} ₽ · {x.Deal.BuyerName}");
        try { StaleDeals?.Invoke(due); } catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("AppHost.cs:504", logEx); }
    }

    public static string FormatAge(TimeSpan age) =>
        age.TotalHours >= 1 ? $"{(int)age.TotalHours} ч {age.Minutes:00} мин" : $"{Math.Max(1, (int)age.TotalMinutes)} мин";

    /// <summary>Данные профиля для вкладки «Профиль»: ник, аватар, дата регистрации.</summary>
    public async Task<(string? Username, string? AvatarUrl, DateTimeOffset? RegisteredAt)> GetProfileForUiAsync(CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        return (client.Username, client.AvatarUrl, client.RegisteredAt);
    }

    public async Task<Balance> GetBalanceForUiAsync(CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        return await client.GetBalanceAsync(ct);
    }

    public async Task<IReadOnlyList<ListingSummary>> GetListingsForUiAsync(CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        return await client.GetMyListingsAsync(ct);
    }

    /// <summary>Свежие места объявлений в выдаче (только те, для которых место удалось получить).</summary>
    public async Task<Dictionary<string, int?>> GetListingPositionsForUiAsync(IReadOnlyList<string> itemIds, CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        var result = new Dictionary<string, int?>();
        var gate = new SemaphoreSlim(2);
        // Резервно проверяем до 500 активных объявлений, у которых позиция
        // отсутствовала в профиле. Основное значение берём из priorityPosition профиля.
        // Небольшой параллелизм снижает риск ограничений со стороны PlayerOK.
        var tasks = itemIds.Take(500).Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var pos = await client.GetItemPositionAsync(id, ct);
                lock (result) result[id] = pos;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("AppHost.cs:547", logEx); /* одно объявление не получилось — остальные важнее */ }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);
        return result;
    }

    public async Task<IReadOnlyList<ItemPriorityStatus>> GetPriorityStatusesForUiAsync(
        string itemId, decimal itemPrice, CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        return await client.GetItemPriorityStatusesAsync(itemId, itemPrice, ct);
    }

    public async Task<IReadOnlyList<GameChoice>> SearchGamesForUiAsync(string query, CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        return await client.SearchGamesAsync(query, ct);
    }

    public async Task<IReadOnlyList<GameCategoryChoice>> GetGameCategoriesForUiAsync(string gameId, CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        return await client.GetGameCategoriesAsync(gameId, ct);
    }

    public async Task<GameCategoryChoice?> GetGameCategoryDetailsForUiAsync(string categoryId, CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        return await client.GetGameCategoryDetailsAsync(categoryId, ct);
    }

    public async Task<IReadOnlyList<GameObtainingChoice>> GetObtainingTypesForUiAsync(string categoryId, CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        return await client.GetObtainingTypesAsync(categoryId, ct);
    }

    public async Task<IReadOnlyList<GameDataField>> GetItemDataFieldsForUiAsync(
        string categoryId, string obtainingTypeId, CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        return await client.GetItemDataFieldsAsync(categoryId, obtainingTypeId, ct);
    }

    public async Task<ListingSummary> CreateItemDraftForUiAsync(
        string categoryId, string obtainingTypeId, string name, int price, string description,
        IReadOnlyDictionary<string, string> attributes,
        IReadOnlyList<(string FieldId, string Value)> dataFields,
        IReadOnlyList<CreateItemAttachment> attachments, CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        return await client.CreateItemAsync(categoryId, obtainingTypeId, name, price, description,
            attributes, dataFields, attachments, ct);
    }

    public async Task SetListingPriorityForUiAsync(
        string itemId, string priorityStatusId, bool publishDraft, CancellationToken ct)
    {
        var client = await EnsureUiClientAsync(ct);
        if (publishDraft)
            await client.PublishItemAsync(itemId, priorityStatusId, ct);
        else
            await client.IncreaseItemPriorityStatusAsync(itemId, priorityStatusId, ct);
    }

    public bool HasAppConnection => _uiClient is not null;
    public string? UiUserId => _uiClient?.UserId;
    public string? UiUsername => _uiClient?.Username;

    private void SetBotEnvironment(ProcessStartInfo info)
    {
        info.Environment["TG_BOT_TOKEN"] = Settings.TgBotToken;
        info.Environment["ADMIN_ID"] = Settings.AdminId.ToString(CultureInfo.InvariantCulture);
        info.Environment["PLAYEROK_TOKEN"] = Settings.PlayerokToken;
        info.Environment["PLAYEROK_DDG3"] = Settings.PlayerokDdg3;
        info.Environment["USER_AGENT"] = Settings.UserAgent;
        info.Environment["TG_PROXY"] = Settings.TgProxy;
        info.Environment["TZ_OFFSET_HOURS"] = Settings.TzOffsetHours.ToString(CultureInfo.InvariantCulture);
        info.Environment["BALANCE_POLL_SECONDS"] = Settings.BalancePollSeconds.ToString(CultureInfo.InvariantCulture);
        info.Environment["AUTOREPLIES_FILE"] = JsonStore.PathOf("auto_replies.json");
        info.Environment["PRODUCT_REPLIES_FILE"] = JsonStore.PathOf("product_replies.json");
        info.Environment["NOTIFY_FILE"] = JsonStore.PathOf("notify_settings.json");
        info.Environment["REVIEW_FILE"] = JsonStore.PathOf("review_request.json");
        info.Environment["AFK_FILE"] = JsonStore.PathOf(AfkSettings.FileName);
        info.Environment["FAVORITES_FILE"] = JsonStore.PathOf("favorites.json");
        info.Environment["SALES_PRESETS_FILE"] = JsonStore.PathOf("sales_presets.json");
    }

    public async Task StopAsync()
    {
        try
        {
            var process = _pythonBot;
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    Say("⚠️ Python-бот не завершился за 5 секунд после сигнала остановки.");
                }
            }
        }
        catch (Exception ex) { Say($"⚠️ Ошибка остановки Python-бота: {ex.Message}"); }
        Say("Остановлено.");
    }

    // ---------- Уведомления ----------

    private async Task NotifyAsync(string html, string? chatId, string? category, CancellationToken ct)
    {
        if (category is not null && !Notify.IsOn(category)) return;
        if (_tg is null) return;

        JsonNode? keyboard = null;
        if (chatId is not null)
        {
            var rows = Quick.Items.Select((t, i) => (t, $"q:{i}:{chatId}"));
            keyboard = TelegramApi.Keyboard(rows);
        }
        try
        {
            var messageId = await _tg.SendMessageAsync(Settings.AdminId, html, keyboard, ct);
            if (chatId is not null && messageId != 0)
            {
                _replyMap[messageId] = chatId;
                _replyOrder.Enqueue(messageId);
                while (_replyOrder.Count > 2000) _replyMap.Remove(_replyOrder.Dequeue());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Say($"⚠️ Не удалось отправить уведомление в Telegram: {ex.Message}");
        }
    }

    // ---------- PlayerOK -> Telegram ----------

    private async Task ListenLoop(CancellationToken ct)
    {
        await foreach (var e in _client!.ListenAsync(ct))
        {
            try
            {
                var formatted = Format(e, _client.UserId);
                if (formatted is not null)
                {
                    Say(formatted.Category + ": " + formatted.Html.Replace("\n", " ")[..Math.Min(90, formatted.Html.Length)]);
                    await NotifyAsync(formatted.Html, formatted.ChatId, formatted.Category, ct);
                }
                // Автоответ по товару не зависит от настроек уведомлений.
                if (e is NewDealEvent nd) await _auto!.OnNewDealAsync(nd, ct);
                if (e is DealConfirmedEvent dc) await _auto!.OnDealConfirmedAsync(dc, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Say($"⚠️ Ошибка обработки события: {ex.Message}"); }
        }
    }

    // ---------- Разморозка средств ----------

    private static string Money(decimal v)
    {
        var s = v.ToString("N2", CultureInfo.InvariantCulture).Replace(",", " ");
        return s.EndsWith(".00") ? s[..^3] : s;
    }

    internal static decimal DetectUnfreeze(Balance prev, Balance cur)
    {
        var released = Math.Max(prev.PendingIncome - cur.PendingIncome, prev.Frozen - cur.Frozen);
        var gained = cur.Available > prev.Available || cur.Withdrawable > prev.Withdrawable;
        return released > 0 && gained ? released : 0m;
    }

    private async Task BalanceLoop(CancellationToken ct)
    {
        Balance? prev = null;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, Settings.BalancePollSeconds)), ct);
            try
            {
                var cur = await _client!.GetBalanceAsync(ct);
                if (prev is not null)
                {
                    var released = DetectUnfreeze(prev, cur);
                    if (released > 0)
                        await NotifyAsync(
                            $"🔓 <b>Средства разморожены</b>: {Money(released)} ₽\n" +
                            $"Доступно: {Money(cur.Available)} ₽\nК выводу: {Money(cur.Withdrawable)} ₽\n" +
                            $"Ещё заморожено/ожидается: {Money(cur.Frozen)} / {Money(cur.PendingIncome)} ₽",
                            null, "unfreeze", ct);
                }
                prev = cur;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Say($"⚠️ Не удалось проверить баланс: {ex.Message}"); }
        }
    }

    // ---------- Telegram -> PlayerOK ----------

    private async Task TelegramLoop(CancellationToken ct)
    {
        long offset = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var update in await _tg!.GetUpdatesAsync(offset, ct))
                {
                    if (update is null) continue;
                    offset = update["update_id"]!.GetValue<long>() + 1;
                    try { await HandleUpdateAsync(update, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Say($"⚠️ Ошибка команды Telegram: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Say($"⚠️ Telegram: {ex.Message}. Повтор через 5 с.");
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task HandleUpdateAsync(JsonNode update, CancellationToken ct)
    {
        if (update["callback_query"] is JsonNode cq)
        {
            if (cq["from"]?["id"]?.GetValue<long>() != Settings.AdminId) return;
            await HandleCallbackAsync(cq, ct);
            return;
        }

        if (update["message"] is not JsonNode m) return;
        if (m["from"]?["id"]?.GetValue<long>() != Settings.AdminId) return;
        var text = m["text"]?.ToString();
        if (string.IsNullOrWhiteSpace(text)) return;

        if (text.StartsWith("/start"))
        {
            await _tg!.SendMessageAsync(Settings.AdminId,
                "🚀 <b>PlayerOK Bridge</b>\n\n" +
                "Ответьте (Reply) на уведомление покупателя — текст уйдёт ему в чат PlayerOK.\n\n" +
                "/balance — баланс\n/stats — статистика продаж\n/deals — последние сделки\n" +
                "/check <i>название лота</i> — какой автоответ подойдёт", null, ct);
        }
        else if (text.StartsWith("/balance")) await SendBalanceAsync(ct);
        else if (text.StartsWith("/stats")) await SendStatsAsync(ct);
        else if (text.StartsWith("/deals")) await SendDealsAsync(ct);
        else if (text.StartsWith("/check")) await SendCheckAsync(text, ct);
        else if (m["reply_to_message"]?["message_id"] is JsonNode replied)
        {
            if (!_replyMap.TryGetValue(replied.GetValue<long>(), out var chatId))
            {
                await _tg!.SendMessageAsync(Settings.AdminId,
                    "Не нашёл чат PlayerOK для этого сообщения. Ответьте на свежее уведомление.", null, ct);
                return;
            }
            await _client!.SendMessageAsync(chatId, text, ct);
            await _tg!.SendMessageAsync(Settings.AdminId, "✅ Отправлено на PlayerOK", null, ct);
        }
    }

    private async Task HandleCallbackAsync(JsonNode cq, CancellationToken ct)
    {
        var id = cq["id"]!.ToString();
        var parts = (cq["data"]?.ToString() ?? "").Split(':', 3);
        try
        {
            if (parts.Length == 3 && parts[0] == "q" && int.TryParse(parts[1], out var i) && i >= 0 && i < Quick.Items.Count)
            {
                var text = Quick.Items[i];
                await _client!.SendMessageAsync(parts[2], text, ct);
                await _tg!.AnswerCallbackAsync(id, "Отправлено ✅", false, ct);
                await _tg.SendMessageAsync(Settings.AdminId, $"✅ Отправлено покупателю:\n{Esc(text)}", null, ct);
            }
            else await _tg!.AnswerCallbackAsync(id, "Этот ответ уже недоступен", true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _tg!.AnswerCallbackAsync(id, "Ошибка отправки", true, ct);
            await _tg.SendMessageAsync(Settings.AdminId, $"Не отправилось: {Esc(ex.Message)}", null, ct);
        }
    }

    // ---------- Команды ----------

    private async Task SendBalanceAsync(CancellationToken ct)
    {
        var b = await _client!.GetBalanceAsync(ct);
        await _tg!.SendMessageAsync(Settings.AdminId,
            $"💰 Баланс: <b>{Money(b.Value)}</b> ₽\nДоступно: {Money(b.Available)} ₽\nЗаморожено: {Money(b.Frozen)} ₽\n" +
            $"К выводу: {Money(b.Withdrawable)} ₽\nОжидается: {Money(b.PendingIncome)} ₽", null, ct);
    }

    private async Task SendCheckAsync(string text, CancellationToken ct)
    {
        var name = text.Length > 6 ? text[6..].Trim() : "";
        if (name.Length == 0)
        {
            await _tg!.SendMessageAsync(Settings.AdminId,
                "Использование: <code>/check название товара</code>\nНапример: <code>/check Brawl Stars 30 гемов</code>", null, ct);
            return;
        }
        var matches = ProductMatcher.Match(Products.Items, name);
        if (matches.Count == 0)
        {
            await _tg!.SendMessageAsync(Settings.AdminId,
                $"🔎 Товар: <b>{Esc(name)}</b>\n❌ Ни одно ключевое слово не подошло — автоответ не отправится.", null, ct);
            return;
        }
        var best = matches[0];
        var entry = Products.Items[best.Index];
        var preview = ReplyRenderer.Render(entry.Text, new DealInfo("TEST", "Тест", null, name, 100, null), _client!.Username);
        var lines = new List<string>
        {
            $"🔎 Товар: <b>{Esc(name)}</b>",
            $"✅ Сработает: <b>{Esc(entry.Title)}</b> (запись {best.Index + 1})",
            $"🔑 Совпало ключевое слово: <code>{Esc(best.Alias)}</code>",
        };
        if (matches.Count > 1)
            lines.Add("Тоже подошли, но менее точные: " + string.Join(", ",
                matches.Skip(1).Select(x => $"{Esc(Products.Items[x.Index].Title)} (<code>{Esc(x.Alias)}</code>)")));
        lines.Add("\n📨 Так увидит покупатель (ник «Тест»):\n" + Esc(preview));
        await _tg!.SendMessageAsync(Settings.AdminId, string.Join("\n", lines), null, ct);
    }

    private static string StatusLabel(Deal d)
    {
        var label = d.Status switch
        {
            DealStatus.Paid => "💳 Оплачена",
            DealStatus.Pending => "⏳ Ожидает выдачи",
            DealStatus.Sent => "📦 Товар отправлен",
            DealStatus.Confirmed => "✅ Подтверждена",
            DealStatus.ConfirmedAutomatically => "✅ Подтверждена автоматически",
            DealStatus.RolledBack => "↩️ Возврат средств",
            _ => "—",
        };
        return d.HasProblem && d.Status != DealStatus.RolledBack ? label + " · ⚠️ проблема" : label;
    }

    private async Task SendDealsAsync(CancellationToken ct)
    {
        var deals = await _client!.GetAllSalesAsync(ct);
        var lines = new List<string> { $"📋 <b>Последние сделки</b> · всего {deals.Count}", "" };
        var n = 1;
        foreach (var d in deals.Take(15))
            lines.Add($"{n++}. <b>{Esc((d.ItemName ?? "Без названия").Length > 70 ? d.ItemName![..67] + "…" : d.ItemName ?? "Без названия")}</b>\n      {Esc(StatusLabel(d))}");
        await _tg!.SendMessageAsync(Settings.AdminId, string.Join("\n", lines), null, ct);
    }

    private async Task SendStatsAsync(CancellationToken ct)
    {
        var deals = await _client!.GetAllSalesAsync(ct);
        var tz = TimeSpan.FromHours(Settings.TzOffsetHours);
        var now = DateTimeOffset.UtcNow.ToOffset(tz);
        var periods = new (string Title, DateTimeOffset? Since)[]
        {
            ("Сегодня", new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, tz)),
            ("За 7 дней", now.AddDays(-7)),
            ("За 30 дней", now.AddDays(-30)),
            ("За всё время", null),
        };

        var blocks = new List<string>
        {
            "📊 <b>Статистика продаж</b>\n" +
            $"<i>По дате создания сделки, время UTC{Settings.TzOffsetHours:+0.##;-0.##}. Суммы — по цене лотов, без комиссии.</i>",
        };
        foreach (var (title, since) in periods)
        {
            var sel = deals.Where(d => since is null || (d.CreatedAt is { } c && c.ToOffset(tz) >= since)).ToList();
            (int n, decimal sum) Bucket(Func<Deal, bool> f) { var x = sel.Where(f).ToList(); return (x.Count, x.Sum(d => d.Price)); }
            var done = Bucket(d => d.Status is DealStatus.Confirmed or DealStatus.ConfirmedAutomatically);
            var work = Bucket(d => d.Status is DealStatus.Paid or DealStatus.Pending or DealStatus.Sent);
            var refund = Bucket(d => d.Status == DealStatus.RolledBack);
            var problems = sel.Count(d => d.HasProblem && d.Status != DealStatus.RolledBack);

            var lines = new List<string>
            {
                $"<b>{title}</b>",
                $"Сделок: {sel.Count} на {Money(sel.Sum(d => d.Price))} ₽",
                $"✅ Завершено: {done.n} ({Money(done.sum)} ₽)",
                $"⏳ В работе: {work.n} ({Money(work.sum)} ₽)",
                $"↩️ Возвраты: {refund.n} ({Money(refund.sum)} ₽)",
            };
            if (problems > 0) lines.Add($"⚠️ С проблемой: {problems}");
            blocks.Add(string.Join("\n", lines));
        }
        await _tg!.SendMessageAsync(Settings.AdminId, string.Join("\n\n", blocks), null, ct);
    }

    public void Dispose()
    {
        try { if (_pythonBot is { HasExited: false }) _pythonBot.Kill(entireProcessTree: true); } catch { }
        _pythonBot?.Dispose();
        StopMonitor();
        StopStaleWatcher();
        _uiClient?.Dispose();
        _imageHttp?.Dispose();
    }
}
