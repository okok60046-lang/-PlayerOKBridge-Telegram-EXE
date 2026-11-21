using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace PlayerokBridge.Core;

/// <summary>
/// Локализация интерфейса (ru / en).
/// Исходные тексты интерфейса написаны по-русски, поэтому английский вариант подставляется «на лету»:
/// <see cref="Apply"/> обходит дерево элементов и заменяет тексты по словарю <see cref="En"/>.
/// Оригинал каждого текста запоминается, поэтому переключение обратно на русский возвращает исходные строки.
/// Новую строку достаточно добавить в словарь ниже — править места создания элементов не нужно.
/// </summary>
public static class Loc
{
    public const string Ru = "ru";
    public const string EnCode = "en";

    public static string Language { get; private set; } = Ru;
    public static bool IsEn => Language == EnCode;

    /// <summary>Вызывается после смены языка (уже на UI-потоке).</summary>
    public static event Action? Changed;

    public static string Normalize(string? lang) =>
        string.Equals(lang, EnCode, StringComparison.OrdinalIgnoreCase) ? EnCode : Ru;

    public static void Init(string? lang) => Language = Normalize(lang);

    public static void Set(string? lang)
    {
        var n = Normalize(lang);
        if (n == Language) return;
        Language = n;
        ApplyAll();
        Changed?.Invoke();
    }

    /// <summary>Для текстов, которые собираются в коде в момент показа.</summary>
    public static string T(string ru, string en) => IsEn ? en : ru;

    // ---------- Перевод строки ----------

    public static string Tr(string ru)
    {
        if (!IsEn || string.IsNullOrEmpty(ru)) return ru;
        if (En.TryGetValue(ru, out var exact)) return exact;

        // «🏠   Обзор»: эмодзи и пробелы в начале сохраняем, переводим остаток.
        var i = 0;
        while (i < ru.Length && !char.IsLetterOrDigit(ru[i])) i++;
        if (i > 0 && i < ru.Length && En.TryGetValue(ru[i..], out var rest))
            return ru[..i] + rest;

        // «Не удалось загрузить чаты: текст ошибки» — переводим только начало.
        foreach (var (key, value) in Prefixes)
            if (ru.StartsWith(key, StringComparison.Ordinal))
                return value + ru[key.Length..];
        return ru;
    }

    // ---------- Обход дерева ----------

    private sealed class Entry { public string Orig = ""; public string Applied = ""; }
    private static readonly ConditionalWeakTable<object, Entry> TextTable = new();
    private static readonly ConditionalWeakTable<object, Entry> ContentTable = new();
    private static readonly ConditionalWeakTable<object, Entry> HeaderTable = new();
    private static readonly ConditionalWeakTable<object, Entry> HintTable = new();

    private static void Fix(ConditionalWeakTable<object, Entry> table, object owner, string? current, Action<string> set)
    {
        if (string.IsNullOrEmpty(current)) return;
        if (!table.TryGetValue(owner, out var e))
        {
            e = new Entry { Orig = current, Applied = current };
            table.Add(owner, e);
        }
        else if (current != e.Applied)
        {
            e.Orig = current; // код приложения сам поменял текст — это новый оригинал
        }
        var result = Tr(e.Orig);
        e.Applied = result;
        if (result != current) set(result);
    }

    public static void Apply(ILogical? node)
    {
        if (node is null) return;
        try
        {
            switch (node)
            {
                case TextBlock tb when tb.Inlines is not { Count: > 0 }:
                    Fix(TextTable, tb, tb.Text, s => tb.Text = s);
                    break;
                case TextBox box:
                    Fix(HintTable, box, box.Watermark, s => box.Watermark = s);
                    break;
                case ContentControl cc when cc.Content is string str:
                    Fix(ContentTable, cc, str, s => cc.Content = s);
                    break;
            }
            if (node is HeaderedContentControl hc && hc.Header is string header)
                Fix(HeaderTable, hc, header, s => hc.Header = s);

            foreach (var child in node.LogicalChildren) Apply(child);
        }
        catch { /* перевод не должен ронять интерфейс */ }
    }

    /// <summary>Применяет язык ко всем открытым окнам приложения.</summary>
    public static void ApplyAll()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var w in desktop.Windows.ToList()) Apply(w);
    }

    private static DispatcherTimer? _watch;

    /// <summary>
    /// Тексты, которые код выставляет позже (статусы, баланс, ошибки), переводятся этим таймером,
    /// пока выбран английский.
    /// </summary>
    public static void StartWatcher()
    {
        if (_watch is not null) return;
        _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _watch.Tick += (_, _) => { if (IsEn) ApplyAll(); };
        _watch.Start();
    }

    // ---------- Словарь ----------

    /// <summary>Фразы, к которым в конце дописывается текст ошибки или значение.</summary>
    private static readonly (string Key, string Value)[] Prefixes =
    {
        ("Не удалось построить аналитику: ", "Failed to build analytics: "),
        ("Не удалось скопировать: ", "Failed to copy: "),
        ("Не удалось скопировать ссылку: ", "Failed to copy link: "),
        ("Не удалось открыть PlayerOK: ", "Failed to open PlayerOK: "),
        ("Не удалось рассчитать статистику: ", "Failed to calculate statistics: "),
        ("Не удалось загрузить товары: ", "Failed to load products: "),
        ("Не удалось пересчитать статистику: ", "Failed to recalculate statistics: "),
        ("Не удалось загрузить чаты: ", "Failed to load chats: "),
        ("Не удалось загрузить старые сообщения: ", "Failed to load older messages: "),
        ("Не удалось открыть диалог: ", "Failed to open the dialog: "),
        ("Не удалось показать диалог: ", "Failed to show the dialog: "),
        ("Не удалось обновить чат: ", "Failed to refresh the chat: "),
        ("Не удалось прикрепить фото: ", "Failed to attach the photo: "),
        ("Не удалось сохранить: ", "Failed to save: "),
        ("Не удалось сохранить настройку: ", "Failed to save the setting: "),
        ("Не удалось загрузить звук: ", "Failed to load the sound: "),
        ("Не удалось загрузить музыку: ", "Failed to load the music: "),
        ("Не удалось обновить статистику: ", "Failed to refresh statistics: "),
        ("Не удалось найти игру: ", "Failed to find the game: "),
        ("Не удалось загрузить категории: ", "Failed to load categories: "),
        ("Не удалось загрузить схему: ", "Failed to load the schema: "),
        ("Не удалось прочитать фотографии: ", "Failed to read the photos: "),
        ("Не удалось загрузить поля способа передачи: ", "Failed to load delivery method fields: "),
        ("⚠️ Не удалось обновить баланс: ", "⚠️ Failed to refresh the balance: "),
        ("⚠️ Не удалось загрузить чаты: ", "⚠️ Failed to load chats: "),
        ("⚠️ Не удалось пересчитать статистику: ", "⚠️ Failed to recalculate statistics: "),
        ("⚠️ Окно чатов не подключилось: ", "⚠️ The chat window failed to connect: "),
        ("⚠️ Автоподключение не удалось: ", "⚠️ Auto-connect failed: "),
        ("⚠️ Не удалось загрузить продажи для фильтров чатов: ", "⚠️ Failed to load sales for chat filters: "),
        ("❌ Не удалось загрузить товары: ", "❌ Failed to load products: "),
        ("❌ Не удалось сохранить: ", "❌ Failed to save: "),
        ("❌ Не удалось создать черновик: ", "❌ Failed to create the draft: "),
        ("❌ Ошибка операции: ", "❌ Operation error: "),
        ("Ошибка обновления: ", "Refresh error: "),
        ("Ошибка отправки: ", "Send error: "),
        ("Ошибка: ", "Error: "),
        ("Обновлено в ", "Updated at "),
        ("На PlayerOK с ", "On PlayerOK since "),
        ("Текущий стиль: ", "Current style: "),
        ("Свой звук: ", "Custom sound: "),
        ("Ваша музыка: ", "Your music: "),
        ("Копия создана — ", "Copy created — "),
        ("✅ Сохранено в ", "✅ Saved to "),
        ("🛒 Куплен товар: ", "🛒 Item purchased: "),
        ("Скопировано: ", "Copied: "),
        ("Черновик создан, но публикация не удалась: ", "Draft created, but publishing failed: "),
        ("⚠ Проблема · ", "⚠ Problem · "),
    };

    public static readonly Dictionary<string, string> En = new()
    {
        // --- Навигация и общие элементы ---
        ["🏠   Обзор"] = "🏠   Overview",
        ["💬   Чаты"] = "💬   Chats",
        ["🤖   Автоответы"] = "🤖   Auto-replies",
        ["👤   Профиль"] = "👤   Profile",
        ["🔔   Уведомления"] = "🔔   Notifications",
        ["🛍️   Мои товары"] = "🛍️   My products",
        ["🛠️   Настройки"] = "🛠️   Settings",
        ["🎨   GUI"] = "🎨   GUI",
        ["Чаты"] = "Chats",
        ["Профиль"] = "Profile",
        ["БАЛАНС PLAYEROK"] = "PLAYEROK BALANCE",
        ["Панель продавца"] = "Seller dashboard",
        ["Открыть PlayerOK Bridge"] = "Open PlayerOK Bridge",
        ["Журнал событий"] = "Event log",
        ["Выход"] = "Exit",
        ["Журнал — PlayerOK Bridge"] = "Log — PlayerOK Bridge",
        ["Ожидание подключения"] = "Waiting for connection",
        ["Добавьте токен в настройках"] = "Add a token in Settings",
        ["Нет подключения · нажмите ↻"] = "Not connected · press ↻",
        ["Обновление…"] = "Refreshing…",

        // --- Обзор ---
        ["▶ Запустить"] = "▶ Start",
        ["■ Остановить"] = "■ Stop",
        ["⚪ Не запущено"] = "⚪ Not running",
        ["🟢 Приложение подключено к PlayerOK"] = "🟢 App connected to PlayerOK",
        ["🟢 Приложение и Telegram-бот работают"] = "🟢 App and Telegram bot are running",
        ["Только приложение"] = "App only",
        ["Приложение + Telegram-бот"] = "App + Telegram bot",
        ["Режим запуска"] = "Launch mode",
        ["↗ В отдельном окне"] = "↗ In a separate window",
        ["Управляйте товарами, переписками и автоматизацией в одном окне."] = "Manage products, conversations and automation in one window.",

        // --- Журнал ---
        ["Подключение"] = "Connection",
        ["Опрос чатов"] = "Chat polling",
        ["Покупки"] = "Purchases",
        ["Python-бот"] = "Python bot",
        ["Все темы"] = "All topics",
        ["Все сообщения"] = "All messages",
        ["Предупреждения и ошибки"] = "Warnings and errors",
        ["Только ошибки"] = "Errors only",
        ["Поиск по тексту…"] = "Search text…",
        ["Следить за новыми"] = "Follow new entries",
        ["🗑 Очистить"] = "🗑 Clear",
        ["📋 Копировать"] = "📋 Copy",

        // --- Чаты ---
        ["Все"] = "All",
        ["Загружаю сообщения…"] = "Loading messages…",
        ["Напишите сообщение..."] = "Write a message...",
        ["Прикрепить фото (повторное нажатие — убрать)"] = "Attach a photo (press again to remove)",
        ["Выберите диалог"] = "Select a dialog",
        ["Чаты обновляются автоматически."] = "Chats refresh automatically.",
        ["↻ Обновить чаты"] = "↻ Refresh chats",
        ["Поиск по имени или сообщению…"] = "Search by name or message…",
        ["Только непрочитанные"] = "Unread only",
        ["Оплачено"] = "Paid",
        ["Товар выдан"] = "Delivered",
        ["Возврат"] = "Refund",
        ["Спор / проблема"] = "Dispute / problem",
        ["🎯 Ответы по товарам ▾"] = "🎯 Product replies ▾",
        ["✅ Подтвердить выдачу"] = "✅ Confirm delivery",
        ["↩ Сделать возврат"] = "↩ Make a refund",
        ["Подтвердить выдачу"] = "Confirm delivery",
        ["Сделать возврат"] = "Make a refund",
        ["✅ Выдача подтверждена."] = "✅ Delivery confirmed.",
        ["↩ Возврат оформлен."] = "↩ Refund processed.",
        ["Отправляю запрос в PlayerOK…"] = "Sending request to PlayerOK…",
        ["покупатель"] = "buyer",
        ["Покупатель"] = "Buyer",
        ["Продавец"] = "Seller",
        ["продавец"] = "seller",
        ["📷 Вложение"] = "📷 Attachment",
        ["Новая покупка!"] = "New purchase!",
        ["В этом диалоге пока нет сообщений."] = "No messages in this dialog yet.",
        ["Загрузка последних 24 сообщений…"] = "Loading the last 24 messages…",
        ["без названия"] = "untitled",
        ["Без названия"] = "Untitled",
        ["Новая сделка PlayerOK"] = "New PlayerOK deal",
        ["Покупка"] = "Purchase",
        ["Новый чат"] = "New chat",
        ["Отправлено фото"] = "Photo sent",
        ["Новое сообщение"] = "New message",
        ["Фото убрано."] = "Photo removed.",
        ["Выберите фото"] = "Choose a photo",
        ["📷 Фото отправляется…"] = "📷 Sending photo…",
        ["Отправка…"] = "Sending…",
        ["Сообщение отправлено."] = "Message sent.",
        ["Фото отправлено."] = "Photo sent.",
        ["Вы"] = "You",
        ["Поддержка"] = "Support",
        ["[Вложение]"] = "[Attachment]",
        ["🖼 Загрузка…"] = "🖼 Loading…",
        ["🖼 Фото (нажмите, чтобы открыть)"] = "🖼 Photo (click to open)",
        ["Заказ"] = "Order",
        ["⏰ Заказ не выдан"] = "⏰ Order not delivered",
        ["⚠️ Сообщение не удалось отобразить"] = "⚠️ Message could not be displayed",
        ["Вы получите оплату после того, как передадите товар"] = "You will be paid after you deliver the item",
        ["Оплата зачислена на ваш баланс и доступна к выплате"] = "Payment has been credited to your balance and is available for withdrawal",

        // --- Автоответы ---
        ["➕  Новый ответ"] = "➕  New reply",
        ["🔍  Поиск по ответам"] = "🔍  Search replies",
        ["⧉ Дублировать"] = "⧉ Duplicate",
        ["🗑 Удалить"] = "🗑 Delete",
        ["💾 Сохранить изменения"] = "💾 Save changes",
        ["Новый ответ"] = "New reply",
        ["💾 Добавить ответ"] = "💾 Add reply",
        ["Впишите слова, по которым ответ подбирается к названию лота."] = "Enter the words used to match this reply to a listing title.",
        ["Предпросмотр появится, когда вы введёте текст."] = "A preview will appear once you enter text.",
        ["⚠ Укажите ключевые слова — по ним ответ подбирается к товару."] = "⚠ Enter keywords — they are used to match the reply to a product.",
        ["⚠ Введите текст ответа."] = "⚠ Enter the reply text.",
        ["✅ Ответ добавлен."] = "✅ Reply added.",
        ["✅ Изменения сохранены."] = "✅ Changes saved.",
        ["Удалить ответ?"] = "Delete the reply?",
        ["📝 Вставить шаблон текста…"] = "📝 Insert text template…",
        ["📦 Из моих товаров"] = "📦 From my products",
        ["🔍  Найти товар"] = "🔍  Find product",
        ["Объявления ещё не загружены — проверьте подключение к PlayerOK."] = "Listings are not loaded yet — check your PlayerOK connection.",
        ["Загружаю объявления…"] = "Loading listings…",
        ["📥 Заготовки для товаров без ответа"] = "📥 Drafts for products without a reply",
        ["✅ У всех ваших товаров уже есть автоответ."] = "✅ All your products already have an auto-reply.",
        ["Создать заготовки?"] = "Create drafts?",
        ["🔎 Проверить подбор"] = "🔎 Test matching",
        ["❌ Ни одно ключевое слово не подошло — автоответ не отправится."] = "❌ No keyword matched — no auto-reply will be sent.",
        ["Тест"] = "Test",
        ["Так увидит покупатель (на примере)"] = "What the buyer will see (example)",
        ["ОТВЕТ ПО ТОВАРУ"] = "PRODUCT REPLY",
        ["ПРОВЕРКА ПОДБОРА"] = "MATCH TEST",
        ["Какой ответ получит лот с таким названием"] = "Which reply a listing with this title will get",
        ["Нужны данные от покупателя"] = "Need details from the buyer",
        ["Ссылка на инструкцию"] = "Link to instructions",
        ["Скоро выдам заказ"] = "Will deliver soon",
        ["Короткое «спасибо»"] = "Short “thank you”",
        ["Ник покупателя"] = "Buyer nickname",
        ["Ваш ник"] = "Your nickname",
        ["Название купленного товара"] = "Purchased item name",
        ["Цена сделки"] = "Deal price",
        ["Номер сделки"] = "Deal number",
        ["🌙 Включить AFK-режим (второе сообщение после ответа по товару)"] = "🌙 Enable AFK mode (second message after the product reply)",
        ["Отправлять AFK, даже если ответ по товару не подобран"] = "Send AFK even if no product reply matched",
        ["☀️ AFK выключен."] = "☀️ AFK is off.",
        ["💾 Сохранить AFK"] = "💾 Save AFK",
        ["❌ Текст AFK-сообщения не должен быть пустым."] = "❌ The AFK message text must not be empty.",
        ["👁 Предпросмотр"] = "👁 Preview",
        ["↺ Текст по умолчанию"] = "↺ Default text",
        ["AFK-режим"] = "AFK mode",
        ["⭐ Просить отзыв после подтверждения сделки"] = "⭐ Ask for a review after the deal is confirmed",
        ["💾 Сохранить просьбу об отзыве"] = "💾 Save review request",
        ["❌ Текст просьбы не должен быть пустым."] = "❌ The request text must not be empty.",
        ["✅ Настройки просьбы об отзыве сохранены."] = "✅ Review request settings saved.",
        ["Просьба об отзыве"] = "Review request",

        // --- Профиль и аналитика ---
        ["📊   Аналитика"] = "📊   Analytics",
        ["Статистика"] = "Statistics",
        ["Сделки"] = "Deals",
        ["Всего сделок"] = "Total deals",
        ["С возвратом"] = "With refund",
        ["Успешных"] = "Successful",
        ["↻ Обновить данные"] = "↻ Refresh data",
        ["Расширенная аналитика"] = "Advanced analytics",
        ["За всё время · данные PlayerOK"] = "All time · PlayerOK data",
        ["Данных пока нет"] = "No data yet",
        ["Нужно подключение к PlayerOK (токен в «Настройках»)."] = "A PlayerOK connection is required (token in Settings).",
        ["Доход"] = "Revenue",
        ["Средний чек"] = "Average order",
        ["по успешным сделкам"] = "from successful deals",
        ["Конверсия"] = "Conversion",
        ["Возвраты"] = "Refunds",
        ["Динамика продаж по дням"] = "Daily sales trend",
        ["Успешных продаж пока нет."] = "No successful sales yet.",
        ["Просмотры появятся после загрузки объявлений."] = "Views will appear after listings are loaded.",
        ["Шкала увеличена в 10 раз — конверсия обычно единицы процентов."] = "The scale is magnified 10× — conversion is usually a few percent.",
        ["Самые продаваемые товары"] = "Best-selling products",
        ["по числу успешных продаж"] = "by number of successful sales",
        ["Конверсия просмотров в покупки"] = "View-to-purchase conversion",
        ["все объявления"] = "all listings",
        ["Объявление"] = "Listing",
        ["Просмотры"] = "Views",
        ["Нет объявлений с просмотрами, покупками или доходом."] = "No listings with views, purchases or revenue.",
        ["Доход по каждому объявлению"] = "Revenue per listing",
        ["учитываются только успешные сделки"] = "only successful deals are counted",
        ["Нет сделок с датами — график появится после первой продажи."] = "No dated deals — the chart will appear after the first sale.",
        ["Масштаб графика"] = "Chart scale",
        ["Шаг: день"] = "Step: day",
        ["Шаг: неделя"] = "Step: week",
        ["Шаг: месяц"] = "Step: month",
        ["Все сделки"] = "All deals",
        ["Успешные"] = "Successful",
        ["Неуспешные (возврат)"] = "Unsuccessful (refund)",
        ["В процессе"] = "In progress",
        ["Успешно"] = "Successful",
        ["Успешно (авто)"] = "Successful (auto)",
        ["Выдано, ждёт подтверждения"] = "Delivered, awaiting confirmation",
        ["Ожидает оплаты"] = "Awaiting payment",
        ["Сделок пока нет или данные ещё не загружены."] = "No deals yet or data is not loaded.",
        ["В этом разделе сделок нет."] = "No deals in this section.",
        ["🎵 Музыка на фоне"] = "🎵 Background music",
        ["САУНДТРЕК НА ФОН"] = "BACKGROUND SOUNDTRACK",
        ["Громкость и остальное — во вкладке «🎨 GUI»."] = "Volume and other options are in the “🎨 GUI” tab.",

        // --- Мои товары ---
        ["↻ Обновить"] = "↻ Refresh",
        ["＋ Создать товар"] = "＋ Create product",
        ["Открывает встроенный мастер создания объявления PlayerOK"] = "Opens the built-in PlayerOK listing wizard",
        ["Откроет встроенный мастер создания объявления PlayerOK"] = "Opens the built-in PlayerOK listing wizard",
        ["Покупки за период"] = "Purchases for the period",
        ["Оборот за период"] = "Turnover for the period",
        ["Просмотры за период"] = "Views for the period",
        ["Активные товары"] = "Active products",
        ["Активные"] = "Active",
        ["На проверке"] = "Under review",
        ["Черновики"] = "Drafts",
        ["Завершённые"] = "Completed",
        ["Карточки"] = "Cards",
        ["Список"] = "List",
        ["Мои товары"] = "My products",
        ["Продажи, просмотры, публикация и продвижение объявлений"] = "Sales, views, publishing and promotion of listings",
        ["За день"] = "Day",
        ["За неделю"] = "Week",
        ["За месяц"] = "Month",
        ["За всё время"] = "All time",
        ["🔍  Поиск по названию товара или игре"] = "🔍  Search by product title or game",
        ["По игре"] = "By game",
        ["Сначала дорогие"] = "Most expensive first",
        ["Сначала дешёвые"] = "Cheapest first",
        ["Больше просмотров"] = "Most views",
        ["Больше покупок"] = "Most purchases",
        ["Выше конверсия"] = "Highest conversion",
        ["Больше выручки"] = "Highest revenue",
        ["Сначала новые"] = "Newest first",
        ["Просмотры накопительные, по данным PlayerOK."] = "Views are cumulative, per PlayerOK data.",
        ["Опубликовать"] = "Publish",
        ["🚀 Поднять"] = "🚀 Boost",
        ["Открыть объявление на PlayerOK"] = "Open the listing on PlayerOK",
        ["Скопировать ссылку"] = "Copy link",
        ["✅ Ссылка на объявление скопирована."] = "✅ Listing link copied.",
        ["Буфер обмена недоступен."] = "Clipboard is unavailable.",
        ["Статус"] = "Status",
        ["Цена"] = "Price",
        ["Просм."] = "Views",
        ["Конв."] = "Conv.",
        ["Все игры"] = "All games",
        ["Объявлений пока нет"] = "No listings yet",
        ["Ничего не найдено"] = "Nothing found",
        ["Нажмите «Обновить», чтобы загрузить объявления, или «Создать товар»."] = "Press “Refresh” to load listings, or “Create product”.",
        ["Измените поиск, игру или вкладку статуса."] = "Change the search, game or status tab.",
        ["Загружаю объявления и историю продаж PlayerOK…"] = "Loading PlayerOK listings and sales history…",
        ["Активно"] = "Active",
        ["Черновик"] = "Draft",
        ["Истекло"] = "Expired",
        ["Отклонено"] = "Rejected",
        ["Заблокировано"] = "Blocked",
        ["Продано"] = "Sold",
        ["Получаю доступные варианты продвижения…"] = "Getting available promotion options…",
        ["PlayerOK не вернул доступные варианты для этого объявления."] = "PlayerOK returned no available options for this listing.",
        ["Выполняю действие…"] = "Performing the action…",
        ["✅ Объявление отправлено на публикацию."] = "✅ Listing submitted for publishing.",
        ["✅ Приоритет объявления обновлён."] = "✅ Listing priority updated.",
        ["Публикую объявление…"] = "Publishing the listing…",
        ["✅ Объявление опубликовано."] = "✅ Listing published.",
        ["Черновик сохранён. Публикацию можно выполнить позже из списка товаров."] = "Draft saved. You can publish it later from the product list.",
        ["Публикация объявления"] = "Publishing a listing",
        ["Продвижение объявления"] = "Listing promotion",
        ["Оплатить и поднять"] = "Pay and boost",
        ["Выберите тариф публикации."] = "Choose a publishing plan.",
        ["Выберите тариф поднятия объявления."] = "Choose a boost plan.",
        ["Выберите вариант."] = "Choose an option.",
        ["Стоимость: бесплатно."] = "Cost: free.",
        ["Да, подтвердить"] = "Yes, confirm",
        ["Отмена"] = "Cancel",

        // --- Создание товара ---
        ["Создание товара"] = "Create product",
        ["Новое объявление PlayerOK"] = "New PlayerOK listing",
        ["1. Игра или приложение"] = "1. Game or app",
        ["2. Категория и способ передачи"] = "2. Category and delivery method",
        ["3. Характеристики"] = "3. Attributes",
        ["4. Описание объявления"] = "4. Listing description",
        ["Например: PUBG Mobile, Brawl Stars, Fortnite"] = "For example: PUBG Mobile, Brawl Stars, Fortnite",
        ["Название объявления"] = "Listing title",
        ["Описание товара"] = "Product description",
        ["Цена в рублях, например 250"] = "Price in rubles, e.g. 250",
        ["Фотографии не добавлены (необязательно)."] = "No photos added (optional).",
        ["Найти игру"] = "Find game",
        ["Загрузить категории"] = "Load categories",
        ["Загрузить характеристики и поля"] = "Load attributes and fields",
        ["🖼 Добавить фотографии"] = "🖼 Add photos",
        ["Создать черновик"] = "Create draft",
        ["Загружаю поля выбранного способа передачи…"] = "Loading fields for the selected delivery method…",
        ["Обязательное поле"] = "Required field",
        ["Можно оставить пустым"] = "May be left empty",
        ["Введите хотя бы два символа названия игры или приложения."] = "Enter at least two characters of the game or app name.",
        ["Ищу игру в каталоге PlayerOK…"] = "Searching the PlayerOK catalog…",
        ["Совпадений не найдено. Попробуйте другое название."] = "No matches found. Try a different name.",
        ["Сначала найдите и выберите игру."] = "Find and select a game first.",
        ["Загружаю категории…"] = "Loading categories…",
        ["PlayerOK не вернул категории для этой игры."] = "PlayerOK returned no categories for this game.",
        ["Сначала выберите категорию."] = "Select a category first.",
        ["Загружаю способы передачи и поля выбранной категории…"] = "Loading delivery methods and fields for the selected category…",
        ["PlayerOK не вернул данные категории."] = "PlayerOK returned no category data.",
        ["Для этой категории нет доступных способов передачи."] = "No delivery methods are available for this category.",
        ["Выберите фотографии объявления"] = "Choose listing photos",
        ["Заполните название и описание объявления."] = "Fill in the listing title and description.",
        ["Цена должна быть положительным целым числом в рублях."] = "The price must be a positive whole number of rubles.",
        ["Создаю черновик на PlayerOK…"] = "Creating a draft on PlayerOK…",
        ["✅ Черновик создан. Теперь можно выбрать тариф публикации."] = "✅ Draft created. You can now choose a publishing plan.",

        // --- Уведомления ---
        ["✈ Telegram"] = "✈ Telegram",
        ["🔔 В приложении"] = "🔔 In the app",
        ["Быстрые наборы"] = "Quick presets",
        ["Выберите, о каких событиях бот будет писать в Telegram. Автоответы покупателям не зависят от этих настроек."] = "Choose which events the bot reports to Telegram. Buyer auto-replies do not depend on these settings.",
        ["Эти ответы появляются кнопками под уведомлениями о сообщениях в Telegram."] = "These replies appear as buttons under message notifications in Telegram.",
        ["Уведомления внутри приложения и Windows. Эти параметры не влияют на сообщения Telegram."] = "Notifications inside the app and Windows. These options do not affect Telegram messages.",
        ["💬 Сообщения покупателей"] = "💬 Buyer messages",
        ["🔰 Сообщения поддержки"] = "🔰 Support messages",
        ["ℹ️ Системные сообщения чата"] = "ℹ️ System chat messages",
        ["🛒 Новые покупки"] = "🛒 New purchases",
        ["✅ Подтверждения сделок"] = "✅ Deal confirmations",
        ["↩️ Возвраты"] = "↩️ Refunds",
        ["⚠️ Проблемы в сделках"] = "⚠️ Deal problems",
        ["📝 Отзывы"] = "📝 Reviews",
        ["🔓 Разморозка средств"] = "🔓 Funds unfrozen",
        ["🤖 Отчёты об автоответах"] = "🤖 Auto-reply reports",
        ["🔔 Всё"] = "🔔 Everything",
        ["🔰 Только поддержка"] = "🔰 Support only",
        ["🛒 Только покупки"] = "🛒 Purchases only",
        ["🙈 Без поддержки"] = "🙈 No support",
        ["🔕 Выключить всё"] = "🔕 Turn everything off",
        ["Показывать системные уведомления Windows о новых сообщениях и сделках"] = "Show Windows system notifications about new messages and deals",
        ["🔔 Проверить уведомление"] = "🔔 Test notification",
        ["Системные уведомления Windows включены."] = "Windows system notifications are on.",
        ["Системные уведомления Windows выключены."] = "Windows system notifications are off.",
        ["Системные уведомления доступны только в Windows."] = "System notifications are only available on Windows.",
        ["Тестовое уведомление работает."] = "The test notification works.",
        ["PlayerOK Bridge — проверка"] = "PlayerOK Bridge — test",
        ["✅ Тестовое уведомление отправлено, звук проигран."] = "✅ Test notification sent, sound played.",
        ["❌ Не удалось отправить уведомление. Проверьте разрешения Windows."] = "❌ Failed to send the notification. Check Windows permissions.",
        ["⏰ Напоминать, если оплаченный заказ долго не выдан"] = "⏰ Remind me if a paid order is not delivered for a long time",
        ["не повторять"] = "do not repeat",
        ["Напоминания выключены."] = "Reminders are off.",
        ["Зависшие сделки"] = "Stalled deals",
        ["Напомнить"] = "Remind",
        ["Звуки событий"] = "Event sounds",
        ["Звуки и пользовательская музыка доступны только в Windows."] = "Sounds and custom music are only available on Windows.",
        ["Поддерживаются MP3, WAV, WMA и MIDI."] = "MP3, WAV, WMA and MIDI are supported.",
        ["Размер аудиофайла не должен превышать 100 МБ."] = "The audio file must not exceed 100 MB.",
        ["Выбранный аудиофайл пуст."] = "The selected audio file is empty.",
        ["🔊 Звуки событий (покупки, сообщения, уведомления)"] = "🔊 Event sounds (purchases, messages, notifications)",
        ["▶ Тестовый звук запущен."] = "▶ Test sound started.",
        ["Проверьте громкость звука."] = "Check the sound volume.",
        ["Звук доступен только в Windows."] = "Sound is only available on Windows.",
        ["🎵 Файл…"] = "🎵 File…",
        ["Сброс"] = "Reset",
        ["Используется встроенный звук"] = "Using the built-in sound",
        ["Выберите звук события"] = "Choose an event sound",
        ["✅ Пользовательский звук сохранён."] = "✅ Custom sound saved.",
        ["Встроенный звук восстановлен."] = "Built-in sound restored.",
        ["🛒 Новая покупка"] = "🛒 New purchase",
        ["▶ Протестировать уведомление"] = "▶ Test notification",
        ["💬 Новое сообщение"] = "💬 New message",
        ["🔔 Системное уведомление"] = "🔔 System notification",
        ["Фоновая музыка"] = "Background music",
        ["🎵 Включить музыку"] = "🎵 Enable music",
        ["Музыка выключена."] = "Music is off.",
        ["Сейчас играет ваш трек."] = "Your track is playing.",
        ["📂 Загрузить свою музыку"] = "📂 Load your own music",
        ["Выберите фоновую музыку"] = "Choose background music",
        ["✅ Своя музыка загружена и выбрана."] = "✅ Your music was loaded and selected.",
        ["Использовать музыку темы"] = "Use theme music",
        ["Восстановлена музыка выбранной темы GUI."] = "Music of the selected GUI theme restored.",
        ["Музыкальная тема (автоматически под фон или вручную)"] = "Music theme (automatic for the background, or manual)",
        ["Автоматически под фон"] = "Automatic for the background",
        ["Лава-лампа / Ambient"] = "Lava lamp / Ambient",
        ["Переливание / Arpeggio"] = "Flow / Arpeggio",
        ["Закат / Lounge"] = "Sunset / Lounge",
        ["Огонь / Dark drive"] = "Fire / Dark drive",
        ["Снег / Music box"] = "Snow / Music box",
        ["Пузыри / Chill"] = "Bubbles / Chill",
        ["Аврора / Ethereal"] = "Aurora / Ethereal",
        ["Звёзды / Space"] = "Stars / Space",
        ["Минимализм"] = "Minimalism",

        // --- Настройки ---
        ["💾 Сохранить настройки"] = "💾 Save settings",
        ["Токен Telegram-бота (от @BotFather)"] = "Telegram bot token (from @BotFather)",
        ["Ваш Telegram ID (число)"] = "Your Telegram ID (number)",
        ["Токен PlayerOK (cookie token)"] = "PlayerOK token (cookie “token”)",
        ["Cookie __ddg3 / __ddg5 (если PlayerOK просит проверку)"] = "Cookie __ddg3 / __ddg5 (if PlayerOK asks for verification)",
        ["User-Agent браузера, из которого взяты cookie"] = "User-Agent of the browser the cookies were taken from",
        ["Прокси для Telegram (необязательно)"] = "Proxy for Telegram (optional)",
        ["Часовой пояс для статистики (3 = Москва)"] = "Time zone for statistics (3 = Moscow)",
        ["http://user:pass@host:port (необязательно)"] = "http://user:pass@host:port (optional)",
        ["❌ ADMIN_ID должен быть числом."] = "❌ ADMIN_ID must be a number.",
        ["❌ Часовой пояс — число, например 3."] = "❌ The time zone must be a number, e.g. 3.",
        ["Язык интерфейса"] = "Interface language",
        ["Шрифт интерфейса"] = "Interface font",
        ["Внешний вид и язык"] = "Appearance and language",
        ["Системный (по умолчанию)"] = "System (default)",
        ["Шрифт применяется сразу и сохраняется автоматически."] = "The font is applied immediately and saved automatically.",
        ["Язык применяется сразу. Часть сообщений об ошибках может остаться на русском."] = "The language is applied immediately. Some error messages may remain in Russian.",
        ["Подключение и аккаунт"] = "Connection and account",
        ["Выйти из аккаунта Playerok"] = "Sign out of Playerok",
        ["Токен удалён. Перезапустите приложение, чтобы войти заново."] = "Token removed. Restart the app to sign in again.",

        // --- GUI ---
        ["✨ Быстро"] = "✨ Quick",
        ["🖼 Фон"] = "🖼 Background",
        ["🎞 Анимация"] = "🎞 Animation",
        ["🧩 Интерфейс"] = "🧩 Interface",
        ["Оформление"] = "Appearance",
        ["Быстрое оформление"] = "Quick appearance",
        ["Быстрые параметры"] = "Quick options",
        ["Шаблон (применяет цвета, стиль и плотность)"] = "Template (applies colors, style and density)",
        ["Стиль анимации фона"] = "Background animation style",
        ["Цвет сообщений покупателя (слева)"] = "Buyer message color (left)",
        ["Цвет ваших сообщений (справа)"] = "Your message color (right)",
        ["Анимация фона включена"] = "Background animation enabled",
        ["💾 Сохранить оформление"] = "💾 Save appearance",
        ["↺ Сбросить всё"] = "↺ Reset all",
        ["Оформление и переходы сброшены (не забудьте сохранить)."] = "Appearance and transitions reset (don't forget to save).",
        ["Анимация переходов между страницами"] = "Page transition animation",
        ["Без анимации — мгновенное переключение вкладок"] = "No animation — instant tab switching",
        ["▶ Предпросмотр перехода"] = "▶ Preview transition",
        ["Брать цвета полосы из цветов фона"] = "Take band colors from the background colors",
        ["Свечение вокруг полосы"] = "Glow around the band",
        ["Параллакс (старая страница уезжает медленнее, новая подъезжает с запасом)"] = "Parallax (the old page leaves more slowly, the new one arrives with a margin)",
        ["⏸ Останавливать анимацию фона на время перехода (выключено — фон движется и во время перехода)"] = "⏸ Pause the background animation during the transition (off — the background keeps moving)",
        ["Эффект (используется, когда анимация включена)"] = "Effect (used when animation is on)",
        ["Направление (откуда приходит новая страница)"] = "Direction (where the new page comes from)",
        ["Плавность движения"] = "Motion easing",
        ["Цвет 1 — передняя кромка полосы (HEX)"] = "Color 1 — leading edge of the band (HEX)",
        ["Цвет 2 — задняя кромка полосы / свечение (HEX)"] = "Color 2 — trailing edge of the band / glow (HEX)",
        ["Предпросмотр переключает страницу и возвращает её обратно."] = "The preview switches the page and returns it back.",
        ["#RRGGBB (необязательно)"] = "#RRGGBB (optional)",
        ["🫧 Лава-лампа"] = "🫧 Lava lamp",
        ["🌈 Переливание"] = "🌈 Flow",
        ["🌇 Закат"] = "🌇 Sunset",
        ["🔥 Огонь"] = "🔥 Fire",
        ["❄️ Снежинки"] = "❄️ Snowflakes",
        ["🫧 Пузыри"] = "🫧 Bubbles",
        ["🌌 Аврора"] = "🌌 Aurora",
        ["✨ Звёздное небо"] = "✨ Starry sky",
        ["▪ Однотонный"] = "▪ Solid",
        ["Однотонный"] = "Solid",
        ["Переливание"] = "Flow",
        ["Лава-лампа"] = "Lava lamp",
        ["Снежинки"] = "Snowflakes",
        ["Пузыри"] = "Bubbles",
        ["Аврора"] = "Aurora",
        ["Звёзды"] = "Stars",
        ["◢ Диагональный пролёт"] = "◢ Diagonal sweep",
        ["🌊 Волна"] = "🌊 Wave",
        ["➡ Сдвиг"] = "➡ Slide",
        ["🔍 Приближение"] = "🔍 Zoom",
        ["🌫 Затухание"] = "🌫 Fade",
        ["⭕ Круг"] = "⭕ Circle",
        ["◆ Ромб"] = "◆ Diamond",
        ["🪟 Жалюзи"] = "🪟 Blinds",
        ["🔲 Мозаика"] = "🔲 Mosaic",
        ["🎭 Шторки"] = "🎭 Curtains",
        ["🧵 Полосы"] = "🧵 Stripes",
        ["🔄 Переворот"] = "🔄 Flip",
        ["🎲 Случайный"] = "🎲 Random",
        ["Справа налево"] = "Right to left",
        ["Слева направо"] = "Left to right",
        ["Сверху вниз"] = "Top to bottom",
        ["Снизу вверх"] = "Bottom to top",
        ["Линейно"] = "Linear",
        ["Замедление в конце"] = "Ease out",
        ["Плавно (разгон и торможение)"] = "Smooth (ease in and out)",
        ["С отскоком"] = "Bounce",
        ["Резкий старт"] = "Sharp start",
    };
}
