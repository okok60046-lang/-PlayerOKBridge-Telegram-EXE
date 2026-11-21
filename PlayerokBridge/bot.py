"""
Telegram <-> PlayerOK мост.

- Присылает в Telegram уведомления: новые сообщения, оплаченные сделки,
  подтверждения, возвраты, проблемы, отзывы.
- Ответ (Reply) на уведомление о сообщении в Telegram уходит покупателю в чат PlayerOK.
- Автоответы по товарам: бот сам определяет купленный товар и сразу отправляет покупателю
  подходящий ответ. В тексте можно использовать {buyer}, {seller}, {item}, {price}, {deal_id}.
- Уведомления настраиваются по категориям (/notify): сообщения покупателей, поддержка,
  покупки, возвраты, проблемы, отзывы, разморозка средств и т. д.
- Сделки (/deals), статистика, избранные объявления, поиск и пресеты для новых продаж.
- Команды: /start, /balance, /chats, /autoreplies, /products, /check <название товара>,
  /items, /itemsearch <запрос>, /favorites, /presets, /deals, /stats, /notify, /review.
- Когда покупатель подтвердил сделку, бот сам просит его оставить отзыв (/review).

Основано на неофициальной библиотеке playerokapi (https://playerokapi.readthedocs.io).
"""
import asyncio
import html
import io
import json
import logging
import os
import re
import threading
import time
import unicodedata
from datetime import datetime, timedelta, timezone
from types import SimpleNamespace
from collections import OrderedDict

from aiogram import Bot, Dispatcher, F
from aiogram.client.default import DefaultBotProperties
from aiogram.client.session.aiohttp import AiohttpSession
from aiogram.enums import ParseMode
from aiogram.exceptions import TelegramBadRequest, TelegramNetworkError, TelegramRetryAfter
from aiogram.filters import Command
from aiogram.types import (
    CallbackQuery,
    InlineKeyboardButton,
    InlineKeyboardMarkup,
    KeyboardButton,
    Message,
    ReplyKeyboardMarkup,
)
from dotenv import load_dotenv

from playerokapi.account import Account
from playerokapi.enums import EventTypes
try:
    from playerokapi.enums import ItemDealDirections
except ImportError:  # другая версия библиотеки: тогда берутся все сделки без фильтра направления
    ItemDealDirections = None
try:
    from playerokapi.enums import ItemStatuses
except ImportError:
    ItemStatuses = None
from playerokapi.listener.events import (
    DealConfirmedEvent,
    DealHasProblemEvent,
    DealProblemResolvedEvent,
    DealRolledBackEvent,
    NewDealEvent,
    NewMessageEvent,
    NewReviewEvent,
)
from playerokapi.listener.listener import EventListener

import glob

load_dotenv()
if "TG_BOT_TOKEN" not in os.environ:
    # на случай, если файл настроек назван не ".env" (например jopa.env)
    for _f in glob.glob("*env*"):
        load_dotenv(_f)

TG_TOKEN = os.environ["TG_BOT_TOKEN"]
ADMIN_ID = int(os.environ["ADMIN_ID"])
PO_TOKEN = os.environ["PLAYEROK_TOKEN"]
PO_DDG3 = os.getenv("PLAYEROK_DDG3", "")
USER_AGENT = os.getenv(
    "USER_AGENT",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36",
)

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
log = logging.getLogger("po-bridge")

TG_PROXY = os.getenv("TG_PROXY", "").strip()
session = AiohttpSession(proxy=TG_PROXY) if TG_PROXY else None
bot = Bot(TG_TOKEN, session=session, default=DefaultBotProperties(parse_mode=ParseMode.HTML))
dp = Dispatcher()

acc: Account | None = None
# tg message_id уведомления -> id чата PlayerOK (для ответов через Reply)
reply_map: "OrderedDict[int, str]" = OrderedDict()
MAX_MAP = 2000


def esc(value) -> str:
    return html.escape(str(value)) if value is not None else "—"


def make_account() -> Account:
    cookies = f"token={PO_TOKEN}"
    if PO_DDG3:
        cookies = f"__ddg3={PO_DDG3};" + cookies
    return Account(cookies=cookies, user_agent=USER_AGENT).get()


# Быстрые ответы: кнопки под уведомлениями. Меняйте тексты под себя.
QUICK_REPLIES = [
    "Здравствуйте! Сейчас всё сделаю, минутку 🙂",
    "Спасибо за покупку! Если всё устроило, подтвердите сделку и оставьте отзыв ⭐",
    "Подскажите, пожалуйста, подробнее — чем могу помочь?",
    "Сейчас проверю и вернусь с ответом.",
]

MENU_BALANCE = "💰 Баланс"
MENU_CHATS = "💬 Чаты"
MENU_AUTOREPLIES = "✍️ Автоответы"
MENU_PRODUCTS = "🎯 Ответы по товарам"
MENU_ITEMS = "🛍 Мои товары"
MENU_FAVORITES = "⭐ Избранное"
MENU_PRESETS = "📋 Пресеты продаж"
MENU_DEALS = "📋 Сделки"
MENU_STATS = "📊 Статистика"
MENU_NOTIFY = "🔔 Уведомления"
AUTOREPLIES_FILE = os.getenv("AUTOREPLIES_FILE", "auto_replies.json")

main_kb = ReplyKeyboardMarkup(
    keyboard=[
        [KeyboardButton(text=MENU_CHATS), KeyboardButton(text=MENU_AUTOREPLIES)],
        [KeyboardButton(text=MENU_DEALS), KeyboardButton(text=MENU_STATS)],
        [KeyboardButton(text=MENU_PRODUCTS), KeyboardButton(text=MENU_ITEMS)],
        [KeyboardButton(text=MENU_FAVORITES), KeyboardButton(text=MENU_PRESETS)],
        [KeyboardButton(text=MENU_BALANCE), KeyboardButton(text=MENU_NOTIFY)],
    ],
    resize_keyboard=True,
)

# Состояние интерфейса администратора
pending_chat_message: dict[int, str] = {}
draft_chat_message: dict[int, str] = {}
chat_page_state: dict[int, dict] = {}
CHAT_FILTERS = {
    "all": ("📋 Все", ()),
    "support": ("🔰 Поддержка", ("поддержка сделала возврат средств", "поддержка решила проблему", "решила проблему")),
    "delivery": ("📦 Получение товара", ("товар отправлен", "получил товар", "получение товара", "подтвердил получение")),
    "confirm": ("✅ Подтверждение", ("подтвердил получение", "подтвердил сделку", "сделка завершена")),
    "problem": ("⚠️ Проблемы", ("сообщил о проблеме", "открыта проблема", "проблема в сделке", "поддержка решила проблему", "проблема по сделке решена")),
    "refund": ("↩️ Возвраты", ("сделала возврат средств", "сделали возврат средств", "вы сделали возврат средств", "возврат средств", "сделка отменена")),
    "payment": ("💳 Оплаты", ("оплатил покупку", "покупатель оплатил товар", "сделка оплачена", "новая оплаченная сделка")),
}
autoreply_input_state: set[int] = set()
autoreply_edit_state: dict[int, int] = {}  # user_id -> index редактируемого автоответа
CHAT_PAGE_SIZE = 10   # сколько чатов показываем на странице в Telegram
CHAT_FETCH_SIZE = 24  # сколько чатов запрашиваем у PlayerOK за один запрос (максимум API)


def load_autoreplies() -> list[str] | None:
    try:
        with open(AUTOREPLIES_FILE, "r", encoding="utf-8") as f:
            data = json.load(f)
        if isinstance(data, list):
            return [str(x) for x in data if isinstance(x, str) and x.strip()]
    except FileNotFoundError:
        return None
    except Exception:
        log.exception("Не удалось загрузить автоответы")
    return []


def save_autoreplies() -> None:
    _atomic_json_save(AUTOREPLIES_FILE, AUTOREPLIES)


_saved_autoreplies = load_autoreplies()
AUTOREPLIES = QUICK_REPLIES.copy() if _saved_autoreplies is None else _saved_autoreplies


# ---------- Автоответы по товарам ----------
# Каждая запись: {"keyword": "standoff, стандофф", "text": "текст ответа"}.
# keyword — одно или несколько слов через запятую; если любое из них встречается
# в названии купленного товара (без учёта регистра), подставляется этот ответ.
PRODUCT_REPLIES_FILE = os.getenv("PRODUCT_REPLIES_FILE", "product_replies.json")
DEFAULT_PRODUCT_REPLIES = [
    {
        "keyword": "standoff, стандофф, standoff 2",
        "text": "Здравствуйте, {buyer}! Спасибо за покупку Standoff 2 🙂 Напишите, пожалуйста, ваш игровой ID — "
                "я сразу всё сделаю. После получения подтвердите сделку и оставьте отзыв ⭐\n— {seller}",
    },
    {
        # Самый конкретный вариант: и «brawl», и «гем» в названии лота
        "keyword": "brawl+гем, brawl+gem, бравл+гем, бравл+gem",
        "text": "Здравствуйте, {buyer}! Спасибо за покупку гемов Brawl Stars 💎 Напишите, пожалуйста, "
                "данные, необходимые для начисления. После получения подтвердите сделку и оставьте отзыв ⭐\n— {seller}",
    },
    {
        # «Brawl Pass Plus» — три части, поэтому точнее обычного «Brawl Pass» и всегда побеждает его
        "keyword": "brawl+pass+plus, brawl+pass+плюс, brawl+пропуск+plus, brawl+пропуск+плюс, "
                   "бравл+pass+plus, бравл+пропуск+плюс",
        "text": "Здравствуйте, {buyer}! Спасибо за покупку Brawl Pass Plus 💎 Напишите, пожалуйста, "
                "данные, необходимые для активации. После получения подтвердите сделку и оставьте отзыв ⭐\n— {seller}",
    },
    {
        "keyword": "brawl+pass, brawl+пропуск, бравл+pass, бравл+пропуск",
        "text": "Здравствуйте, {buyer}! Спасибо за покупку Brawl Pass 🎫 Напишите, пожалуйста, "
                "данные, необходимые для активации. После получения подтвердите сделку и оставьте отзыв ⭐\n— {seller}",
    },
    {
        # Запасной вариант для остальных товаров по Brawl Stars
        "keyword": "brawl, бравл",
        "text": "Здравствуйте, {buyer}! Спасибо за покупку Brawl Stars 🙂 Напишите, пожалуйста, "
                "данные, необходимые для выдачи заказа. После получения подтвердите сделку и оставьте отзыв ⭐\n— {seller}",
    },
]


def _atomic_json_save(path: str, data) -> None:
    temp_path = path + ".tmp"
    try:
        with open(temp_path, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
            f.flush()
            os.fsync(f.fileno())
        os.replace(temp_path, path)
    except Exception:
        try:
            os.remove(temp_path)
        except OSError:
            pass
        raise


def load_product_replies() -> list[dict]:
    try:
        with open(PRODUCT_REPLIES_FILE, "r", encoding="utf-8") as f:
            data = json.load(f)
        return [
            {"keyword": str(x["keyword"]), "text": str(x["text"])}
            for x in data
            if isinstance(x, dict) and str(x.get("keyword", "")).strip() and str(x.get("text", "")).strip()
        ]
    except FileNotFoundError:
        return [dict(x) for x in DEFAULT_PRODUCT_REPLIES]
    except Exception:
        log.exception("Не удалось загрузить ответы по товарам")
        return []


def save_product_replies() -> None:
    _atomic_json_save(PRODUCT_REPLIES_FILE, PRODUCT_REPLIES)


PRODUCT_REPLIES: list[dict] = load_product_replies()
product_state: dict[int, dict] = {}  # user_id -> шаг ввода (добавление/изменение)


_ZERO_WIDTH_RE = re.compile("[​‌‍⁠﻿­]")
# Латинские буквы, похожие на русские: в названиях лотов их часто смешивают («BИП», «СEРВЕР»).
_LOOKALIKES = str.maketrans("abcehkmoptxy", "авсенкмортху")


def norm_text(value) -> str:
    """Приводит текст к единому виду для сравнения: регистр, ё/е, невидимые символы, латинские «двойники»."""
    text = unicodedata.normalize("NFKC", str(value or ""))
    text = _ZERO_WIDTH_RE.sub("", text).casefold().replace("ё", "е")
    return text.translate(_LOOKALIKES)


def product_aliases(entry: dict) -> list[str]:
    return [norm_text(a).strip() for a in entry["keyword"].split(",") if a.strip()]


def product_title(entry: dict) -> str:
    return entry["keyword"].split(",")[0].strip() or "товар"


def _alias_score(alias: str, name: str):
    """Ключевое слово может состоять из частей через «+» (все части должны быть в названии).
    Возвращает оценку точности (больше частей и длиннее — точнее) или None, если не подошло."""
    parts = [p.strip() for p in alias.split("+") if p.strip()]
    if not parts or not all(p in name for p in parts):
        return None
    return (len(parts), sum(len(p) for p in parts))


def match_products(item_name) -> list:
    """Все подходящие ответы: [(оценка, индекс, ключевое слово)], лучший первым.
    При равной точности выше тот, что стоит раньше в списке."""
    name = norm_text(item_name)
    if not name:
        return []
    found = []
    for i, entry in enumerate(PRODUCT_REPLIES):
        best = None
        for alias in product_aliases(entry):
            score = _alias_score(alias, name)
            if score and (best is None or score > best[0]):
                best = (score, alias)
        if best:
            found.append((best[0], i, best[1]))
    found.sort(key=lambda x: (x[0], -x[1]), reverse=True)
    return found


def find_product_reply(item_name) -> int | None:
    """Возвращает индекс самого точного ответа по названию товара или None."""
    matches = match_products(item_name)
    return matches[0][1] if matches else None


_PLACEHOLDER_RE = re.compile(r"\{(\w+)\}")


class BoundedSet:
    """Множество с ограничением размера: старые записи вытесняются, память не растёт бесконечно."""

    def __init__(self, limit: int = 5000):
        self._limit = limit
        self._items: "OrderedDict" = OrderedDict()

    def add(self, key) -> None:
        self._items[key] = True
        self._items.move_to_end(key)
        while len(self._items) > self._limit:
            self._items.popitem(last=False)

    def discard(self, key) -> None:
        self._items.pop(key, None)

    def __contains__(self, key) -> bool:
        return key in self._items

    def __len__(self) -> int:
        return len(self._items)


auto_replied_deals = BoundedSet()  # сделки, по которым автоответ уже отправлен
deal_by_chat: "OrderedDict[str, object]" = OrderedDict()  # chat_id -> последняя сделка (для подстановок в ответах)
MAX_DEAL_CTX = 500


def remember_deal(chat_id, deal) -> None:
    if not chat_id or deal is None:
        return
    deal_by_chat[chat_id] = deal
    deal_by_chat.move_to_end(chat_id)
    while len(deal_by_chat) > MAX_DEAL_CTX:
        deal_by_chat.popitem(last=False)


def render_reply(text: str, deal) -> str:
    """Подставляет {buyer}, {seller}, {item}, {price}, {deal_id}; неизвестные {поля} остаются как есть."""
    item = getattr(deal, "item", None)
    buyer = getattr(deal, "user", None)
    seller = getattr(deal, "seller", None)
    values = {
        "buyer": getattr(buyer, "username", None) or "покупатель",
        "seller": getattr(seller, "username", None) or getattr(acc, "username", None) or "продавец",
        "item": getattr(item, "name", None) or "",
        "price": getattr(item, "price", None) or "",
        "deal_id": getattr(deal, "id", None) or "",
    }
    return _PLACEHOLDER_RE.sub(lambda m: str(values.get(m.group(1).lower(), m.group(0))), text)


# ---------- AFK-режим ----------
# Второе сообщение, которое уходит сразу после автоответа по товару, если AFK включён.
# Настройки лежат в afk_settings.json (их редактирует приложение); файл перечитывается при каждой сделке,
# поэтому включение/выключение AFK в окне действует без перезапуска бота.
AFK_FILE = os.getenv("AFK_FILE", "afk_settings.json")
DEFAULT_AFK_TEXT = (
    "Здравствуйте, {buyer}! Сейчас я не в сети (AFK) 🌙 Как только вернусь, сразу выдам заказ «{item}». "
    "Спасибо за терпение!\n— {seller}"
)
afk_sent_deals = BoundedSet()  # сделки, по которым AFK-сообщение уже отправлено


def load_afk_settings() -> dict:
    settings = {"enabled": False, "text": DEFAULT_AFK_TEXT, "delay_seconds": 2, "send_without_product": True}
    try:
        with open(AFK_FILE, "r", encoding="utf-8") as f:
            data = json.load(f)
        if isinstance(data, dict):
            if isinstance(data.get("enabled"), bool):
                settings["enabled"] = data["enabled"]
            if isinstance(data.get("text"), str) and data["text"].strip():
                settings["text"] = data["text"]
            if isinstance(data.get("send_without_product"), bool):
                settings["send_without_product"] = data["send_without_product"]
            delay = data.get("delay_seconds")
            if isinstance(delay, (int, float)) and not isinstance(delay, bool):
                settings["delay_seconds"] = max(0.0, min(float(delay), 60.0))
    except FileNotFoundError:
        pass
    except Exception:
        log.exception("Не удалось загрузить настройки AFK-режима")
    return settings


def send_afk_for_deal(deal, chat_id, loop, delay: float = 0.0) -> None:
    """Отправляет AFK-сообщение (один раз на сделку). Пауза выполняется в отдельном потоке, слушатель не блокируется."""
    deal_id = getattr(deal, "id", None)
    if deal_id in afk_sent_deals:
        return
    afk = load_afk_settings()
    if not afk["enabled"]:
        return
    afk_sent_deals.add(deal_id)

    def _send() -> None:
        text = render_reply(afk["text"], deal)
        try:
            acc.send_message(chat_id=chat_id, text=text)
            report = f"🌙 <b>AFK-сообщение отправлено</b>:\n{esc(text)}"
            ok = True
        except Exception as e:
            afk_sent_deals.discard(deal_id)
            log.exception("afk_reply")
            report = f"❌ Не удалось отправить AFK-сообщение по сделке <code>{esc(deal_id)}</code>: {esc(e)}"
            ok = False
        if not ok or notif_on("autoreply"):
            asyncio.run_coroutine_threadsafe(notify(report, chat_id), loop)

    if delay > 0:
        timer = threading.Timer(delay, _send)
        timer.daemon = True
        timer.start()
    else:
        _send()


def auto_reply_for_deal(event, chat_id, loop) -> None:
    """Сама отправляет покупателю автоответ по купленному товару, а затем (если включён AFK) AFK-сообщение."""
    deal = event.deal
    deal_id = getattr(deal, "id", None)
    if deal_id in auto_replied_deals:
        return
    chat_id = chat_id or getattr(getattr(deal, "chat", None), "id", None)
    item_name = getattr(getattr(deal, "item", None), "name", None)
    if not chat_id:
        # Раньше в этом случае бот молчал; теперь сообщает, почему автоответ не ушёл.
        asyncio.run_coroutine_threadsafe(notify(
            f"⚠️ <b>Автоответ не отправлен</b>: у сделки <code>{esc(deal_id)}</code> нет чата с покупателем. "
            "Ответь ему вручную."), loop)
        return
    afk = load_afk_settings()
    idx = find_product_reply(item_name)
    if idx is None:
        if notif_on("autoreply"):
            asyncio.run_coroutine_threadsafe(notify(
                "ℹ️ <b>Автоответ не сработал</b>: название лота не подошло ни под один тег.\n"
                f"Название: <code>{esc(item_name)}</code>\n"
                f"Проверь теги командой <code>/check {esc(item_name or 'название лота')}</code>.",
                chat_id), loop)
        # Покупатель всё равно должен узнать, что вас нет на месте.
        if afk["enabled"] and afk["send_without_product"]:
            auto_replied_deals.add(deal_id)
            send_afk_for_deal(deal, chat_id, loop)
        return
    entry = PRODUCT_REPLIES[idx]
    text = render_reply(entry["text"], deal)
    ok = True
    try:
        acc.send_message(chat_id=chat_id, text=text)
        auto_replied_deals.add(deal_id)
        report = f"🤖 <b>Автоответ отправлен</b> («{esc(product_title(entry))}»):\n{esc(text)}"
    except Exception as e:
        ok = False
        log.exception("auto_reply")
        report = f"❌ Не удалось отправить автоответ по сделке <code>{esc(deal_id)}</code>: {esc(e)}"
    # Ошибки показываются всегда, успешные отчёты — по настройке «Отчёты об автоответах».
    if not ok or notif_on("autoreply"):
        asyncio.run_coroutine_threadsafe(notify(report, chat_id), loop)
    # Порядок: сначала ответ по товару, потом (с паузой) AFK-сообщение.
    if ok and afk["enabled"]:
        send_afk_for_deal(deal, chat_id, loop, delay=afk["delay_seconds"])


def quick_kb(chat_id: str, item_name=None) -> InlineKeyboardMarkup:
    rows = []
    idx = find_product_reply(item_name)
    if idx is not None:
        rows.append([InlineKeyboardButton(
            text=f"📨 Отправить ответ: {product_title(PRODUCT_REPLIES[idx])}"[:60],
            callback_data=f"pr_send:{idx}:{chat_id}",
        )])
    rows += [
        [InlineKeyboardButton(text=t if len(t) <= 40 else t[:37] + "…", callback_data=f"q:{i}:{chat_id}")]
        for i, t in enumerate(AUTOREPLIES)
    ]
    return InlineKeyboardMarkup(inline_keyboard=rows)


async def notify(
    text: str,
    chat_id: str | None = None,
    item_name=None,
    *,
    category: str | None = None,
) -> None:
    """Отправляет уведомление; категорию повторно проверяем прямо перед отправкой.

    Повторная проверка важна для событий, уже поставленных в очередь event loop
    в момент переключения настройки в Telegram.
    """
    if category is not None and not notif_on(category):
        log.info("Уведомление категории %s пропущено: категория выключена", category)
        return

    markup = quick_kb(chat_id, item_name) if chat_id else None
    try:
        msg = await _send_to_admin(text, markup)
    except Exception:
        # Исключение из run_coroutine_threadsafe никто не читает — без лога уведомление пропало бы молча.
        log.exception("Не удалось отправить уведомление в Telegram")
        return
    if chat_id:
        reply_map[msg.message_id] = chat_id
        while len(reply_map) > MAX_MAP:
            reply_map.popitem(last=False)


TG_TEXT_LIMIT = 4000  # у Telegram предел 4096 символов; небольшой запас


def truncate_html(text: str, limit: int = TG_TEXT_LIMIT) -> str:
    """Обрезает HTML-текст, не разрывая тег или сущность (&amp; и т. п.) посередине."""
    if len(text) <= limit:
        return text
    cut = text[: limit - 1]
    lt = cut.rfind("<")
    if lt > cut.rfind(">"):
        cut = cut[:lt]
    amp = cut.rfind("&")
    if amp > cut.rfind(";") and len(cut) - amp <= 10:
        cut = cut[:amp]
    return cut + "…"


async def _send_to_admin(text: str, markup):
    """Отправка админу: длинный текст режется, при flood-лимите и сбое сети — повтор,
    при ошибке разбора HTML (оборванный тег после обрезки) — повтор обычным текстом."""
    text = truncate_html(text)
    last_error: Exception | None = None
    for attempt in range(3):
        try:
            return await bot.send_message(ADMIN_ID, text, reply_markup=markup)
        except TelegramRetryAfter as e:
            last_error = e
            await asyncio.sleep(float(e.retry_after) + 1)
        except TelegramBadRequest as e:
            if "parse entities" not in str(e).lower():
                raise
            plain = html.unescape(re.sub(r"<[^>]+>", "", text))
            return await bot.send_message(ADMIN_ID, plain, reply_markup=markup, parse_mode=None)
        except TelegramNetworkError as e:
            last_error = e
            await asyncio.sleep(2 * (attempt + 1))
    raise last_error if last_error else RuntimeError("не удалось отправить сообщение")


# ---------- Playerok -> Telegram ----------

def deal_summary(deal) -> str:
    item = getattr(deal, "item", None)
    buyer = getattr(deal, "user", None)
    lines = [
        f"📦 <b>{esc(getattr(item, 'name', None))}</b>",
        f"💰 {esc(getattr(item, 'price', None))} ₽",
        f"👤 {esc(getattr(buyer, 'username', None))}",
        f"🆔 сделка <code>{esc(getattr(deal, 'id', None))}</code>",
    ]
    comment = getattr(deal, "comment_from_buyer", None)
    if comment:
        lines.append(f"💬 {esc(comment)}")
    return "\n".join(lines)


SYSTEM_MESSAGE_MAP = {
    "{{DEAL_CONFIRMED}}": "✅ Покупатель подтвердил сделку",
    "{{DEAL_CONFIRMED_AUTOMATICALLY}}": "✅ Сделка подтверждена автоматически",
    "{{DEAL_ROLLED_BACK}}": "↩️ Вы сделали возврат средств",
    "{{DEAL_REFUNDED}}": "↩️ Вы сделали возврат средств",
    "{{DEAL_ROLLED_BACK_BY_SUPPORT}}": "🔰 Поддержка сделала возврат средств",
    "{{DEAL_REFUNDED_BY_SUPPORT}}": "🔰 Поддержка сделала возврат средств",
    "{{DEAL_CONFIRMED_BY_BUYER}}": "✅ Покупатель подтвердил получение",
    "{{DEAL_HAS_PROBLEM}}": "⚠️ Покупатель сообщил о проблеме",
    "{{DEAL_PROBLEM_RESOLVED}}": "🔰 Поддержка решила проблему",
    "{{NEW_DEAL}}": "🛒 Новая оплаченная сделка",
    "{{DEAL_PAID}}": "🛒 Сделка оплачена",
    "{{DEAL_CREATED}}": "🛒 Создана новая сделка",
    "{{DEAL_COMPLETED}}": "✅ Сделка завершена",
    "{{DEAL_CANCELED}}": "↩️ Сделка отменена",
    "{{DEAL_CANCELLED}}": "↩️ Сделка отменена",
    "{{NEW_REVIEW}}": "📝 Получен новый отзыв",
    "{{ITEM_PAID}}": "💳 Покупатель оплатил товар",
    "{{ITEM_SENT}}": "📦 Товар отправлен покупателю",
}


# Предкомпилированные шаблоны: не пересоздаём регулярные выражения при каждом сообщении.
_MARKER_PATTERNS = [
    (re.compile(re.escape(marker), re.IGNORECASE), replacement)
    for marker, replacement in SYSTEM_MESSAGE_MAP.items()
]
_UNKNOWN_MARKER_RE = re.compile(r"\{\{[^{}]+\}\}")
_BUYER_EVENT_PATTERNS = (
    (re.compile(r"^.+?\s+(оплатил покупку)$", re.IGNORECASE), r"Покупатель \1"),
    (re.compile(r"^.+?\s+(сообщил о проблеме)$", re.IGNORECASE), r"Покупатель \1"),
    (re.compile(r"^.+?\s+(подтвердил получение)$", re.IGNORECASE), r"Покупатель \1"),
)


def readable_system_message(text: str) -> str:
    """Переводит известные маркеры PlayerOK и скрывает имена в системных событиях."""
    stripped = str(text or "").strip()
    if stripped in SYSTEM_MESSAGE_MAP:
        return SYSTEM_MESSAGE_MAP[stripped]

    for pattern, replacement in _MARKER_PATTERNS:
        stripped = pattern.sub(replacement, stripped)

    if "{{" in stripped and "}}" in stripped:
        stripped = _UNKNOWN_MARKER_RE.sub("", stripped).strip()
        if not stripped:
            return "ℹ️ Событие сделки обновлено в PlayerOK."

    for pattern, replacement in _BUYER_EVENT_PATTERNS:
        stripped = pattern.sub(replacement, stripped)

    if re.fullmatch(r"🔰?\s*поддержка\s+сделала\s+возврат\s+средств", stripped, re.IGNORECASE):
        return "🔰 Поддержка сделала возврат средств"
    if re.fullmatch(r"вы\s+сделали\s+возврат\s+средств", stripped, re.IGNORECASE):
        return "↩️ Вы сделали возврат средств"
    return stripped


_ANY_BRACES_RE = re.compile(r"\{[^{}]*\}")


def is_system_marker_message(text) -> bool:
    """Служебное сообщение PlayerOK: шаблон в фигурных скобках, например {{ITEM_SENT}}."""
    raw = str(text or "").strip()
    if not raw:
        return False
    if _ANY_BRACES_RE.search(raw) is None:
        return False
    # Если после удаления всех {...} остался осмысленный текст, это обычное сообщение со скобками.
    leftover = _ANY_BRACES_RE.sub("", raw.replace("{{", "{").replace("}}", "}")).strip(" \n\t.,;:-—")
    return not leftover or raw.startswith("{{")


def format_chat_time(value) -> str:
    raw = str(value or "")
    if len(raw) >= 16 and raw[10] in "T ":
        return f"{raw[8:10]}.{raw[5:7]} {raw[11:16]}"
    return ""


seen_message_ids: "OrderedDict[str, bool]" = OrderedDict()  # чтобы не присылать одно сообщение дважды


def classify_message(msg) -> str:
    """Кто прислал сообщение: own (вы), support (поддержка), system (служебное) или buyer (покупатель)."""
    author = getattr(msg, "user", None)
    if author is not None and acc is not None and getattr(author, "id", None) == getattr(acc, "id", None):
        return "own"
    raw = str(getattr(msg, "text", None) or "").strip()
    role = getattr(author, "role", None)
    role_name = str(getattr(role, "name", role) or "").upper()
    if role_name in ("MODERATOR", "CHECKER") or "SUPPORT" in role_name:
        return "support"
    has_marker = "{{" in raw and "}}" in raw
    if author is None or role_name == "BOT" or has_marker:
        if "SUPPORT" in raw.upper() or "поддержк" in readable_system_message(raw).casefold():
            return "support"
        return "system"
    return "buyer"


def format_event(event):
    """Возвращает (текст, chat_id для ответа, категория уведомления) или None, если событие не нужно."""
    chat = getattr(event, "chat", None)
    chat_id = getattr(chat, "id", None)

    if isinstance(event, NewMessageEvent):
        msg = event.message
        kind = classify_message(msg)
        if kind == "own":
            return None  # свои сообщения не дублируем
        msg_id = getattr(msg, "id", None)
        if msg_id is not None:
            if msg_id in seen_message_ids:
                return None
            seen_message_ids[msg_id] = True
            while len(seen_message_ids) > 2000:
                seen_message_ids.popitem(last=False)
        author = getattr(msg, "user", None)
        text = readable_system_message(str(getattr(msg, "text", None) or "[вложение / без текста]"))
        if kind == "support":
            if text.startswith("🔰"):
                return esc(text), chat_id, "msg_support"
            name = getattr(author, "username", None) or "Поддержка"
            return f"🔰 <b>{esc(name)}</b>:\n{esc(text)}", chat_id, "msg_support"
        if kind == "system":
            return esc(text), chat_id, "msg_system"
        return (
            f"💬 <b>{esc(getattr(author, 'username', None) or 'Покупатель')}</b>:\n{esc(text)}\n\n"
            f"<i>Ответьте на это сообщение (Reply), чтобы отправить ответ на PlayerOK.</i>",
            chat_id,
            "msg_buyer",
        )
    if isinstance(event, NewDealEvent):
        return f"🛒 <b>Новая оплаченная сделка</b>\n{deal_summary(event.deal)}", chat_id, "deal_new"
    if isinstance(event, DealConfirmedEvent):
        return f"✅ <b>Покупатель подтвердил сделку</b>\n{deal_summary(event.deal)}", chat_id, "deal_confirmed"
    if isinstance(event, DealRolledBackEvent):
        return f"↩️ <b>Возврат средств по сделке</b>\n{deal_summary(event.deal)}", chat_id, "deal_refund"
    if isinstance(event, DealHasProblemEvent):
        return f"⚠️ <b>Проблема в сделке</b>\n{deal_summary(event.deal)}", chat_id, "deal_problem"
    if isinstance(event, DealProblemResolvedEvent):
        return f"🟢 <b>Проблема в сделке решена</b>\n{deal_summary(event.deal)}", chat_id, "deal_problem"
    if isinstance(event, NewReviewEvent):
        review = getattr(event.deal, "review", None)
        extra = ""
        if review is not None:
            extra = f"\n⭐ {esc(getattr(review, 'rating', None))}\n{esc(getattr(review, 'text', None))}"
        return f"📝 <b>Новый отзыв</b>\n{deal_summary(event.deal)}{extra}", chat_id, "review"
    return None


def listener_thread(loop: asyncio.AbstractEventLoop) -> None:
    """Блокирующий слушатель PlayerOK в отдельном потоке с авто-переподключением."""
    global acc
    while True:
        try:
            acc = make_account()
            log.info("PlayerOK: вход выполнен как %s", acc.username)
            listener = EventListener(acc)
            for event in listener.listen():
                try:
                    try:
                        if event.type is EventTypes.CHAT_INITIALIZED:
                            continue
                    except AttributeError:
                        pass
                    formatted = format_event(event)
                    if formatted:
                        text, chat_id, category = formatted
                        item_name = None
                        if isinstance(event, NewDealEvent):
                            # Название лота нужно кнопке «Отправить ответ»; сделка — для подстановок {buyer}, {item}…
                            item_name = getattr(getattr(event.deal, "item", None), "name", None)
                            remember_deal(chat_id, event.deal)
                        # Проверка выполняется внутри notify непосредственно перед отправкой,
                        # а не только в listener-потоке: выключенная категория не пройдёт.
                        asyncio.run_coroutine_threadsafe(
                            notify(text, chat_id, item_name, category=category), loop
                        )
                    # Автоответ по товару не зависит от настроек уведомлений.
                    if isinstance(event, NewDealEvent):
                        auto_reply_for_deal(event, getattr(getattr(event, "chat", None), "id", None), loop)
                    if isinstance(event, DealConfirmedEvent):
                        review_request_for_deal(event, loop)
                except Exception:
                    log.exception("Ошибка обработки события, продолжаю")
            # listen() завершился без ошибки: без паузы цикл бесконечно и быстро дёргал бы API.
            log.warning("Слушатель PlayerOK завершился, переподключение через 15 с")
            time.sleep(15)
        except Exception:
            log.exception("Ошибка слушателя PlayerOK, переподключение через 15 с")
            time.sleep(15)


# ---------- Интерфейс чатов ----------

def chat_partner(chat) -> str:
    """Пытается определить собеседника по участникам чата."""
    for user in (getattr(chat, "users", None) or []):
        if getattr(user, "id", None) != getattr(acc, "id", None):
            return getattr(user, "username", None) or "Покупатель"
    last_message = getattr(chat, "last_message", None)
    author = getattr(last_message, "user", None)
    if author and getattr(author, "id", None) != getattr(acc, "id", None):
        return getattr(author, "username", None) or "Покупатель"
    return "Чат PlayerOK"


def chat_matches_filter(chat, filter_key: str) -> bool:
    """Проверяет последнее сообщение чата на соответствие выбранной категории."""
    if filter_key == "all":
        return True
    _label, phrases = CHAT_FILTERS.get(filter_key, CHAT_FILTERS["all"])
    last_message = getattr(chat, "last_message", None)
    raw = str(getattr(last_message, "text", None) or "")
    readable = readable_system_message(raw).casefold()
    raw_lower = raw.casefold()
    return any(phrase.casefold() in readable or phrase.casefold() in raw_lower for phrase in phrases)


def chat_list_keyboard(chats, page_info, page_index: int, filter_key: str = "all") -> InlineKeyboardMarkup:
    rows = []
    # Фильтры в две строки, активный фильтр отмечается галочкой.
    filter_buttons = []
    for key, (label, _phrases) in CHAT_FILTERS.items():
        shown = f"✓ {label}" if key == filter_key else label
        filter_buttons.append(InlineKeyboardButton(text=shown, callback_data=f"chats:filter:{key}"))
    rows.append(filter_buttons[:3])
    rows.append(filter_buttons[3:])

    for chat in chats:
        name = chat_partner(chat)
        last_message = getattr(chat, "last_message", None)
        preview = readable_system_message(str(getattr(last_message, "text", None) or "Вложение / без текста"))
        preview = preview.replace("\n", " ")
        label = f"{name}: {preview}"
        if len(label) > 58:
            label = label[:55] + "…"
        unread = int(getattr(chat, "unread_messages_counter", 0) or 0)
        if unread:
            label = f"🔵 {label} ({unread})"
        if len(label) > 60:
            label = label[:59] + "…"
        rows.append([InlineKeyboardButton(text=label, callback_data=f"open:{chat.id}")])

    nav = []
    if page_index > 0:
        nav.append(InlineKeyboardButton(text="⬅️ Назад", callback_data="chats:prev"))
    if getattr(page_info, "has_next_page", False):
        nav.append(InlineKeyboardButton(text="Вперёд ➡️", callback_data="chats:next"))
    if nav:
        rows.append(nav)
    rows.append([InlineKeyboardButton(text="🔄 Обновить", callback_data="chats:refresh")])
    return InlineKeyboardMarkup(inline_keyboard=rows)


def chat_controls(chat_id: str) -> InlineKeyboardMarkup:
    return InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="✍️ Просто написать", callback_data=f"write:{chat_id}")],
        [InlineKeyboardButton(text="💬 Ответить автоответом", callback_data=f"ar_choose:{chat_id}")],
        [InlineKeyboardButton(text="⬅️ Назад к списку чатов", callback_data="chats:back")],
    ])


def autoreplies_keyboard() -> InlineKeyboardMarkup:
    rows = []
    for i, text in enumerate(AUTOREPLIES):
        label = text.replace("\n", " ")
        if len(label) > 48:
            label = label[:45] + "…"
        rows.append([InlineKeyboardButton(text=f"{i + 1}. {label}", callback_data=f"ar_view:{i}")])
    rows.append([InlineKeyboardButton(text="➕ Добавить автоответ", callback_data="ar_add")])
    rows.append([InlineKeyboardButton(text="🎯 Ответы по товарам", callback_data="pr_list")])
    rows.append([InlineKeyboardButton(text="⭐ Просьба об отзыве", callback_data="rv:s")])
    return InlineKeyboardMarkup(inline_keyboard=rows)


async def load_all_chats() -> list:
    """Загружает все страницы PlayerOK, чтобы фильтры работали по всему списку."""
    all_chats = []
    cursor = None
    seen_cursors = set()
    # Ограничение защищает от зацикливания при некорректном курсоре API.
    for _ in range(1000):
        # API отдаёт до 24 чатов за запрос; раньше брали по 10 — запросов было в 2,4 раза больше.
        result = await asyncio.to_thread(acc.get_chats, count=CHAT_FETCH_SIZE, after_cursor=cursor)
        all_chats.extend(getattr(result, "chats", []) or [])
        page_info = getattr(result, "page_info", None)
        if not getattr(page_info, "has_next_page", False):
            break
        next_cursor = getattr(page_info, "end_cursor", None)
        if not next_cursor or next_cursor in seen_cursors:
            break
        seen_cursors.add(next_cursor)
        cursor = next_cursor
    return all_chats


async def show_chats(target, *, reset: bool = False):
    """Фильтрует полный список чатов и выводит по 10 результатов на страницу."""
    if acc is None:
        return await target.answer("PlayerOK ещё подключается. Попробуй чуть позже.")
    admin_id = target.chat.id
    state = chat_page_state.setdefault(admin_id, {"index": 0, "filter": "all", "all_chats": None})
    state.setdefault("filter", "all")
    if reset:
        state["index"] = 0
        state["all_chats"] = None
    try:
        if state.get("all_chats") is None:
            state["all_chats"] = await load_all_chats()
        all_chats = state["all_chats"]
        filter_key = state.get("filter", "all")
        filtered = [chat for chat in all_chats if chat_matches_filter(chat, filter_key)]
        total_pages = max(1, (len(filtered) + CHAT_PAGE_SIZE - 1) // CHAT_PAGE_SIZE)
        state["index"] = min(max(0, state.get("index", 0)), total_pages - 1)
        page_chats = filtered[state["index"] * CHAT_PAGE_SIZE:(state["index"] + 1) * CHAT_PAGE_SIZE]
        page_info = SimpleNamespace(has_next_page=state["index"] + 1 < total_pages)
        filter_label = CHAT_FILTERS.get(filter_key, CHAT_FILTERS["all"])[0]
        if not filtered:
            text = "💬 Чатов пока нет." if filter_key == "all" else f"💬 <b>{esc(filter_label)}</b>\nЧатов по этому фильтру не найдено."
        else:
            text = (f"💬 <b>Чаты PlayerOK</b> · фильтр: {esc(filter_label)}\n"
                    f"Найдено: {len(filtered)} · страница {state['index'] + 1}/{total_pages}. Выбери чат.")
        await target.answer(text, reply_markup=chat_list_keyboard(page_chats, page_info, state["index"], filter_key))
    except Exception as e:
        log.exception("get_chats")
        await target.answer(f"Не удалось загрузить чаты: {esc(e)}")


# Сколько страниц (по 24 сообщения, сначала новые) читать при открытии чата. Раньше — до 200 страниц:
# длинный чат грузился десятки секунд и превращался в десятки сообщений Telegram. Меняется через env.
CHAT_HISTORY_MAX_PAGES = max(1, int(os.getenv("CHAT_HISTORY_MAX_PAGES", "10")))


async def load_chat_history(chat_id: str) -> list:
    """Загружает последние сообщения чата (до CHAT_HISTORY_MAX_PAGES страниц), пропуская служебные {{...}}."""
    collected: dict = {}
    cursor = None
    seen_cursors = set()
    for _ in range(CHAT_HISTORY_MAX_PAGES):
        result = await asyncio.to_thread(acc.get_chat_messages, chat_id=chat_id, count=24, after_cursor=cursor)
        for m in getattr(result, "messages", []) or []:
            if is_system_marker_message(getattr(m, "text", None)):
                continue
            collected[getattr(m, "id", None) or id(m)] = m
        page_info = getattr(result, "page_info", None)
        if not getattr(page_info, "has_next_page", False):
            break
        cursor = getattr(page_info, "end_cursor", None)
        if not cursor or cursor in seen_cursors:
            break
        seen_cursors.add(cursor)
    return sorted(collected.values(), key=lambda item: str(getattr(item, "created_at", "") or ""))


def split_blocks(header: str, blocks: list[str], limit: int = 3900) -> list[str]:
    """Склеивает блоки в сообщения Telegram, не разрывая блок (и HTML-теги внутри него)."""
    parts: list[str] = []
    current = header
    for block in blocks:
        # Одно очень длинное сообщение режем по границе строк/символов (теги в блоке только в заголовке).
        while len(block) > limit:
            cut = block.rfind("\n", 0, limit)
            cut = cut if cut > 0 else limit
            chunk, block = block[:cut], block[cut:].lstrip("\n")
            if current.strip():
                parts.append(current)
            current = chunk
        if len(current) + len(block) + 2 > limit:
            parts.append(current)
            current = block
        else:
            current = f"{current}\n\n{block}" if current else block
    if current.strip():
        parts.append(current)
    return parts


async def show_chat(target, chat_id: str):
    if acc is None:
        return await target.answer("PlayerOK ещё подключается. Попробуй чуть позже.")
    try:
        chat = await asyncio.to_thread(acc.get_chat, chat_id=chat_id)
        messages = await load_chat_history(chat_id)
        header = f"💬 <b>Переписка с {esc(chat_partner(chat))}</b> · сообщений: {len(messages)}"
        blocks = []
        last_day = None
        for msg in messages:
            author = getattr(msg, "user", None)
            is_mine = author is not None and getattr(author, "id", None) == acc.id
            role = str(getattr(getattr(author, "role", None), "name", getattr(author, "role", "")) or "").upper()
            if is_mine:
                name = "➡️ Вы"
            elif role in ("MODERATOR", "CHECKER") or "SUPPORT" in role:
                name = "🔰 " + (getattr(author, "username", None) or "Поддержка")
            else:
                name = "⬅️ " + (getattr(author, "username", None) or "Покупатель")
            body = str(getattr(msg, "text", None) or "[вложение / без текста]").strip()
            stamp = format_chat_time(getattr(msg, "created_at", None))
            day, _, hhmm = stamp.partition(" ")
            day_line = ""
            if day and day != last_day:
                day_line = f"<i>— {day} —</i>\n"
                last_day = day
            time_part = f" <i>{hhmm}</i>" if hhmm else ""
            blocks.append(f"{day_line}<b>{esc(name)}</b>{time_part}\n{esc(body)}")
        if not blocks:
            blocks.append("Сообщений пока нет.")
        parts = split_blocks(header, blocks)
        for i, part in enumerate(parts):
            last = i == len(parts) - 1
            await target.answer(part, reply_markup=chat_controls(chat_id) if last else None)
        try:
            await asyncio.to_thread(acc.mark_chat_as_read, chat_id=chat_id)
        except Exception:
            log.debug("Не удалось отметить чат как прочитанный", exc_info=True)
    except Exception as e:
        log.exception("open_chat")
        await target.answer(f"Не удалось открыть переписку: {esc(e)}")


def is_admin(message: Message) -> bool:
    return message.from_user is not None and message.from_user.id == ADMIN_ID


@dp.message(F.text == MENU_AUTOREPLIES, F.func(is_admin))
@dp.message(Command("autoreplies"), F.func(is_admin))
async def cmd_autoreplies(message: Message):
    text = "✍️ <b>Мои автоответы</b>\nВыбери ответ для просмотра/удаления или добавь новый."
    if not AUTOREPLIES:
        text = "✍️ Автоответов пока нет. Нажми «Добавить автоответ»."
    await message.answer(text, reply_markup=autoreplies_keyboard())


@dp.callback_query(F.data == "ar_add", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def autoreply_add_start(call: CallbackQuery):
    autoreply_edit_state.pop(call.from_user.id, None)
    autoreply_input_state.add(call.from_user.id)
    await call.answer()
    await call.message.answer("Отправь следующим сообщением текст нового автоответа. Для отмены напиши /cancel.")


@dp.callback_query(F.data.startswith("ar_view:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def autoreply_view(call: CallbackQuery):
    try:
        idx = int(call.data.split(":", 1)[1])
        text = AUTOREPLIES[idx]
    except (ValueError, IndexError):
        return await call.answer("Автоответ не найден", show_alert=True)
    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="✏️ Изменить", callback_data=f"ar_edit:{idx}")],
        [InlineKeyboardButton(text="🗑 Удалить", callback_data=f"ar_del:{idx}")],
        [InlineKeyboardButton(text="⬅️ К списку автоответов", callback_data="ar_list")],
    ])
    await call.answer()
    await call.message.answer(f"<b>Автоответ {idx + 1}</b>\n\n{esc(text)}", reply_markup=kb)


@dp.callback_query(F.data.startswith("ar_edit:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def autoreply_edit_start(call: CallbackQuery):
    try:
        idx = int(call.data.split(":", 1)[1])
        current = AUTOREPLIES[idx]
    except (ValueError, IndexError):
        return await call.answer("Автоответ не найден", show_alert=True)
    autoreply_edit_state[call.from_user.id] = idx
    autoreply_input_state.add(call.from_user.id)
    await call.answer()
    await call.message.answer(
        f"✏️ Отправь новый текст для автоответа {idx + 1}.\n"
        "Текущий текст ниже. Для отмены напиши /cancel.\n\n" + esc(current)
    )


@dp.callback_query(F.data.startswith("ar_del:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def autoreply_delete(call: CallbackQuery):
    try:
        idx = int(call.data.split(":", 1)[1])
        removed = AUTOREPLIES.pop(idx)
        save_autoreplies()
        await call.answer("Автоответ удалён")
        await call.message.edit_text(f"🗑 Удалён автоответ: {esc(removed[:100])}", reply_markup=autoreplies_keyboard())
    except (ValueError, IndexError):
        await call.answer("Автоответ не найден", show_alert=True)
    except Exception as e:
        log.exception("delete autoreply")
        await call.answer("Не удалось сохранить изменения", show_alert=True)


@dp.callback_query(F.data == "ar_list", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def autoreply_list_callback(call: CallbackQuery):
    await call.answer()
    await call.message.answer("✍️ <b>Мои автоответы</b>", reply_markup=autoreplies_keyboard())


@dp.message(Command("cancel"), F.func(is_admin))
async def cancel_autoreply_add(message: Message):
    user_id = message.from_user.id
    if preset_name_state.pop(user_id, None) is not None:
        return await message.answer("Сохранение пресета отменено. Мастер создания объявления сохранён.")
    if user_id in listing_search_state:
        listing_search_state.discard(user_id)
        return await message.answer("Поиск товаров отменён.")
    if listing_state.pop(message.from_user.id, None) is not None:
        return await message.answer("Действие с товарами отменено.")
    if product_state.pop(message.from_user.id, None) is not None:
        return await message.answer("Действие отменено.")
    if message.from_user.id in review_state:
        review_state.discard(message.from_user.id)
        return await message.answer("Изменение текста отменено.")
    was_editing = autoreply_edit_state.pop(message.from_user.id, None) is not None
    was_adding = message.from_user.id in autoreply_input_state
    autoreply_input_state.discard(message.from_user.id)
    if was_editing or was_adding:
        await message.answer("Изменение автоответа отменено." if was_editing else "Добавление автоответа отменено.")


@dp.message(F.text, F.func(lambda m: m.from_user is not None and m.from_user.id == ADMIN_ID and m.from_user.id in autoreply_input_state and not _is_listing_text_stage(m.from_user.id)))
async def autoreply_add_text(message: Message):
    user_id = message.from_user.id
    text = message.text.strip()
    if not text:
        return await message.answer("Пустой автоответ не сохранён. Отправь текст ещё раз или /cancel.")

    edit_idx = autoreply_edit_state.pop(user_id, None)
    if edit_idx is not None:
        if not 0 <= edit_idx < len(AUTOREPLIES):
            return await message.answer("Этот автоответ уже удалён. Открой меню автоответов ещё раз.")
        old_text = AUTOREPLIES[edit_idx]
        AUTOREPLIES[edit_idx] = text
        try:
            save_autoreplies()
        except Exception as e:
            AUTOREPLIES[edit_idx] = old_text
            log.exception("save edited autoreply")
            return await message.answer(f"Не удалось сохранить изменения: {esc(e)}")
        await message.answer("✅ Автоответ изменён.", reply_markup=autoreplies_keyboard())
        return

    autoreply_input_state.discard(user_id)
    AUTOREPLIES.append(text)
    try:
        save_autoreplies()
    except Exception as e:
        AUTOREPLIES.pop()
        log.exception("save autoreply")
        return await message.answer(f"Не удалось сохранить автоответ: {esc(e)}")
    await message.answer("✅ Автоответ сохранён.", reply_markup=autoreplies_keyboard())


@dp.callback_query(F.data.startswith("ar_choose:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def choose_autoreply_for_chat(call: CallbackQuery):
    chat_id = call.data.split(":", 1)[1]
    if not AUTOREPLIES:
        return await call.answer("Сначала добавь автоответ в меню «Автоответы»", show_alert=True)
    rows = []
    for i, text in enumerate(AUTOREPLIES):
        label = text.replace("\n", " ")
        if len(label) > 45:
            label = label[:42] + "…"
        rows.append([InlineKeyboardButton(text=f"{i + 1}. {label}", callback_data=f"ar_send:{i}:{chat_id}")])
    rows.append([InlineKeyboardButton(text="⬅️ Отмена", callback_data=f"open:{chat_id}")])
    await call.answer()
    await call.message.answer("Выбери автоответ для отправки в этот чат:", reply_markup=InlineKeyboardMarkup(inline_keyboard=rows))


@dp.callback_query(F.data.startswith("ar_send:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def send_selected_autoreply(call: CallbackQuery):
    try:
        _, idx_raw, chat_id = call.data.split(":", 2)
        text = AUTOREPLIES[int(idx_raw)]
        if acc is None:
            return await call.answer("PlayerOK ещё не подключён", show_alert=True)
        await asyncio.to_thread(acc.send_message, chat_id=chat_id, text=text)
        await call.answer("Автоответ отправлен ✅")
        await call.message.edit_text(f"✅ Автоответ отправлен в выбранный чат:\n{esc(text)}")
    except (ValueError, IndexError):
        await call.answer("Этот автоответ уже недоступен", show_alert=True)
    except Exception as e:
        log.exception("send selected autoreply")
        await call.answer("Ошибка отправки", show_alert=True)
        await call.message.answer(f"❌ Не удалось отправить автоответ: {esc(e)}")



# ---------- Ответы по товарам: меню управления ----------

def products_keyboard() -> InlineKeyboardMarkup:
    rows = []
    for i, entry in enumerate(PRODUCT_REPLIES):
        rows.append([InlineKeyboardButton(text=f"{i + 1}. {product_title(entry)}"[:55], callback_data=f"pr_view:{i}")])
    rows.append([InlineKeyboardButton(text="➕ Добавить товар", callback_data="pr_add")])
    return InlineKeyboardMarkup(inline_keyboard=rows)


PRODUCTS_HELP = (
    "🎯 <b>Ответы по товарам</b>\n"
    "Когда покупатель оплатит товар, бот найдёт в его названии ключевое слово и "
    "<b>сам отправит</b> покупателю соответствующий ответ.\n\n"
    "Подстановки в тексте: <code>{buyer}</code> — ник покупателя, <code>{seller}</code> — ваш ник, "
    "<code>{item}</code> — название товара, <code>{price}</code> — цена, <code>{deal_id}</code> — номер сделки.\n"
    "Пример: <code>Здравствуйте, {buyer}!</code>\n\n"
    "<b>Если у игры несколько товаров</b> (например гемы и пропуск), соединяй слова через «+»: "
    "<code>brawl+гем</code> сработает только когда в названии есть и «brawl», и «гем». "
    "Из подходящих ответов выбирается самый точный.\n"
    "Проверить подбор: <code>/check название товара</code>.\n\n"
    "Выбери товар, чтобы изменить ключевые слова или текст, либо добавь новый."
)


@dp.message(F.text == MENU_PRODUCTS, F.func(is_admin))
@dp.message(Command("products"), F.func(is_admin))
async def cmd_products(message: Message):
    await message.answer(PRODUCTS_HELP, reply_markup=products_keyboard())


@dp.message(Command("check"), F.func(is_admin))
async def cmd_check(message: Message):
    """/check <название товара> — показывает, какой автоответ подойдёт, ничего не отправляя."""
    parts = (message.text or "").split(maxsplit=1)
    if len(parts) < 2 or not parts[1].strip():
        return await message.answer(
            "Использование: <code>/check название товара</code>\n"
            "Например: <code>/check Brawl Stars 30 гемов</code>"
        )
    name = parts[1].strip()
    matches = match_products(name)
    if not matches:
        return await message.answer(
            f"🔎 Товар: <b>{esc(name)}</b>\n❌ Ни одно ключевое слово не подошло — автоответ не отправится."
        )
    _score, idx, alias = matches[0]
    entry = PRODUCT_REPLIES[idx]
    fake_deal = SimpleNamespace(
        user=SimpleNamespace(username="Тест"),
        item=SimpleNamespace(name=name, price=100),
        id="TEST",
        seller=None,
    )
    lines = [
        f"🔎 Товар: <b>{esc(name)}</b>",
        f"✅ Сработает: <b>{esc(product_title(entry))}</b> (запись {idx + 1})",
        f"🔑 Совпало ключевое слово: <code>{esc(alias)}</code>",
    ]
    others = matches[1:]
    if others:
        lines.append("Тоже подошли, но менее точные: " + ", ".join(
            f"{esc(product_title(PRODUCT_REPLIES[i]))} (<code>{esc(a)}</code>)" for _s, i, a in others
        ))
    lines.append("\n📨 Так увидит покупатель (ник «Тест»):\n" + esc(render_reply(entry["text"], fake_deal)))
    await message.answer("\n".join(lines))


@dp.callback_query(F.data == "pr_list", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def product_list(call: CallbackQuery):
    product_state.pop(call.from_user.id, None)
    await call.answer()
    await call.message.answer(PRODUCTS_HELP, reply_markup=products_keyboard())


@dp.callback_query(F.data.startswith("pr_view:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def product_view(call: CallbackQuery):
    try:
        idx = int(call.data.split(":", 1)[1])
        entry = PRODUCT_REPLIES[idx]
    except (ValueError, IndexError):
        return await call.answer("Запись не найдена", show_alert=True)
    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="✏️ Изменить текст", callback_data=f"pr_edt:{idx}")],
        [InlineKeyboardButton(text="🔑 Изменить ключевые слова", callback_data=f"pr_edk:{idx}")],
        [InlineKeyboardButton(text="🗑 Удалить", callback_data=f"pr_del:{idx}")],
        [InlineKeyboardButton(text="⬅️ К списку товаров", callback_data="pr_list")],
    ])
    await call.answer()
    await call.message.answer(
        f"🎯 <b>{esc(product_title(entry))}</b>\n"
        f"🔑 Ключевые слова: <code>{esc(entry['keyword'])}</code>\n\n{esc(entry['text'])}",
        reply_markup=kb,
    )


@dp.callback_query(F.data == "pr_add", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def product_add_start(call: CallbackQuery):
    product_state[call.from_user.id] = {"step": "add_kw"}
    await call.answer()
    await call.message.answer(
        "Отправь ключевые слова из названия товара (через запятую), например:\n"
        "<code>standoff, стандофф</code>\n"
        "Чтобы отличить товары одной игры, соединяй слова через «+»: <code>brawl+гем, brawl+gem</code>\n\n"
        "Для отмены — /cancel."
    )


@dp.callback_query(F.data.startswith("pr_edt:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def product_edit_text_start(call: CallbackQuery):
    try:
        idx = int(call.data.split(":", 1)[1])
        current = PRODUCT_REPLIES[idx]["text"]
    except (ValueError, IndexError):
        return await call.answer("Запись не найдена", show_alert=True)
    product_state[call.from_user.id] = {"step": "edit_text", "idx": idx}
    await call.answer()
    await call.message.answer("✏️ Отправь новый текст ответа. Для отмены — /cancel.\n\nТекущий:\n" + esc(current))


@dp.callback_query(F.data.startswith("pr_edk:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def product_edit_kw_start(call: CallbackQuery):
    try:
        idx = int(call.data.split(":", 1)[1])
        current = PRODUCT_REPLIES[idx]["keyword"]
    except (ValueError, IndexError):
        return await call.answer("Запись не найдена", show_alert=True)
    product_state[call.from_user.id] = {"step": "edit_kw", "idx": idx}
    await call.answer()
    await call.message.answer(
        "🔑 Отправь новые ключевые слова через запятую. Для отмены — /cancel.\n\nТекущие: "
        f"<code>{esc(current)}</code>"
    )


@dp.callback_query(F.data.startswith("pr_del:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def product_delete(call: CallbackQuery):
    try:
        idx = int(call.data.split(":", 1)[1])
        removed = PRODUCT_REPLIES.pop(idx)
    except (ValueError, IndexError):
        return await call.answer("Запись не найдена", show_alert=True)
    try:
        save_product_replies()
    except Exception as e:
        PRODUCT_REPLIES.insert(idx, removed)
        log.exception("delete product reply")
        return await call.answer(f"Не удалось сохранить: {e}", show_alert=True)
    await call.answer("Удалено")
    await call.message.edit_text(f"🗑 Удалён ответ для: {esc(product_title(removed))}", reply_markup=products_keyboard())


@dp.message(F.text, F.func(lambda m: m.from_user is not None and m.from_user.id == ADMIN_ID and m.from_user.id in product_state and not _is_listing_text_stage(m.from_user.id)))
async def product_input_text(message: Message):
    user_id = message.from_user.id
    state = product_state[user_id]
    value = message.text.strip()
    if not value:
        return await message.answer("Пустое значение не сохранено. Отправь текст ещё раз или /cancel.")
    step = state["step"]

    if step == "add_kw":
        state.update(step="add_text", keyword=value)
        return await message.answer(
            "Теперь отправь текст автоответа для этого товара.\n"
            "Можно использовать {buyer} (ник покупателя), {seller} (ваш ник), {item}, {price}, {deal_id}."
        )

    try:
        if step == "add_text":
            PRODUCT_REPLIES.append({"keyword": state["keyword"], "text": value})
            try:
                save_product_replies()
            except Exception:
                PRODUCT_REPLIES.pop()
                raise
            done = "✅ Ответ для товара добавлен."
        else:
            idx = state["idx"]
            if not 0 <= idx < len(PRODUCT_REPLIES):
                product_state.pop(user_id, None)
                return await message.answer("Эта запись уже удалена. Открой меню ещё раз.")
            field = "text" if step == "edit_text" else "keyword"
            old = PRODUCT_REPLIES[idx][field]
            PRODUCT_REPLIES[idx][field] = value
            try:
                save_product_replies()
            except Exception:
                PRODUCT_REPLIES[idx][field] = old
                raise
            done = "✅ Текст ответа изменён." if field == "text" else "✅ Ключевые слова изменены."
    except Exception as e:
        log.exception("save product reply")
        return await message.answer(f"Не удалось сохранить: {esc(e)}")

    product_state.pop(user_id, None)
    await message.answer(done, reply_markup=products_keyboard())


@dp.callback_query(F.data.startswith("pr_send:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def product_send(call: CallbackQuery):
    try:
        _, idx_raw, chat_id = call.data.split(":", 2)
        template = PRODUCT_REPLIES[int(idx_raw)]["text"]
        if acc is None:
            return await call.answer("PlayerOK ещё не подключён", show_alert=True)
        # Раньше уходил «сырой» шаблон с буквальным {buyer}/{seller}; теперь подстановки заполняются.
        text = render_reply(template, deal_by_chat.get(chat_id) or SimpleNamespace())
        await asyncio.to_thread(acc.send_message, chat_id=chat_id, text=text)
        await call.answer("Отправлено ✅")
        await call.message.reply(f"✅ Отправлено покупателю:\n{esc(text)}")
    except (ValueError, IndexError):
        await call.answer("Этот ответ уже недоступен", show_alert=True)
    except Exception as e:
        log.exception("product_send")
        await call.answer("Ошибка отправки", show_alert=True)
        await call.message.reply(f"Не отправилось: {esc(e)}")


# ---------- Настоящие товары PlayerOK: создание, публикация и управление ----------
# В отличие от PRODUCT_REPLIES, этот раздел работает с объявлениями PlayerOK.
# Для создания используется create_item (черновик), затем отдельное подтверждение publish_item.
listing_state: dict[int, dict] = {}
listing_items_cache: dict[int, list] = {}
listing_pages: dict[int, dict] = {}
listing_search_state: set[int] = set()
listing_search_cache: dict[int, list] = {}
listing_search_pages: dict[int, int] = {}


def _is_listing_text_stage(user_id: int) -> bool:
    """True while the admin is entering data in the listing wizard.

    This guard prevents older input modes (search, autoreplies, review text, etc.)
    from consuming a message that belongs to the product creation flow.
    """
    state = listing_state.get(user_id) or {}
    mode, step = state.get("mode"), state.get("step")
    return (
        (mode == "create" and step in {"game", "data", "name", "description", "price", "photos"})
        or (mode == "edit" and step in {"name", "description", "price"})
        or (mode == "edit_data" and step == "data")
        or mode == "edit_photo"
    )


def _clear_other_text_modes(user_id: int) -> None:
    """Give the listing wizard exclusive ownership of the next text message."""
    autoreply_input_state.discard(user_id)
    autoreply_edit_state.pop(user_id, None)
    product_state.pop(user_id, None)
    listing_search_state.discard(user_id)
    preset_name_state.pop(user_id, None)
    review_state.discard(user_id)
    pending_chat_message.pop(user_id, None)
    draft_chat_message.pop(user_id, None)
listing_favorite_cache: dict[int, list] = {}
listing_favorite_pages: dict[int, int] = {}
preset_name_state: dict[int, dict] = {}

FAVORITES_FILE = os.getenv("FAVORITES_FILE", "favorites.json")
SALES_PRESETS_FILE = os.getenv("SALES_PRESETS_FILE", "sales_presets.json")
ITEMS_SEARCH_MAX_PAGES = max(1, int(os.getenv("ITEMS_SEARCH_MAX_PAGES", "100")))
ITEMS_VIEW_PAGE_SIZE = 10


def _load_favorites() -> set[str]:
    try:
        with open(FAVORITES_FILE, "r", encoding="utf-8") as file:
            data = json.load(file)
        return {str(item_id) for item_id in data if item_id is not None} if isinstance(data, list) else set()
    except FileNotFoundError:
        return set()
    except Exception:
        log.exception("Не удалось загрузить избранное")
        return set()


def _load_sales_presets() -> list[dict]:
    try:
        with open(SALES_PRESETS_FILE, "r", encoding="utf-8") as file:
            data = json.load(file)
        return [item for item in data if isinstance(item, dict) and item.get("id") and item.get("name")]
    except FileNotFoundError:
        return []
    except Exception:
        log.exception("Не удалось загрузить пресеты продаж")
        return []


FAVORITE_ITEM_IDS: set[str] = _load_favorites()
SALES_PRESETS: list[dict] = _load_sales_presets()


def _save_favorites() -> None:
    _atomic_json_save(FAVORITES_FILE, sorted(FAVORITE_ITEM_IDS))


def _save_sales_presets() -> None:
    _atomic_json_save(SALES_PRESETS_FILE, SALES_PRESETS)


def _item_id(item) -> str:
    value = getattr(item, "id", None)
    return str(value) if value is not None else ""


def _field_key(field) -> str:
    for attr in ("id", "name", "label"):
        value = getattr(field, attr, None)
        if value not in (None, ""):
            return str(value)
    return ""


def _search_item_score(item, query: str) -> int:
    """Оценивает совпадение по названию, описанию, игре, категории и способу получения."""
    query = " ".join(norm_text(query).split())
    if not query:
        return 0
    parts = [
        getattr(item, "name", ""), getattr(item, "description", ""),
        getattr(getattr(item, "game", None), "name", ""),
        getattr(getattr(item, "category", None), "name", ""),
        getattr(getattr(item, "obtaining_type", None), "name", ""),
    ]
    haystack = norm_text(" ".join(str(part or "") for part in parts))
    if query in haystack:
        return 10000 - haystack.index(query)

    tokens = list(dict.fromkeys(re.findall(r"[\w-]+", query)))
    words = set(re.findall(r"[\w-]+", haystack))
    matched = 0
    score = 0
    for token in tokens:
        if token in haystack:
            matched += 1
            score += 100 + min(len(token), 20)
        elif any(word.startswith(token) or token.startswith(word) for word in words if min(len(word), len(token)) >= 3):
            matched += 1
            score += 65 + min(len(token), 20)
    if tokens and matched == len(tokens):
        return 5000 + score
    return 0


async def _search_all_my_items(query: str) -> tuple[list, bool]:
    """Ищет по всем доступным страницам объявлений; ограничение защищает от долгого обхода."""
    kwargs = {"count": 24}
    statuses = _item_statuses_for_query()
    if statuses is not None:
        kwargs["statuses"] = statuses
    cursor, seen_cursors, seen_ids = None, set(), set()
    matches = []
    truncated = False
    for _ in range(ITEMS_SEARCH_MAX_PAGES):
        try:
            result = await asyncio.to_thread(acc.get_my_items, after_cursor=cursor, **kwargs)
        except TypeError:
            # Совместимость с версиями playerokapi, где фильтр statuses отсутствует.
            kwargs.pop("statuses", None)
            result = await asyncio.to_thread(acc.get_my_items, after_cursor=cursor, **kwargs)
        items = list(getattr(result, "items", []) or [])
        for item in items:
            item_id = _item_id(item)
            if item_id and item_id not in seen_ids:
                seen_ids.add(item_id)
                score = _search_item_score(item, query)
                if score:
                    matches.append((score, item))
        page_info = getattr(result, "page_info", None)
        if not getattr(page_info, "has_next_page", False):
            break
        next_cursor = getattr(page_info, "end_cursor", None)
        if not next_cursor or next_cursor in seen_cursors:
            break
        seen_cursors.add(next_cursor)
        cursor = next_cursor
    else:
        truncated = True
    matches.sort(key=lambda pair: pair[0], reverse=True)
    return [item for _score, item in matches], truncated


async def show_item_search_results(
    target, user_id: int, *, index: int = 0, notice: str | None = None, edit_message: bool = False,
):
    items = listing_search_cache.get(user_id, [])
    total_pages = max(1, (len(items) + ITEMS_VIEW_PAGE_SIZE - 1) // ITEMS_VIEW_PAGE_SIZE)
    index = min(max(0, index), total_pages - 1)
    listing_search_pages[user_id] = index
    start = index * ITEMS_VIEW_PAGE_SIZE
    page_items = items[start:start + ITEMS_VIEW_PAGE_SIZE]
    rows = []
    for i, item in enumerate(page_items, start=start):
        name = str(getattr(item, "name", None) or "Без названия").replace("\n", " ")
        price = getattr(item, "price", "?")
        mark = "⭐ " if _item_id(item) in FAVORITE_ITEM_IDS else ""
        rows.append([InlineKeyboardButton(text=f"{mark}{i + 1}. {name} · {price} ₽"[:60], callback_data=f"itm_sopen:{i}")])
    nav = []
    if index > 0:
        nav.append(InlineKeyboardButton(text="⬅️ Назад", callback_data="itm_sprev"))
    if index + 1 < total_pages:
        nav.append(InlineKeyboardButton(text="Вперёд ➡️", callback_data="itm_snext"))
    if nav:
        rows.append(nav)
    rows.append([InlineKeyboardButton(text="🔎 Новый поиск", callback_data="itm_search"), InlineKeyboardButton(text="🛍 Все товары", callback_data="itm_list")])
    title = f"🔎 <b>Результаты поиска</b> · {len(items)}"
    if notice:
        title += f"\n{esc(notice)}"
    title += f"\nСтраница {index + 1}/{total_pages}."
    if not items:
        title += "\nСовпадений не найдено. Попробуй другое название или часть описания."
    sender = target.edit_text if edit_message else target.answer
    await sender(title, reply_markup=InlineKeyboardMarkup(inline_keyboard=rows))


async def show_favorite_items(
    target, user_id: int, *, index: int = 0, refresh: bool = True, edit_message: bool = False,
):
    if acc is None:
        return await target.answer("PlayerOK ещё подключается. Попробуй чуть позже.")
    if refresh or user_id not in listing_favorite_cache:
        ids = list(FAVORITE_ITEM_IDS)
        semaphore = asyncio.Semaphore(4)

        async def fetch(item_id: str):
            async with semaphore:
                try:
                    return await asyncio.to_thread(acc.get_item, id=item_id)
                except Exception:
                    log.info("Не удалось загрузить избранное объявление %s", item_id, exc_info=True)
                    return None

        loaded = await asyncio.gather(*(fetch(item_id) for item_id in ids))
        listing_favorite_cache[user_id] = sorted(
            (item for item in loaded if item is not None),
            key=lambda item: str(getattr(item, "name", "")).casefold(),
        )
    items = listing_favorite_cache.get(user_id, [])
    total_pages = max(1, (len(items) + ITEMS_VIEW_PAGE_SIZE - 1) // ITEMS_VIEW_PAGE_SIZE)
    index = min(max(0, index), total_pages - 1)
    listing_favorite_pages[user_id] = index
    start = index * ITEMS_VIEW_PAGE_SIZE
    rows = []
    for i, item in enumerate(items[start:start + ITEMS_VIEW_PAGE_SIZE], start=start):
        name = str(getattr(item, "name", None) or "Без названия").replace("\n", " ")
        price = getattr(item, "price", "?")
        rows.append([InlineKeyboardButton(text=f"⭐ {name} · {price} ₽"[:60], callback_data=f"itm_fopen:{i}")])
    nav = []
    if index > 0:
        nav.append(InlineKeyboardButton(text="⬅️ Назад", callback_data="itm_fprev"))
    if index + 1 < total_pages:
        nav.append(InlineKeyboardButton(text="Вперёд ➡️", callback_data="itm_fnext"))
    if nav:
        rows.append(nav)
    rows.append([InlineKeyboardButton(text="🔄 Обновить", callback_data="itm_frefresh"), InlineKeyboardButton(text="🛍 Все товары", callback_data="itm_list")])
    title = f"⭐ <b>Избранные товары</b> · {len(items)} · страница {index + 1}/{total_pages}"
    if not items:
        title += "\nПока нет избранных объявлений. Открой товар и нажми «В избранное» на его карточке."
    sender = target.edit_text if edit_message else target.answer
    await sender(title, reply_markup=InlineKeyboardMarkup(inline_keyboard=rows))


def _enum_name(value) -> str:
    return str(getattr(value, "name", value) or "").split(".")[-1].upper()


def _field_is_item_data(field) -> bool:
    kind = _enum_name(getattr(field, "type", None))
    return kind == "ITEM_DATA" or kind == "0"


def _status_name(item) -> str:
    status = _enum_name(getattr(item, "status", None))
    return {
        "DRAFT": "Черновик",
        "PENDING_APPROVAL": "На модерации",
        "PENDING_MODERATION": "Проверка изменений",
        "APPROVED": "Активен",
        "DECLINED": "Отклонён",
        "BLOCKED": "Заблокирован",
        "EXPIRED": "Истёк",
        "SOLD": "Продан",
    }.get(status, status or "Статус неизвестен")


def _listing_url(item) -> str | None:
    slug = getattr(item, "slug", None)
    return f"https://playerok.com/products/{slug}" if slug else None


def _item_keyboard(item) -> InlineKeyboardMarkup:
    rows = []
    editable = getattr(item, "is_editable", True)
    if editable is not False:
        rows.extend([
            [InlineKeyboardButton(text="✏️ Изменить название", callback_data="itm_ed:name")],
            [InlineKeyboardButton(text="💰 Изменить цену", callback_data="itm_ed:price")],
            [InlineKeyboardButton(text="📝 Изменить описание", callback_data="itm_ed:description")],
            [InlineKeyboardButton(text="🧩 Изменить характеристики", callback_data="itm_ed:options")],
            [InlineKeyboardButton(text="🔐 Изменить данные товара", callback_data="itm_ed:data")],
            [InlineKeyboardButton(text="🖼 Добавить фото", callback_data="itm_ed:photo")],
        ])
        for i, attachment in enumerate(getattr(item, "attachments", []) or []):
            rows.append([InlineKeyboardButton(text=f"🗑 Удалить фото {i + 1}", callback_data=f"itm_rm_photo:{i}")])
    if _enum_name(getattr(item, "status", None)) == "DRAFT":
        rows.append([InlineKeyboardButton(text="🚀 Опубликовать", callback_data="itm_publish")])
    item_id = _item_id(item)
    if item_id:
        favorite_label = "💔 Убрать из избранного" if item_id in FAVORITE_ITEM_IDS else "⭐ В избранное"
        rows.append([InlineKeyboardButton(text=favorite_label, callback_data=f"itm_fav:{item_id}")])
    rows.extend([
        [InlineKeyboardButton(text="🗑 Удалить объявление", callback_data="itm_delete")],
        [InlineKeyboardButton(text="⬅️ К моим товарам", callback_data="itm_list")],
    ])
    return InlineKeyboardMarkup(inline_keyboard=rows)


def _item_statuses_for_query():
    return list(ItemStatuses) if ItemStatuses is not None else None


async def show_my_items(target, user_id: int, *, notice: str | None = None, reset: bool = True):
    if acc is None:
        return await target.answer("PlayerOK ещё подключается. Попробуй чуть позже.")
    try:
        if reset or user_id not in listing_pages:
            listing_pages[user_id] = {"cursors": [None], "index": 0, "page_info": None, "total_count": 0}
        page_state = listing_pages[user_id]
        cursor = page_state["cursors"][page_state["index"]]
        statuses = _item_statuses_for_query()
        kwargs = {"count": 24, "after_cursor": cursor}
        if statuses is not None:
            kwargs["statuses"] = statuses
        result = await asyncio.to_thread(acc.get_my_items, **kwargs)
        items = list(getattr(result, "items", []) or [])
        page_info = getattr(result, "page_info", None)
        page_state["page_info"] = page_info
        page_state["total_count"] = getattr(result, "total_count", len(items))
        if getattr(page_info, "has_next_page", False):
            next_cursor = getattr(page_info, "end_cursor", None)
            if next_cursor and page_state["index"] == len(page_state["cursors"]) - 1:
                page_state["cursors"].append(next_cursor)
        listing_items_cache[user_id] = items
        listing_state.pop(user_id, None)
        rows = []
        for i, item in enumerate(items[:24]):
            name = str(getattr(item, "name", None) or "Без названия").replace("\n", " ")
            if len(name) > 38:
                name = name[:35] + "…"
            price = getattr(item, "price", "?")
            rows.append([InlineKeyboardButton(
                text=f"{i + 1}. {name} · {price} ₽ · {_status_name(item)}"[:60],
                callback_data=f"itm_open:{i}",
            )])
        nav = []
        if page_state["index"] > 0:
            nav.append(InlineKeyboardButton(text="⬅️ Назад", callback_data="itm_prev"))
        if getattr(page_info, "has_next_page", False):
            nav.append(InlineKeyboardButton(text="Вперёд ➡️", callback_data="itm_next"))
        if nav:
            rows.append(nav)
        rows.append([InlineKeyboardButton(text="➕ Создать объявление", callback_data="itm_new")])
        rows.append([
            InlineKeyboardButton(text="🔎 Поиск", callback_data="itm_search"),
            InlineKeyboardButton(text="⭐ Избранное", callback_data="itm_favorites"),
        ])
        rows.append([InlineKeyboardButton(text="📋 Пресеты продаж", callback_data="itm_presets")])
        rows.append([InlineKeyboardButton(text="🔄 Обновить список", callback_data="itm_list")])
        text = "🛍 <b>Мои объявления PlayerOK</b>\n"
        if notice:
            text += f"{esc(notice)}\n\n"
        text += f"Всего: {page_state['total_count']} · страница {page_state['index'] + 1}.\nВыбери объявление или создай новое."
        if not items:
            text += "\n\nОбъявлений пока нет."
        await target.answer(text, reply_markup=InlineKeyboardMarkup(inline_keyboard=rows))
    except Exception as e:
        log.exception("get_my_items")
        await target.answer(f"Не удалось загрузить товары PlayerOK: {esc(e)}")


def _preset_snapshot(state: dict) -> dict:
    game, category, obtaining = state.get("game"), state.get("category"), state.get("obtaining")
    if not all(getattr(obj, "id", None) for obj in (game, category, obtaining)):
        raise ValueError("Не хватает данных игры, категории или способа получения")
    fields = []
    for field in state.get("fields", []):
        key = _field_key(field)
        value = getattr(field, "value", None)
        if key and value is not None:
            fields.append({
                "key": key,
                "label": str(getattr(field, "label", "") or ""),
                "value": str(value),
            })
    return {
        "game_id": str(game.id), "game_name": str(getattr(game, "name", "")),
        "category_id": str(category.id), "category_name": str(getattr(category, "name", "")),
        "obtaining_id": str(obtaining.id), "obtaining_name": str(getattr(obtaining, "name", "")),
        "name": str(state.get("name", "")), "description": str(state.get("description", "")),
        "price": int(state.get("price", 0)),
        "option_ids": [str(option.id) for option in state.get("selected_options", []) if getattr(option, "id", None)],
        "skipped_option_groups": sorted(state.get("skipped_option_groups", set())),
        "fields": fields,
    }


@dp.callback_query(F.data == "itm_preset_save_current", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_preset_save_current(call: CallbackQuery):
    state = listing_state.get(call.from_user.id, {})
    if state.get("mode") != "create" or state.get("step") != "create_preview":
        return await call.answer("Сначала подготовь объявление в мастере создания", show_alert=True)
    try:
        snapshot = _preset_snapshot(state)
        preset_name_state[call.from_user.id] = snapshot
        await call.answer()
        await call.message.answer("📋 Отправь название пресета, например «Brawl Stars — гемы». Для отмены — /cancel.")
    except Exception as e:
        await call.answer("Не удалось подготовить пресет", show_alert=True)
        await call.message.answer(f"Ошибка: {esc(e)}")


@dp.message(F.text, F.func(lambda m: m.from_user is not None and m.from_user.id == ADMIN_ID and m.from_user.id in preset_name_state
    and (listing_state.get(m.from_user.id, {}).get("mode"), listing_state.get(m.from_user.id, {}).get("step")) == ("create", "create_preview")))
async def item_preset_name_input(message: Message):
    user_id = message.from_user.id
    name = (message.text or "").strip()
    if not name:
        return await message.answer("Название не может быть пустым. Отправь другое или /cancel.")
    if name.startswith("/"):
        return await message.answer("Отправь название пресета или используй /cancel.")
    snapshot = preset_name_state.pop(user_id)
    snapshot.update(id=str(time.time_ns()), name=name[:60], created_at=datetime.now(timezone.utc).isoformat())
    SALES_PRESETS.append(snapshot)
    try:
        _save_sales_presets()
    except Exception as e:
        SALES_PRESETS.pop()
        preset_name_state[user_id] = snapshot
        log.exception("save sales preset")
        return await message.answer(f"Не удалось сохранить пресет: {esc(e)}")
    await message.answer(
        f"✅ Пресет «{esc(snapshot['name'])}» сохранён. Фото в шаблон не входят — их можно добавить при следующем создании.",
        reply_markup=InlineKeyboardMarkup(inline_keyboard=[
            [InlineKeyboardButton(text="📋 Все пресеты", callback_data="itm_presets")],
            [InlineKeyboardButton(text="✅ Продолжить создание", callback_data="itm_create_confirm")],
        ]),
    )


async def show_sales_presets(target, user_id: int):
    rows = []
    for preset in SALES_PRESETS[:40]:
        preset_id = str(preset.get("id", ""))
        name = str(preset.get("name", "Без названия"))
        price = preset.get("price", "?")
        rows.append([
            InlineKeyboardButton(text=f"📋 {name} · {price} ₽"[:48], callback_data=f"itm_preset:{preset_id}"),
            InlineKeyboardButton(text="🗑", callback_data=f"itm_preset_del:{preset_id}"),
        ])
    rows.append([InlineKeyboardButton(text="🛍 К товарам", callback_data="itm_list")])
    text = f"📋 <b>Пресеты продаж</b> · {len(SALES_PRESETS)}\nВыбери шаблон: бот подставит сохранённые параметры, а перед созданием покажет новый предпросмотр."
    if not SALES_PRESETS:
        text += "\n\nПресетов пока нет. Подготовь объявление до предпросмотра и нажми «Сохранить как пресет»."
    await target.answer(text, reply_markup=InlineKeyboardMarkup(inline_keyboard=rows))


@dp.callback_query(F.data == "itm_presets", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_presets_callback(call: CallbackQuery):
    await call.answer()
    await show_sales_presets(call.message, call.from_user.id)


@dp.message(F.text == MENU_PRESETS, F.func(is_admin))
@dp.message(Command("presets"), F.func(is_admin))
async def cmd_sales_presets(message: Message):
    await show_sales_presets(message, message.from_user.id)


@dp.callback_query(F.data.startswith("itm_preset_del:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_preset_delete(call: CallbackQuery):
    preset_id = call.data.split(":", 1)[1]
    index = next((i for i, p in enumerate(SALES_PRESETS) if str(p.get("id")) == preset_id), None)
    if index is None:
        return await call.answer("Пресет не найден", show_alert=True)
    removed = SALES_PRESETS.pop(index)
    try:
        _save_sales_presets()
    except Exception as e:
        SALES_PRESETS.insert(index, removed)
        log.exception("delete sales preset")
        return await call.answer(f"Не удалось удалить пресет: {e}", show_alert=True)
    await call.answer("Пресет удалён")
    await call.message.edit_reply_markup(reply_markup=None)
    await show_sales_presets(call.message, call.from_user.id)


@dp.callback_query(F.data.startswith("itm_preset:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_preset_apply(call: CallbackQuery):
    preset_id = call.data.split(":", 1)[1]
    preset = next((p for p in SALES_PRESETS if str(p.get("id")) == preset_id), None)
    if preset is None:
        return await call.answer("Пресет не найден", show_alert=True)
    if acc is None:
        return await call.answer("PlayerOK ещё не подключён", show_alert=True)
    try:
        _clear_other_text_modes(call.from_user.id)
        game = await asyncio.to_thread(acc.get_game, id=preset["game_id"])
        category = await asyncio.to_thread(acc.get_game_category, id=preset["category_id"])
        obtaining_result = await asyncio.to_thread(acc.get_game_category_obtaining_types, category.id)
        obtaining_types = list(getattr(obtaining_result, "obtaining_types", []) or [])
        obtaining = next((x for x in obtaining_types if str(getattr(x, "id", "")) == str(preset["obtaining_id"])), None)
        if obtaining is None:
            return await call.answer("Способ получения из пресета больше недоступен. Создай объявление заново.", show_alert=True)

        option_map = {
            str(getattr(option, "id", "")): option
            for _group, options in _option_groups(getattr(category, "options", None))
            for option in options
            if getattr(option, "id", None)
        }
        selected_options = [option_map[x] for x in preset.get("option_ids", []) if x in option_map]
        missing_options = len(preset.get("option_ids", [])) - len(selected_options)
        listing_state[call.from_user.id] = {
            "mode": "create", "step": "options", "game": game, "category": category,
            "obtaining": obtaining, "name": preset.get("name", ""),
            "description": preset.get("description", ""), "price": int(preset.get("price", 0) or 0),
            "preset_loaded": True, "preset_field_values": {
                str(entry.get("key", "")): entry.get("value", "")
                for entry in preset.get("fields", []) if isinstance(entry, dict)
            } | {
                str(entry.get("label")): entry.get("value", "")
                for entry in preset.get("fields", []) if isinstance(entry, dict) and entry.get("label")
            },
        }
        await call.answer("Пресет загружен")
        if missing_options:
            await call.message.answer(f"⚠️ {missing_options} характеристик из пресета больше нет. Проверь доступные варианты.")
        await _begin_option_selection(
            call.message, call.from_user.id, getattr(category, "options", None),
            selected_options=selected_options,
            skipped_groups=preset.get("skipped_option_groups", []),
        )
    except Exception as e:
        log.exception("apply sales preset")
        await call.answer("Не удалось загрузить пресет", show_alert=True)
        await call.message.answer(f"Ошибка загрузки пресета: {esc(e)}")


@dp.message(F.text == MENU_ITEMS, F.func(is_admin))
@dp.message(Command("items"), F.func(is_admin))
async def cmd_my_items(message: Message):
    await show_my_items(message, message.from_user.id)


@dp.message(F.text == MENU_FAVORITES, F.func(is_admin))
@dp.message(Command("favorites"), F.func(is_admin))
async def cmd_favorites(message: Message):
    await show_favorite_items(message, message.from_user.id, index=0, refresh=True)


@dp.message(Command("newitem"), F.func(is_admin))
async def cmd_new_item(message: Message):
    if acc is None:
        return await message.answer("PlayerOK ещё подключается. Попробуй чуть позже.")
    _clear_other_text_modes(message.from_user.id)
    listing_state[message.from_user.id] = {"mode": "create", "step": "game"}
    await message.answer("🎮 Напиши название игры или приложения на PlayerOK. Например: Standoff 2, Telegram. Для отмены — /cancel.")


@dp.callback_query(F.data == "itm_list", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def items_list_callback(call: CallbackQuery):
    await call.answer()
    await show_my_items(call.message, call.from_user.id)


@dp.callback_query(F.data == "itm_next", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def items_next_page(call: CallbackQuery):
    page = listing_pages.get(call.from_user.id)
    if not page or page["index"] + 1 >= len(page["cursors"]):
        return await call.answer("Следующей страницы нет")
    page["index"] += 1
    await call.answer()
    await show_my_items(call.message, call.from_user.id, reset=False)


@dp.callback_query(F.data == "itm_prev", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def items_prev_page(call: CallbackQuery):
    page = listing_pages.get(call.from_user.id)
    if not page or page["index"] <= 0:
        return await call.answer("Это первая страница")
    page["index"] -= 1
    await call.answer()
    await show_my_items(call.message, call.from_user.id, reset=False)


@dp.callback_query(F.data == "itm_new", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_new_callback(call: CallbackQuery):
    if acc is None:
        return await call.answer("PlayerOK ещё не подключён", show_alert=True)
    _clear_other_text_modes(call.from_user.id)
    listing_state[call.from_user.id] = {"mode": "create", "step": "game"}
    await call.answer()
    await call.message.answer("🎮 Напиши название игры или приложения на PlayerOK. Например: Standoff 2, Telegram. Для отмены — /cancel.")


async def _send_item_details(call: CallbackQuery, item_id: str):
    if acc is None:
        return await call.answer("PlayerOK ещё не подключён", show_alert=True)
    try:
        item = await asyncio.to_thread(acc.get_item, id=item_id)
        listing_state[call.from_user.id] = {"mode": "details", "item_id": item_id, "item": item}
        link = _listing_url(item)
        text = (
            f"📦 <b>{esc(getattr(item, 'name', 'Без названия'))}</b>\n"
            f"💰 Цена: <b>{esc(getattr(item, 'price', '?'))} ₽</b>\n"
            f"📌 Статус: <b>{esc(_status_name(item))}</b>\n"
            f"🎮 Игра: {esc(getattr(getattr(item, 'game', None), 'name', '—'))}\n"
            f"📂 Категория: {esc(getattr(getattr(item, 'category', None), 'name', '—'))}\n\n"
            f"{esc(getattr(item, 'description', '') or 'Описание отсутствует')}"
        )
        if link:
            text += f"\n\n🔗 <a href=\"{esc(link)}\">Открыть объявление</a>"
        await call.answer()
        await call.message.answer(text, reply_markup=_item_keyboard(item))
    except Exception as e:
        log.exception("open PlayerOK item")
        await call.answer("Не удалось открыть объявление", show_alert=True)
        await call.message.answer(f"Ошибка загрузки товара: {esc(e)}")


@dp.callback_query(F.data.startswith("itm_open:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_open_callback(call: CallbackQuery):
    try:
        idx = int(call.data.split(":", 1)[1])
        item = listing_items_cache.get(call.from_user.id, [])[idx]
        await _send_item_details(call, _item_id(item))
    except (ValueError, IndexError):
        await call.answer("Объявление не найдено", show_alert=True)


@dp.callback_query(F.data == "itm_search", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_search_start(call: CallbackQuery):
    listing_search_state.add(call.from_user.id)
    await call.answer()
    await call.message.answer("🔎 Напиши название, часть названия, игру или слово из описания товара. Для отмены — /cancel.")


async def _run_item_search(message: Message, query: str):
    if acc is None:
        return await message.answer("PlayerOK ещё подключается. Попробуй чуть позже.")
    wait = await message.answer("🔎 Ищу по объявлениям PlayerOK…")
    try:
        items, truncated = await _search_all_my_items(query)
        listing_search_cache[message.from_user.id] = items
        listing_search_pages[message.from_user.id] = 0
        notice = f"Запрос: {esc(query)}" + (
            f"\nПроверены первые {ITEMS_SEARCH_MAX_PAGES * 24} объявлений." if truncated else ""
        )
        await show_item_search_results(wait, message.from_user.id, index=0, notice=notice, edit_message=True)
    except Exception as e:
        log.exception("search PlayerOK items")
        await wait.edit_text(f"Не удалось выполнить поиск: {esc(e)}")


@dp.message(Command("itemsearch"), F.func(is_admin))
async def cmd_item_search(message: Message):
    parts = (message.text or "").split(maxsplit=1)
    if len(parts) < 2 or not parts[1].strip():
        listing_search_state.add(message.from_user.id)
        return await message.answer("Напиши название или часть описания товара для поиска. Для отмены — /cancel.")
    await _run_item_search(message, parts[1].strip())


@dp.message(F.text, F.func(lambda m: m.from_user is not None and m.from_user.id == ADMIN_ID and m.from_user.id in listing_search_state and not _is_listing_text_stage(m.from_user.id)))
async def item_search_input(message: Message):
    listing_search_state.discard(message.from_user.id)
    query = (message.text or "").strip()
    if not query:
        return await message.answer("Пустой запрос. Начни поиск заново или /cancel.")
    await _run_item_search(message, query)


@dp.callback_query(F.data.startswith("itm_sopen:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_search_open(call: CallbackQuery):
    try:
        item = listing_search_cache.get(call.from_user.id, [])[int(call.data.split(":", 1)[1])]
        await _send_item_details(call, _item_id(item))
    except (ValueError, IndexError):
        await call.answer("Объявление не найдено", show_alert=True)


@dp.callback_query(F.data.in_({"itm_snext", "itm_sprev"}), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_search_navigation(call: CallbackQuery):
    index = listing_search_pages.get(call.from_user.id, 0)
    index += 1 if call.data == "itm_snext" else -1
    await call.answer()
    await show_item_search_results(call.message, call.from_user.id, index=index, edit_message=True)


@dp.callback_query(F.data == "itm_favorites", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_favorites_callback(call: CallbackQuery):
    await call.answer()
    await show_favorite_items(call.message, call.from_user.id, index=0, refresh=True, edit_message=True)


@dp.callback_query(F.data.in_({"itm_fnext", "itm_fprev", "itm_frefresh"}), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_favorites_navigation(call: CallbackQuery):
    index = listing_favorite_pages.get(call.from_user.id, 0)
    refresh = call.data == "itm_frefresh"
    if call.data == "itm_fnext":
        index += 1
    elif call.data == "itm_fprev":
        index -= 1
    await call.answer()
    await show_favorite_items(call.message, call.from_user.id, index=index, refresh=refresh, edit_message=True)


@dp.callback_query(F.data.startswith("itm_fopen:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_favorite_open(call: CallbackQuery):
    try:
        item = listing_favorite_cache.get(call.from_user.id, [])[int(call.data.split(":", 1)[1])]
        await _send_item_details(call, _item_id(item))
    except (ValueError, IndexError):
        await call.answer("Объявление не найдено", show_alert=True)


@dp.callback_query(F.data.startswith("itm_fav:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_favorite_toggle(call: CallbackQuery):
    item_id = call.data.split(":", 1)[1]
    was_favorite = item_id in FAVORITE_ITEM_IDS
    if was_favorite:
        FAVORITE_ITEM_IDS.remove(item_id)
    else:
        FAVORITE_ITEM_IDS.add(item_id)
    try:
        _save_favorites()
    except Exception as e:
        if was_favorite:
            FAVORITE_ITEM_IDS.add(item_id)
        else:
            FAVORITE_ITEM_IDS.discard(item_id)
        log.exception("save favorite item")
        return await call.answer(f"Не удалось сохранить избранное: {e}", show_alert=True)
    listing_favorite_cache.pop(call.from_user.id, None)
    await call.answer("Убрано из избранного" if was_favorite else "Добавлено в избранное ⭐")
    try:
        item = listing_state.get(call.from_user.id, {}).get("item")
        if item is not None and _item_id(item) == item_id:
            await call.message.edit_reply_markup(reply_markup=_item_keyboard(item))
    except Exception:
        log.debug("Не удалось обновить клавиатуру карточки товара", exc_info=True)


# --- Мастер создания ---
def _option_groups(options) -> list[tuple[str, list]]:
    grouped: OrderedDict[str, list] = OrderedDict()
    for option in options or []:
        group_name = str(getattr(option, "group", None) or "Характеристики")
        grouped.setdefault(group_name, []).append(option)
    return list(grouped.items())


async def _begin_create_fields(user_id: int, message: Message):
    state = listing_state[user_id]
    try:
        result = await asyncio.to_thread(
            acc.get_game_category_data_fields,
            state["category"].id,
            state["obtaining"].id,
        )
        fields = [f for f in (getattr(result, "data_fields", []) or []) if _field_is_item_data(f)]
        state["step"] = "data"
        state["fields"] = fields
        state["field_index"] = 0
        saved_values = state.get("preset_field_values", {})
        prefilled = set()
        for field in fields:
            key = _field_key(field)
            label = str(getattr(field, "label", "") or "")
            if key in saved_values or label in saved_values:
                field.value = saved_values.get(key, saved_values.get(label, ""))
                prefilled.add(key or label)
        state["prefilled_field_keys"] = prefilled
        await _ask_listing_data_field(message, user_id)
    except Exception as e:
        log.exception("load category data fields")
        await message.answer(f"Не удалось получить поля выбранной категории: {esc(e)}\nМожно отменить создание командой /cancel.")


async def _ask_listing_data_field(message: Message, user_id: int):
    state = listing_state[user_id]
    fields = state.get("fields", [])
    index = state.get("field_index", 0)
    prefilled = state.get("prefilled_field_keys", set())
    while index < len(fields) and (_field_key(fields[index]) in prefilled or str(getattr(fields[index], "label", "") or "") in prefilled):
        index += 1
    state["field_index"] = index
    if index >= len(fields):
        if state.get("preset_loaded"):
            state["step"] = "photos"
            state["attachments"] = []
            return await message.answer("🖼 Параметры пресета заполнены. Добавь фотографии (до 10), затем /done. Без фото — /skip.")
        state["step"] = "name"
        return await message.answer("📝 Введи название объявления.")
    field = fields[index]
    required = bool(getattr(field, "required", False))
    suffix = "Обязательное поле." if required else "Необязательное поле — для пропуска отправь /skip."
    await message.answer(
        f"🔐 Поле {index + 1}/{len(fields)}: <b>{esc(getattr(field, 'label', 'Данные'))}</b>\n{suffix}\n"
        "Отправь значение следующим сообщением."
    )


async def _show_option_group(message: Message, user_id: int):
    state = listing_state[user_id]
    groups = state.get("option_groups", [])
    index = state.get("option_index", 0)
    if index >= len(groups):
        if state["mode"] == "create":
            return await _begin_create_fields(user_id, message)
        return await message.answer(
            "Характеристики выбраны. Сохранить изменения?",
            reply_markup=InlineKeyboardMarkup(inline_keyboard=[
                [InlineKeyboardButton(text="✅ Сохранить характеристики", callback_data="itm_opt_save")],
                [InlineKeyboardButton(text="❌ Отмена", callback_data="itm_opt_cancel")],
            ]),
        )
    group_name, options = groups[index]
    selected_ids = {str(getattr(option, "id", "")) for option in state.get("selected_options", [])}
    group_ids = {str(getattr(option, "id", "")) for option in options}
    if group_name in state.get("skipped_option_groups", set()) or selected_ids.intersection(group_ids):
        state["option_index"] = index + 1
        return await _show_option_group(message, user_id)
    state["current_options"] = options
    rows = []
    for i, option in enumerate(options[:40]):
        label = str(getattr(option, "label", None) or getattr(option, "value", "Вариант"))
        rows.append([InlineKeyboardButton(text=label[:60], callback_data=f"itm_opt:{i}")])
    rows.append([InlineKeyboardButton(text="⏭ Пропустить группу", callback_data="itm_opt_skip")])
    await message.answer(
        f"🧩 Характеристика {index + 1}/{len(groups)}: <b>{esc(group_name)}</b>\nВыбери вариант:",
        reply_markup=InlineKeyboardMarkup(inline_keyboard=rows),
    )


async def _begin_option_selection(
    message: Message, user_id: int, options, *, selected_options=None, skipped_groups=None,
):
    groups = _option_groups(options)
    state = listing_state[user_id]
    state["option_groups"] = groups
    state["option_index"] = 0
    state["selected_options"] = list(selected_options or [])
    state["skipped_option_groups"] = set(skipped_groups or ())
    if not groups:
        if state["mode"] == "create":
            return await _begin_create_fields(user_id, message)
        item_id = state.get("item_id")
        item = state.get("item")
        listing_state[user_id] = {"mode": "details", "item_id": item_id, "item": item}
        return await message.answer(
            "У этого объявления нет настраиваемых характеристик.",
            reply_markup=_item_keyboard(item) if item is not None else None,
        )
    await _show_option_group(message, user_id)


@dp.callback_query(F.data.startswith("itm_game:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_choose_game(call: CallbackQuery):
    try:
        state = listing_state[call.from_user.id]
        game = state["games"][int(call.data.split(":", 1)[1])]
        full_game = await asyncio.to_thread(acc.get_game, id=game.id)
        categories = list(getattr(full_game, "categories", []) or [])
        if not categories:
            return await call.answer("У игры не найдены категории", show_alert=True)
        state.update(game=full_game, categories=categories, step="category")
        rows = [[InlineKeyboardButton(text=str(cat.name)[:60], callback_data=f"itm_cat:{i}")] for i, cat in enumerate(categories[:40])]
        await call.answer()
        await call.message.answer("📂 Выбери категорию:", reply_markup=InlineKeyboardMarkup(inline_keyboard=rows))
    except Exception as e:
        log.exception("choose PlayerOK game")
        await call.answer("Не удалось получить категории", show_alert=True)
        await call.message.answer(f"Ошибка: {esc(e)}")


@dp.callback_query(F.data.startswith("itm_cat:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_choose_category(call: CallbackQuery):
    try:
        state = listing_state[call.from_user.id]
        category_preview = state["categories"][int(call.data.split(":", 1)[1])]
        category = await asyncio.to_thread(acc.get_game_category, id=category_preview.id)
        obtaining_result = await asyncio.to_thread(acc.get_game_category_obtaining_types, category.id)
        obtaining_types = list(getattr(obtaining_result, "obtaining_types", []) or [])
        if not obtaining_types:
            return await call.answer("Для категории нет доступных способов получения", show_alert=True)
        state.update(category=category, obtaining_types=obtaining_types, step="obtaining")
        rows = [[InlineKeyboardButton(text=str(obj.name)[:60], callback_data=f"itm_obt:{i}")] for i, obj in enumerate(obtaining_types[:40])]
        await call.answer()
        await call.message.answer("📦 Выбери способ получения товара:", reply_markup=InlineKeyboardMarkup(inline_keyboard=rows))
    except Exception as e:
        log.exception("choose PlayerOK category")
        await call.answer("Не удалось загрузить категорию", show_alert=True)
        await call.message.answer(f"Ошибка: {esc(e)}")


@dp.callback_query(F.data.startswith("itm_obt:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_choose_obtaining(call: CallbackQuery):
    try:
        state = listing_state[call.from_user.id]
        obtaining = state["obtaining_types"][int(call.data.split(":", 1)[1])]
        state["obtaining"] = obtaining
        await call.answer()
        await _begin_option_selection(call.message, call.from_user.id, getattr(state["category"], "options", None))
    except Exception as e:
        log.exception("choose PlayerOK obtaining type")
        await call.answer("Не удалось выбрать способ получения", show_alert=True)
        await call.message.answer(f"Ошибка: {esc(e)}")


@dp.callback_query(F.data.startswith("itm_opt:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_option_choose(call: CallbackQuery):
    try:
        state = listing_state[call.from_user.id]
        options = state.get("current_options", [])
        option = options[int(call.data.split(":", 1)[1])]
        state.setdefault("selected_options", []).append(option)
        state["option_index"] = state.get("option_index", 0) + 1
        await call.answer("Выбрано")
        await _show_option_group(call.message, call.from_user.id)
    except Exception as e:
        log.exception("choose item option")
        await call.answer("Не удалось выбрать вариант", show_alert=True)


@dp.callback_query(F.data == "itm_opt_skip", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_option_skip(call: CallbackQuery):
    state = listing_state.get(call.from_user.id)
    if not state:
        return await call.answer("Мастер уже закрыт", show_alert=True)
    current_index = state.get("option_index", 0)
    groups = state.get("option_groups", [])
    if current_index < len(groups):
        state.setdefault("skipped_option_groups", set()).add(groups[current_index][0])
    state["option_index"] = current_index + 1
    await call.answer("Пропущено")
    await _show_option_group(call.message, call.from_user.id)


@dp.callback_query(F.data == "itm_opt_save", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_option_edit_save(call: CallbackQuery):
    state = listing_state.get(call.from_user.id, {})
    if state.get("mode") != "edit_options":
        return await call.answer("Сейчас нет изменений характеристик", show_alert=True)
    try:
        await asyncio.to_thread(
            acc.update_item,
            id=state["item_id"],
            options=state.get("selected_options", []),
        )
        item_id = state["item_id"]
        await call.answer("Характеристики сохранены ✅")
        await call.message.answer("✅ Характеристики объявления обновлены.")
        item = await asyncio.to_thread(acc.get_item, id=item_id)
        listing_state[call.from_user.id] = {"mode": "details", "item_id": item_id, "item": item}
        await call.message.answer(f"📦 <b>{esc(item.name)}</b>", reply_markup=_item_keyboard(item))
    except Exception as e:
        log.exception("save item options")
        await call.answer("Не удалось сохранить", show_alert=True)
        await call.message.answer(f"Ошибка обновления характеристик: {esc(e)}")


@dp.callback_query(F.data == "itm_opt_cancel", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_option_edit_cancel(call: CallbackQuery):
    state = listing_state.get(call.from_user.id, {})
    item_id = state.get("item_id")
    await call.answer("Отменено")
    if item_id:
        item = await asyncio.to_thread(acc.get_item, id=item_id)
        listing_state[call.from_user.id] = {"mode": "details", "item_id": item_id, "item": item}
        await call.message.answer(f"Изменение характеристик отменено.\n📦 <b>{esc(item.name)}</b>", reply_markup=_item_keyboard(item))


@dp.callback_query(F.data.startswith("itm_ed:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_edit_start(call: CallbackQuery):
    state = listing_state.get(call.from_user.id, {})
    if state.get("mode") != "details":
        return await call.answer("Сначала открой объявление", show_alert=True)
    item_id = state["item_id"]
    field = call.data.split(":", 1)[1]
    if field == "options":
        item = await asyncio.to_thread(acc.get_item, id=item_id)
        listing_state[call.from_user.id] = {"mode": "edit_options", "item_id": item_id, "item": item}
        await call.answer()
        return await _begin_option_selection(call.message, call.from_user.id, getattr(getattr(item, "category", None), "options", None))
    if field == "data":
        try:
            item = await asyncio.to_thread(acc.get_item, id=item_id)
            fields = [f for f in (getattr(item, "data_fields", []) or []) if _field_is_item_data(f)]
            if not fields:
                return await call.answer("У объявления нет редактируемых полей данных", show_alert=True)
            listing_state[call.from_user.id] = {
                "mode": "edit_data", "step": "data", "item_id": item_id,
                "fields": fields, "field_index": 0,
            }
            await call.answer()
            return await _ask_listing_data_field(call.message, call.from_user.id)
        except Exception as e:
            log.exception("load item data fields")
            return await call.answer(f"Не удалось загрузить поля: {e}", show_alert=True)
    if field == "photo":
        listing_state[call.from_user.id] = {"mode": "edit_photo", "step": "photo", "item_id": item_id}
        await call.answer()
        return await call.message.answer("🖼 Отправь фотографию как фото или изображение-файл. Можно отправить несколько. Когда закончишь, напиши /done. Для отмены — /cancel.")
    if field not in ("name", "description", "price"):
        return await call.answer("Неизвестное поле", show_alert=True)
    listing_state[call.from_user.id] = {"mode": "edit", "step": field, "item_id": item_id}
    prompts = {
        "name": "✏️ Отправь новое название объявления.",
        "description": "📝 Отправь новое описание объявления.",
        "price": "💰 Отправь новую цену в рублях (целое число).",
    }
    await call.answer()
    await call.message.answer(prompts[field] + " Для отмены — /cancel.")


@dp.callback_query(F.data.startswith("itm_rm_photo:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_remove_photo_ask(call: CallbackQuery):
    state = listing_state.get(call.from_user.id, {})
    if state.get("mode") != "details":
        return await call.answer("Сначала открой объявление", show_alert=True)
    try:
        attachments = list(getattr(state.get("item"), "attachments", []) or [])
        attachment = attachments[int(call.data.split(":", 1)[1])]
        attachment_id = getattr(attachment, "id", None)
        if not attachment_id:
            return await call.answer("Не удалось определить ID фотографии", show_alert=True)
        state["remove_attachment_id"] = attachment_id
        await call.answer()
        await call.message.answer(
            "Удалить выбранную фотографию из объявления?",
            reply_markup=InlineKeyboardMarkup(inline_keyboard=[
                [InlineKeyboardButton(text="🗑 Да, удалить фото", callback_data="itm_rm_photo_yes")],
                [InlineKeyboardButton(text="❌ Нет", callback_data="itm_rm_photo_no")],
            ]),
        )
    except (ValueError, IndexError):
        await call.answer("Фотография не найдена", show_alert=True)


@dp.callback_query(F.data == "itm_rm_photo_no", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_remove_photo_no(call: CallbackQuery):
    state = listing_state.get(call.from_user.id, {})
    state.pop("remove_attachment_id", None)
    await call.answer("Отменено")
    try:
        await call.message.edit_text("Удаление фотографии отменено.")
    except Exception:
        pass


@dp.callback_query(F.data == "itm_rm_photo_yes", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_remove_photo_yes(call: CallbackQuery):
    state = listing_state.get(call.from_user.id, {})
    attachment_id = state.pop("remove_attachment_id", None)
    item_id = state.get("item_id")
    if not attachment_id or not item_id:
        return await call.answer("Фотография не выбрана", show_alert=True)
    try:
        await asyncio.to_thread(acc.update_item, id=item_id, remove_attachments=[attachment_id])
        item = await asyncio.to_thread(acc.get_item, id=item_id)
        listing_state[call.from_user.id] = {"mode": "details", "item_id": item_id, "item": item}
        await call.answer("Фотография удалена ✅")
        await call.message.answer("✅ Фотография удалена из объявления.", reply_markup=_item_keyboard(item))
    except Exception as e:
        log.exception("remove item attachment")
        await call.answer("Не удалось удалить фото", show_alert=True)
        await call.message.answer(f"Ошибка удаления фотографии: {esc(e)}")


@dp.callback_query(F.data == "itm_delete", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_delete_ask(call: CallbackQuery):
    state = listing_state.get(call.from_user.id, {})
    if state.get("mode") != "details":
        return await call.answer("Сначала открой объявление", show_alert=True)
    await call.answer()
    await call.message.answer(
        "⚠️ <b>Удалить объявление полностью?</b> Это действие нельзя отменить.",
        reply_markup=InlineKeyboardMarkup(inline_keyboard=[
            [InlineKeyboardButton(text="🗑 Да, удалить", callback_data="itm_delete_yes")],
            [InlineKeyboardButton(text="❌ Нет", callback_data="itm_delete_no")],
        ]),
    )


@dp.callback_query(F.data == "itm_delete_no", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_delete_no(call: CallbackQuery):
    await call.answer("Удаление отменено")
    try:
        await call.message.edit_text("Удаление объявления отменено.")
    except Exception:
        pass


@dp.callback_query(F.data == "itm_delete_yes", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_delete_yes(call: CallbackQuery):
    state = listing_state.get(call.from_user.id, {})
    if state.get("mode") != "details" or not state.get("item_id"):
        return await call.answer("Не найдено объявление для удаления", show_alert=True)
    try:
        result = await asyncio.to_thread(acc.remove_item, id=state["item_id"])
        listing_state.pop(call.from_user.id, None)
        await call.answer("Объявление удалено" if result else "Запрос выполнен")
        await show_my_items(call.message, call.from_user.id, notice="Объявление удалено.")
    except Exception as e:
        log.exception("remove PlayerOK item")
        await call.answer("Не удалось удалить", show_alert=True)
        await call.message.answer(f"Ошибка удаления: {esc(e)}")


@dp.callback_query(F.data == "itm_publish", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_publish_start(call: CallbackQuery):
    state = listing_state.get(call.from_user.id, {})
    if state.get("mode") not in ("details", "publish"):
        return await call.answer("Сначала выбери объявление", show_alert=True)
    item_id = state.get("item_id")
    if not item_id:
        return await call.answer("Не найден ID объявления", show_alert=True)
    try:
        statuses = await asyncio.to_thread(acc.get_item_priority_statuses, item_id, state.get("item").price if state.get("item") else 0)
        statuses = list(statuses or [])
        if not statuses:
            return await call.answer("PlayerOK не вернул доступные статусы публикации", show_alert=True)
        listing_state[call.from_user.id] = {"mode": "publish", "item_id": item_id, "statuses": statuses}
        rows = []
        for i, status in enumerate(statuses[:20]):
            cost = int(getattr(status, "price", 0) or 0)
            label = "Бесплатная публикация" if cost == 0 else f"{getattr(status, 'name', 'Приоритет')} · {cost} ₽"
            rows.append([InlineKeyboardButton(text=label[:60], callback_data=f"itm_ps:{i}")])
        await call.answer()
        await call.message.answer(
            "🚀 <b>Выбери статус публикации.</b>\n"
            "Платные варианты списывают указанную сумму с баланса PlayerOK только после следующего подтверждения.",
            reply_markup=InlineKeyboardMarkup(inline_keyboard=rows),
        )
    except Exception as e:
        log.exception("get item priority statuses")
        await call.answer("Не удалось получить варианты публикации", show_alert=True)
        await call.message.answer(f"Ошибка: {esc(e)}")


@dp.callback_query(F.data.startswith("itm_ps:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_publish_choose_status(call: CallbackQuery):
    state = listing_state.get(call.from_user.id, {})
    try:
        status = state["statuses"][int(call.data.split(":", 1)[1])]
    except (KeyError, ValueError, IndexError):
        return await call.answer("Вариант публикации недоступен", show_alert=True)
    state["publish_status"] = status
    cost = int(getattr(status, "price", 0) or 0)
    await call.answer()
    await call.message.answer(
        f"Подтверди публикацию с вариантом <b>{esc(getattr(status, 'name', 'Стандартный'))}</b>.\n"
        f"Стоимость: <b>{cost} ₽</b>." + ("\nДеньги будут списаны с баланса PlayerOK." if cost else "\nСписание не требуется."),
        reply_markup=InlineKeyboardMarkup(inline_keyboard=[
            [InlineKeyboardButton(text=f"✅ Подтвердить публикацию · {cost} ₽", callback_data="itm_pub_confirm")],
            [InlineKeyboardButton(text="❌ Отмена", callback_data="itm_pub_cancel")],
        ]),
    )


@dp.callback_query(F.data == "itm_pub_cancel", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_publish_cancel(call: CallbackQuery):
    state = listing_state.get(call.from_user.id, {})
    state.pop("publish_status", None)
    await call.answer("Публикация отменена")
    try:
        await call.message.edit_text("Публикация отменена. Черновик сохранён на PlayerOK.")
    except Exception:
        pass


@dp.callback_query(F.data == "itm_pub_confirm", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_publish_confirm(call: CallbackQuery):
    state = listing_state.get(call.from_user.id, {})
    status = state.get("publish_status")
    item_id = state.get("item_id")
    if not status or not item_id:
        return await call.answer("Не найдено подтверждение публикации", show_alert=True)
    try:
        published = await asyncio.to_thread(acc.publish_item, item_id, status.id)
        listing_state[call.from_user.id] = {"mode": "details", "item_id": item_id, "item": published}
        url = _listing_url(published)
        text = (
            f"✅ <b>Запрос на публикацию отправлен!</b>\n"
            f"Товар: {esc(getattr(published, 'name', '—'))}\n"
            f"Статус PlayerOK: {esc(_status_name(published))}"
        )
        if url:
            text += f"\n🔗 <a href=\"{esc(url)}\">Открыть объявление</a>"
        await call.answer("Публикация отправлена ✅")
        await call.message.answer(text, reply_markup=_item_keyboard(published))
    except Exception as e:
        log.exception("publish PlayerOK item")
        await call.answer("Не удалось опубликовать", show_alert=True)
        await call.message.answer(f"Ошибка публикации: {esc(e)}")


async def _finish_create_photos(message: Message, user_id: int):
    state = listing_state[user_id]
    state["step"] = "create_preview"
    photo_count = len(state.get("attachments", []))
    text = (
        "🧾 <b>Проверь объявление перед созданием черновика</b>\n\n"
        f"🎮 Игра: {esc(state['game'].name)}\n"
        f"📂 Категория: {esc(state['category'].name)}\n"
        f"📦 Получение: {esc(state['obtaining'].name)}\n"
        f"📝 Название: {esc(state['name'])}\n"
        f"💰 Цена: <b>{state['price']} ₽</b>\n"
        f"📷 Фотографий: {photo_count}\n\n"
        f"{esc(state['description'])}"
    )
    await message.answer(text, reply_markup=InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="✅ Создать черновик на PlayerOK", callback_data="itm_create_confirm")],
        [InlineKeyboardButton(text="📋 Сохранить как пресет", callback_data="itm_preset_save_current")],
        [InlineKeyboardButton(text="❌ Отмена", callback_data="itm_create_cancel")],
    ]))


@dp.callback_query(F.data == "itm_create_confirm", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_create_confirm(call: CallbackQuery):
    state = listing_state.get(call.from_user.id, {})
    if state.get("mode") != "create" or state.get("step") != "create_preview":
        return await call.answer("Мастер создания уже закрыт", show_alert=True)
    try:
        created = await asyncio.to_thread(
            acc.create_item,
            game_category_id=state["category"].id,
            obtaining_type_id=state["obtaining"].id,
            name=state["name"],
            price=state["price"],
            description=state["description"],
            options=state.get("selected_options", []),
            data_fields=state.get("fields", []),
            attachments=state.get("attachments", []),
        )
        item_id = str(created.id)
        listing_state[call.from_user.id] = {"mode": "publish", "item_id": item_id, "item": created}
        url = _listing_url(created)
        text = (
            f"✅ <b>Черновик создан на PlayerOK!</b>\n"
            f"📦 {esc(created.name)}\n💰 {esc(created.price)} ₽\n"
            "Объявление ещё не опубликовано. Для публикации нажми кнопку ниже."
        )
        if url:
            text += f"\n🔗 <a href=\"{esc(url)}\">Открыть черновик</a>"
        await call.answer("Черновик создан ✅")
        await call.message.answer(text, reply_markup=InlineKeyboardMarkup(inline_keyboard=[
            [InlineKeyboardButton(text="🚀 Перейти к публикации", callback_data="itm_publish")],
            [InlineKeyboardButton(text="🛍 Мои товары", callback_data="itm_list")],
        ]))
    except Exception as e:
        log.exception("create PlayerOK item")
        await call.answer("Не удалось создать черновик", show_alert=True)
        await call.message.answer(f"Ошибка создания товара: {esc(e)}")


@dp.callback_query(F.data == "itm_create_cancel", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def item_create_cancel(call: CallbackQuery):
    listing_state.pop(call.from_user.id, None)
    await call.answer("Создание отменено")
    try:
        await call.message.edit_text("Создание объявления отменено. Ничего не опубликовано.")
    except Exception:
        pass


# --- Ввод текста в пошаговом мастере ---
@dp.message(F.text, F.func(lambda m: (
    m.from_user is not None
    and m.from_user.id == ADMIN_ID
    and m.from_user.id in listing_state
    and (
        (listing_state[m.from_user.id].get("mode") == "create" and listing_state[m.from_user.id].get("step") in ("game", "data", "name", "description", "price", "photos"))
        or (listing_state[m.from_user.id].get("mode") == "edit" and listing_state[m.from_user.id].get("step") in ("name", "description", "price"))
        or (listing_state[m.from_user.id].get("mode") == "edit_data" and listing_state[m.from_user.id].get("step") == "data")
        or listing_state[m.from_user.id].get("mode") == "edit_photo"
    )
)))
async def listing_text_input(message: Message):
    user_id = message.from_user.id
    state = listing_state.get(user_id)
    if not state:
        return
    text = (message.text or "").strip()
    if not text:
        return await message.answer("Пустое значение не сохранено. Отправь текст ещё раз или /cancel.")
    mode = state.get("mode")
    step = state.get("step")
    log.info("Listing text input: admin=%s mode=%s step=%s", user_id, mode, step)

    if mode == "create" and step == "game":
        try:
            result = await asyncio.to_thread(acc.get_games, name=text, count=24)
            games = list(getattr(result, "games", []) or [])
            if not games:
                return await message.answer("Игра не найдена. Попробуй другое название или /cancel.")
            state["games"] = games
            state["step"] = "choose_game"
            rows = [[InlineKeyboardButton(text=str(game.name)[:60], callback_data=f"itm_game:{i}")] for i, game in enumerate(games[:24])]
            return await message.answer("🎮 Выбери найденную игру:", reply_markup=InlineKeyboardMarkup(inline_keyboard=rows))
        except Exception as e:
            log.exception("search PlayerOK games")
            return await message.answer(f"Не удалось найти игру: {esc(e)}")

    if mode == "create" and step == "data":
        fields = state.get("fields", [])
        idx = state.get("field_index", 0)
        if idx >= len(fields):
            state["step"] = "name"
            return await message.answer("📝 Введи название объявления.")
        field = fields[idx]
        if text == "/skip":
            if bool(getattr(field, "required", False)):
                return await message.answer("Это поле обязательное. Введи значение или /cancel.")
            field.value = ""
        else:
            field.value = text
        state["field_index"] = idx + 1
        return await _ask_listing_data_field(message, user_id)

    if mode == "edit_data" and step == "data":
        fields = state.get("fields", [])
        idx = state.get("field_index", 0)
        if idx >= len(fields):
            return await message.answer("Все поля уже заполнены.")
        field = fields[idx]
        if text == "/skip":
            if bool(getattr(field, "required", False)):
                return await message.answer("Это поле обязательное. Введи значение или /cancel.")
        else:
            field.value = text
        state["field_index"] = idx + 1
        if state["field_index"] < len(fields):
            return await _ask_listing_data_field(message, user_id)
        try:
            await asyncio.to_thread(acc.update_item, id=state["item_id"], data_fields=fields)
            item_id = state["item_id"]
            item = await asyncio.to_thread(acc.get_item, id=item_id)
            listing_state[user_id] = {"mode": "details", "item_id": item_id, "item": item}
            return await message.answer("✅ Данные товара обновлены.", reply_markup=_item_keyboard(item))
        except Exception as e:
            log.exception("update item data fields")
            return await message.answer(f"Не удалось обновить данные: {esc(e)}")

    if mode == "create" and step == "name":
        state["name"] = text
        state["step"] = "description"
        return await message.answer("📝 Введи описание объявления.")

    if mode == "create" and step == "description":
        state["description"] = text
        state["step"] = "price"
        log.info("Listing wizard: description saved for admin %s; waiting for price", user_id)
        return await message.answer("💰 Описание сохранено. Теперь введи цену в рублях (целое число, например 250).")

    if mode == "create" and step == "price":
        try:
            price = int(text.replace(" ", ""))
            if price <= 0:
                raise ValueError
        except ValueError:
            return await message.answer("Цена должна быть положительным целым числом. Например: 250.")
        state["price"] = price
        state["attachments"] = []
        state["step"] = "photos"
        return await message.answer("🖼 Теперь отправь фотографии объявления (можно несколько). Когда закончишь — /done. Без фото — /skip. Для отмены — /cancel.")

    if mode == "create" and step == "photos":
        if text in ("/done", "/skip"):
            return await _finish_create_photos(message, user_id)
        return await message.answer("Отправь изображение или напиши /done, чтобы перейти к проверке объявления.")

    if mode == "edit_photo":
        if text == "/done":
            item_id = state["item_id"]
            try:
                item = await asyncio.to_thread(acc.get_item, id=item_id)
                listing_state[user_id] = {"mode": "details", "item_id": item_id, "item": item}
                return await message.answer("✅ Работа с фотографиями завершена.", reply_markup=_item_keyboard(item))
            except Exception as e:
                return await message.answer(f"Фото могли сохраниться, но не удалось обновить карточку: {esc(e)}")
        return await message.answer("Отправь изображение или напиши /done. Для отмены — /cancel.")

    if mode == "edit" and step in ("name", "description", "price"):
        kwargs = {step: text}
        if step == "price":
            try:
                price = int(text.replace(" ", ""))
                if price <= 0:
                    raise ValueError
                kwargs["price"] = price
            except ValueError:
                return await message.answer("Цена должна быть положительным целым числом. Например: 250.")
        try:
            item_id = state["item_id"]
            await asyncio.to_thread(acc.update_item, id=item_id, **kwargs)
            item = await asyncio.to_thread(acc.get_item, id=item_id)
            listing_state[user_id] = {"mode": "details", "item_id": item_id, "item": item}
            return await message.answer("✅ Объявление обновлено.", reply_markup=_item_keyboard(item))
        except Exception as e:
            log.exception("update PlayerOK item field")
            return await message.answer(f"Не удалось обновить объявление: {esc(e)}")


async def _save_listing_image(message: Message, user_id: int, file_id: str):
    state = listing_state.get(user_id, {})
    try:
        file_info = await bot.get_file(file_id)
        buffer = io.BytesIO()
        await bot.download_file(file_info.file_path, destination=buffer)
        image_bytes = buffer.getvalue()
        if not image_bytes:
            raise ValueError("Telegram вернул пустой файл")
        if state.get("mode") == "create" and state.get("step") == "photos":
            attachments = state.setdefault("attachments", [])
            if len(attachments) >= 10:
                return await message.answer("Можно добавить не более 10 фотографий.")
            attachments.append(image_bytes)
            return await message.answer(f"✅ Фото добавлено ({len(attachments)}/10). Отправь ещё или напиши /done.")
        if state.get("mode") == "edit_photo":
            await asyncio.to_thread(acc.update_item, id=state["item_id"], add_attachments=[image_bytes])
            return await message.answer("✅ Фотография добавлена к объявлению. Можно отправить ещё или /done.")
    except Exception as e:
        log.exception("download or save listing image")
        await message.answer(f"Не удалось добавить изображение: {esc(e)}")


@dp.message(F.photo, F.func(lambda m: m.from_user is not None and m.from_user.id == ADMIN_ID and m.from_user.id in listing_state))
async def listing_photo_input(message: Message):
    state = listing_state.get(message.from_user.id, {})
    if (state.get("mode") == "create" and state.get("step") == "photos") or state.get("mode") == "edit_photo":
        await _save_listing_image(message, message.from_user.id, message.photo[-1].file_id)
    else:
        await message.answer("Сейчас бот не ожидает фотографию товара.")


@dp.message(F.document, F.func(lambda m: m.from_user is not None and m.from_user.id == ADMIN_ID and m.from_user.id in listing_state))
async def listing_document_input(message: Message):
    state = listing_state.get(message.from_user.id, {})
    mime = str(getattr(message.document, "mime_type", "") or "")
    if mime.startswith("image/") and ((state.get("mode") == "create" and state.get("step") == "photos") or state.get("mode") == "edit_photo"):
        await _save_listing_image(message, message.from_user.id, message.document.file_id)
    else:
        await message.answer("Пришли изображение в формате фото или image/* файла.")


@dp.message(Command("done"), F.func(lambda m: m.from_user is not None and m.from_user.id == ADMIN_ID))
async def listing_done_command(message: Message):
    state = listing_state.get(message.from_user.id, {})
    if state.get("mode") == "create" and state.get("step") == "photos":
        return await _finish_create_photos(message, message.from_user.id)
    if state.get("mode") == "edit_photo":
        item_id = state["item_id"]
        try:
            item = await asyncio.to_thread(acc.get_item, id=item_id)
            listing_state[message.from_user.id] = {"mode": "details", "item_id": item_id, "item": item}
            return await message.answer("✅ Работа с фотографиями завершена.", reply_markup=_item_keyboard(item))
        except Exception as e:
            return await message.answer(f"Фото могли сохраниться, но не удалось обновить карточку: {esc(e)}")
    await message.answer("Сейчас нет шага, который можно завершить командой /done.")


@dp.message(F.text == "/skip", F.func(lambda m: m.from_user is not None and m.from_user.id == ADMIN_ID))
async def listing_skip_command(message: Message):
    state = listing_state.get(message.from_user.id, {})
    if state.get("mode") == "create" and state.get("step") == "photos":
        return await _finish_create_photos(message, message.from_user.id)
    await listing_text_input(message)


# ---------- Просьба об отзыве после подтверждения сделки ----------

REVIEW_FILE = os.getenv("REVIEW_FILE", "review_request.json")
DEFAULT_REVIEW_TEXT = (
    "Спасибо за покупку, {buyer}! 🙌 Если всё понравилось, оставьте, пожалуйста, отзыв — "
    "для меня это очень важно ⭐\n— {seller}"
)


def load_review_settings() -> dict:
    settings = {"enabled": True, "text": DEFAULT_REVIEW_TEXT}
    try:
        with open(REVIEW_FILE, "r", encoding="utf-8") as f:
            data = json.load(f)
        if isinstance(data, dict):
            if isinstance(data.get("enabled"), bool):
                settings["enabled"] = data["enabled"]
            if isinstance(data.get("text"), str) and data["text"].strip():
                settings["text"] = data["text"]
    except FileNotFoundError:
        pass
    except Exception:
        log.exception("Не удалось загрузить настройки просьбы об отзыве")
    return settings


REVIEW_SETTINGS: dict = load_review_settings()
review_state: set[int] = set()  # админ вводит новый текст просьбы
review_requested = BoundedSet()  # сделки, по которым просьба уже отправлена


def save_review_settings() -> None:
    _atomic_json_save(REVIEW_FILE, REVIEW_SETTINGS)


def review_request_for_deal(event, loop) -> None:
    """После подтверждения сделки сама просит покупателя оставить отзыв (один раз на сделку)."""
    if not REVIEW_SETTINGS.get("enabled"):
        return
    deal = getattr(event, "deal", None)
    deal_id = getattr(deal, "id", None)
    chat_id = getattr(getattr(event, "chat", None), "id", None) or getattr(getattr(deal, "chat", None), "id", None)
    if deal is None or not chat_id or deal_id in review_requested:
        return
    if getattr(deal, "review", None) is not None:
        return  # отзыв уже оставлен — просить не нужно
    text = render_reply(REVIEW_SETTINGS["text"], deal)
    try:
        acc.send_message(chat_id=chat_id, text=text)
        review_requested.add(deal_id)
        report = f"⭐ <b>Просьба об отзыве отправлена</b>:\n{esc(text)}"
    except Exception as e:
        log.exception("review_request")
        report = f"❌ Не удалось отправить просьбу об отзыве по сделке <code>{esc(deal_id)}</code>: {esc(e)}"
        asyncio.run_coroutine_threadsafe(notify(report, chat_id), loop)
        return
    if notif_on("autoreply"):
        asyncio.run_coroutine_threadsafe(notify(report, chat_id), loop)


def review_keyboard() -> InlineKeyboardMarkup:
    mark = "✅ Включена" if REVIEW_SETTINGS["enabled"] else "❌ Выключена"
    return InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text=f"Просьба: {mark}", callback_data="rv:t")],
        [InlineKeyboardButton(text="✏️ Изменить текст", callback_data="rv:e")],
    ])


def review_text() -> str:
    return (
        "⭐ <b>Просьба об отзыве</b>\n"
        "Когда покупатель подтверждает сделку, бот сам отправляет ему это сообщение (один раз на сделку).\n"
        "Подстановки: <code>{buyer}</code>, <code>{seller}</code>, <code>{item}</code>, "
        "<code>{price}</code>, <code>{deal_id}</code>.\n\n"
        f"<b>Текущий текст:</b>\n{esc(REVIEW_SETTINGS['text'])}"
    )


@dp.message(Command("review"), F.func(is_admin))
async def cmd_review(message: Message):
    await message.answer(review_text(), reply_markup=review_keyboard())


@dp.callback_query(F.data == "rv:s", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def review_show(call: CallbackQuery):
    await call.answer()
    await call.message.answer(review_text(), reply_markup=review_keyboard())


@dp.callback_query(F.data == "rv:t", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def review_toggle(call: CallbackQuery):
    REVIEW_SETTINGS["enabled"] = not REVIEW_SETTINGS["enabled"]
    try:
        save_review_settings()
    except Exception as e:
        REVIEW_SETTINGS["enabled"] = not REVIEW_SETTINGS["enabled"]
        log.exception("save review settings")
        return await call.answer(f"Не удалось сохранить: {e}", show_alert=True)
    await call.answer("Сохранено")
    try:
        await call.message.edit_reply_markup(reply_markup=review_keyboard())
    except Exception:
        pass


@dp.callback_query(F.data == "rv:e", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def review_edit_start(call: CallbackQuery):
    review_state.add(call.from_user.id)
    await call.answer()
    await call.message.answer(
        "✏️ Отправь новый текст просьбы об отзыве. Можно использовать {buyer}, {seller}, {item}, "
        "{price}, {deal_id}. Для отмены — /cancel."
    )


@dp.message(F.text, F.func(lambda m: m.from_user is not None and m.from_user.id == ADMIN_ID and m.from_user.id in review_state and not _is_listing_text_stage(m.from_user.id)))
async def review_input_text(message: Message):
    value = (message.text or "").strip()
    if not value:
        return await message.answer("Пустой текст не сохранён. Отправь текст ещё раз или /cancel.")
    old = REVIEW_SETTINGS["text"]
    REVIEW_SETTINGS["text"] = value
    try:
        save_review_settings()
    except Exception as e:
        REVIEW_SETTINGS["text"] = old
        log.exception("save review text")
        return await message.answer(f"Не удалось сохранить: {esc(e)}")
    review_state.discard(message.from_user.id)
    await message.answer("✅ Текст просьбы об отзыве сохранён.", reply_markup=review_keyboard())


# ---------- Настройки уведомлений ----------

NOTIFY_FILE = os.getenv("NOTIFY_FILE", "notify_settings.json")
NOTIFY_CATEGORIES = {
    "msg_buyer": "💬 Сообщения покупателей",
    "msg_support": "🔰 Сообщения поддержки",
    "msg_system": "ℹ️ Системные сообщения чата",
    "deal_new": "🛒 Новые покупки",
    "deal_confirmed": "✅ Подтверждения сделок",
    "deal_refund": "↩️ Возвраты",
    "deal_problem": "⚠️ Проблемы в сделках",
    "review": "📝 Отзывы",
    "unfreeze": "🔓 Разморозка средств",
    "autoreply": "🤖 Отчёты об автоответах",
}
NOTIFY_DEFAULTS = {key: True for key in NOTIFY_CATEGORIES}
NOTIFY_DEFAULTS["msg_system"] = False  # дублирует события сделок, поэтому по умолчанию выключено
# Готовые наборы: ключ -> (название кнопки, какие категории включены)
NOTIFY_PRESETS = {
    "all": ("🔔 Всё", set(NOTIFY_CATEGORIES)),
    "support": ("🔰 Только поддержка", {"msg_support"}),
    "buys": ("🛒 Только покупки", {"deal_new"}),
    "nosupport": ("🙈 Без поддержки", set(NOTIFY_CATEGORIES) - {"msg_support", "msg_system"}),
    "none": ("🔕 Выключить всё", set()),
}


def load_notify_settings() -> dict:
    settings = dict(NOTIFY_DEFAULTS)
    try:
        with open(NOTIFY_FILE, "r", encoding="utf-8") as f:
            data = json.load(f)
        if isinstance(data, dict):
            for key in settings:
                if isinstance(data.get(key), bool):
                    settings[key] = data[key]
    except FileNotFoundError:
        pass
    except Exception:
        log.exception("Не удалось загрузить настройки уведомлений")
    return settings


NOTIFY_SETTINGS: dict = load_notify_settings()


def save_notify_settings() -> None:
    _atomic_json_save(NOTIFY_FILE, NOTIFY_SETTINGS)


def notif_on(category: str) -> bool:
    # Неизвестные категории должны быть выключены, а не случайно разрешены.
    return NOTIFY_SETTINGS.get(category, False) is True


def notify_keyboard() -> InlineKeyboardMarkup:
    rows = []
    for key, label in NOTIFY_CATEGORIES.items():
        mark = "✅" if NOTIFY_SETTINGS.get(key) else "❌"
        rows.append([InlineKeyboardButton(text=f"{mark} {label}", callback_data=f"ns:t:{key}")])
    preset_buttons = [
        InlineKeyboardButton(text=label, callback_data=f"ns:p:{key}")
        for key, (label, _enabled) in NOTIFY_PRESETS.items()
    ]
    for i in range(0, len(preset_buttons), 2):
        rows.append(preset_buttons[i:i + 2])
    return InlineKeyboardMarkup(inline_keyboard=rows)


NOTIFY_TEXT = (
    "🔔 <b>Уведомления</b>\n"
    "Нажми на пункт, чтобы включить или выключить. ✅ — приходят, ❌ — не приходят.\n"
    "Внизу — готовые наборы.\n\n"
    "<i>Автоответы покупателям от этих настроек не зависят: они отправляются всегда.</i>"
)


@dp.message(F.text == MENU_NOTIFY, F.func(is_admin))
@dp.message(Command("notify"), F.func(is_admin))
async def cmd_notify(message: Message):
    await message.answer(NOTIFY_TEXT, reply_markup=notify_keyboard())


@dp.callback_query(F.data.startswith("ns:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def notify_toggle(call: CallbackQuery):
    try:
        _, kind, key = call.data.split(":", 2)
    except ValueError:
        return await call.answer()
    backup = dict(NOTIFY_SETTINGS)
    if kind == "t" and key in NOTIFY_CATEGORIES:
        NOTIFY_SETTINGS[key] = not NOTIFY_SETTINGS.get(key, True)
    elif kind == "p" and key in NOTIFY_PRESETS:
        enabled = NOTIFY_PRESETS[key][1]
        for category in NOTIFY_CATEGORIES:
            NOTIFY_SETTINGS[category] = category in enabled
    else:
        return await call.answer("Неизвестная настройка", show_alert=True)
    try:
        save_notify_settings()
    except Exception:
        NOTIFY_SETTINGS.clear()
        NOTIFY_SETTINGS.update(backup)
        log.exception("save notify settings")
        return await call.answer("Не удалось сохранить настройки", show_alert=True)
    if kind == "t":
        state_text = "включены" if NOTIFY_SETTINGS.get(key) else "выключены"
        await call.answer(f"Уведомления {state_text}")
    else:
        await call.answer("Настройки набора сохранены")
    try:
        await call.message.edit_reply_markup(reply_markup=notify_keyboard())
    except Exception:
        pass


# ---------- Разморозка средств (слежение за балансом) ----------
# В PlayerOK нет отдельного события «средства разморожены», поэтому бот раз в минуту
# сравнивает баланс: если замороженная/ожидаемая часть уменьшилась, а доступная выросла — это разморозка.

BALANCE_POLL_SECONDS = int(os.getenv("BALANCE_POLL_SECONDS", "60"))


def _num(value) -> float:
    try:
        return float(value or 0)
    except (TypeError, ValueError):
        return 0.0


def fmt_money(value) -> str:
    text = f"{float(value):,.2f}".replace(",", " ")
    return text[:-3] if text.endswith(".00") else text


def read_balance() -> dict:
    balance = acc.get().profile.balance
    return {k: _num(getattr(balance, k, 0)) for k in ("value", "available", "frozen", "withdrawable", "pending_income")}


def detect_unfreeze(prev: dict, cur: dict) -> float:
    """Сколько средств разморожено между двумя замерами баланса (0, если ничего)."""
    released = max(prev["pending_income"] - cur["pending_income"], prev["frozen"] - cur["frozen"])
    gained = cur["available"] > prev["available"] or cur["withdrawable"] > prev["withdrawable"]
    return released if released > 0 and gained else 0.0


def balance_watch_thread(loop: asyncio.AbstractEventLoop) -> None:
    prev = None
    while True:
        time.sleep(BALANCE_POLL_SECONDS)
        if acc is None:
            continue
        try:
            cur = read_balance()
            if prev is not None:
                released = detect_unfreeze(prev, cur)
                if released > 0 and notif_on("unfreeze"):
                    text = (
                        f"🔓 <b>Средства разморожены</b>: {fmt_money(released)} ₽\n"
                        f"Доступно: {fmt_money(cur['available'])} ₽\n"
                        f"К выводу: {fmt_money(cur['withdrawable'])} ₽\n"
                        f"Ещё заморожено/ожидается: {fmt_money(cur['frozen'])} / {fmt_money(cur['pending_income'])} ₽"
                    )
                    asyncio.run_coroutine_threadsafe(notify(text), loop)
            prev = cur
        except Exception:
            log.exception("Не удалось проверить баланс")


# ---------- Сделки и статистика ----------

TZ_OFFSET_HOURS = float(os.getenv("TZ_OFFSET_HOURS", "3"))  # часовой пояс для статистики (3 = Москва)
LOCAL_TZ = timezone(timedelta(hours=TZ_OFFSET_HOURS))
EPOCH = datetime.min.replace(tzinfo=timezone.utc)
_FRACTION_RE = re.compile(r"(\.\d{6})\d+")

DEAL_STATUS_LABELS = {
    "PAID": "💳 Оплачена",
    "PENDING": "⏳ Ожидает выдачи",
    "SENT": "📦 Товар отправлен",
    "CONFIRMED": "✅ Подтверждена",
    "CONFIRMED_AUTOMATICALLY": "✅ Подтверждена автоматически",
    "ROLLED_BACK": "↩️ Возврат средств",
}
WORK_STATUSES = {"PAID", "PENDING", "SENT"}
DONE_STATUSES = {"CONFIRMED", "CONFIRMED_AUTOMATICALLY"}
REFUND_STATUSES = {"ROLLED_BACK"}
DEAL_GROUPS = {
    "all": ("📋 Все", None),
    "work": ("⏳ В работе", WORK_STATUSES),
    "done": ("✅ Завершены", DONE_STATUSES),
    "refund": ("↩️ Возвраты", REFUND_STATUSES),
    "problem": ("⚠️ С проблемой", None),
}
DEALS_PAGE_SIZE = 10
DEALS_TTL = 60  # секунд хранить загруженный список сделок
DEALS_CACHE: dict = {"ts": 0.0, "deals": None}
_DEALS_LOCK = asyncio.Lock()
deals_state: dict[int, dict] = {}


def parse_dt(value):
    if not value:
        return None
    text = _FRACTION_RE.sub(r"\1", str(value).strip().replace("Z", "+00:00"))
    try:
        dt = datetime.fromisoformat(text)
    except ValueError:
        return None
    return dt if dt.tzinfo else dt.replace(tzinfo=timezone.utc)


def deal_status_key(deal) -> str:
    status = getattr(deal, "status", None)
    return str(getattr(status, "name", None) or status or "").upper()


def deal_status_text(deal) -> str:
    key = deal_status_key(deal)
    label = DEAL_STATUS_LABELS.get(key, key or "—")
    if getattr(deal, "has_problem", False) and key not in REFUND_STATUSES:
        label += " · ⚠️ проблема"
    return label


def deal_price(deal) -> float:
    return _num(getattr(getattr(deal, "item", None), "price", 0))


def deal_in_group(deal, group: str) -> bool:
    if group == "all":
        return True
    if group == "problem":
        return bool(getattr(deal, "has_problem", False)) and deal_status_key(deal) not in REFUND_STATUSES
    statuses = DEAL_GROUPS.get(group, DEAL_GROUPS["all"])[1]
    return statuses is None or deal_status_key(deal) in statuses


async def load_all_deals(force: bool = False) -> list:
    """Загружает все ваши продажи (по 24 за запрос) и ненадолго запоминает результат."""
    def fresh() -> bool:
        return DEALS_CACHE["deals"] is not None and time.time() - DEALS_CACHE["ts"] < DEALS_TTL

    if not force and fresh():
        return DEALS_CACHE["deals"]
    # Замок: «Сделки» и «Статистика», нажатые одновременно, раньше оба качали все страницы заново.
    async with _DEALS_LOCK:
        if not force and fresh():
            return DEALS_CACHE["deals"]
        kwargs = {"count": 24}
        if ItemDealDirections is not None:
            kwargs["direction"] = ItemDealDirections.OUT  # OUT — продажи; IN — ваши покупки
        deals, cursor, seen = [], None, set()
        for _ in range(500):  # защита от зацикливания курсора
            result = await asyncio.to_thread(acc.get_deals, after_cursor=cursor, **kwargs)
            deals.extend(getattr(result, "deals", []) or [])
            page_info = getattr(result, "page_info", None)
            if not getattr(page_info, "has_next_page", False):
                break
            cursor = getattr(page_info, "end_cursor", None)
            if not cursor or cursor in seen:
                break
            seen.add(cursor)
        # Каждую дату разбираем один раз, а не при каждом сравнении сортировки.
        keyed = [(parse_dt(getattr(d, "created_at", None)) or EPOCH, d) for d in deals]
        keyed.sort(key=lambda pair: pair[0], reverse=True)
        deals = [d for _created, d in keyed]
        DEALS_CACHE.update(ts=time.time(), deals=deals)
        return deals


def deals_keyboard(filter_key: str, index: int, total_pages: int) -> InlineKeyboardMarkup:
    buttons = []
    for key, (label, _statuses) in DEAL_GROUPS.items():
        shown = f"✓ {label}" if key == filter_key else label
        buttons.append(InlineKeyboardButton(text=shown, callback_data=f"dl:f:{key}"))
    rows = [buttons[:3], buttons[3:]]
    nav = []
    if index > 0:
        nav.append(InlineKeyboardButton(text="⬅️ Назад", callback_data="dl:p"))
    if index + 1 < total_pages:
        nav.append(InlineKeyboardButton(text="Вперёд ➡️", callback_data="dl:n"))
    if nav:
        rows.append(nav)
    rows.append([InlineKeyboardButton(text="🔄 Обновить", callback_data="dl:r")])
    return InlineKeyboardMarkup(inline_keyboard=rows)


async def render_deals(admin_id: int, force: bool = False):
    state = deals_state.setdefault(admin_id, {"filter": "all", "index": 0})
    deals = await load_all_deals(force)
    filter_key = state.get("filter", "all")
    filtered = [d for d in deals if deal_in_group(d, filter_key)]
    total_pages = max(1, (len(filtered) + DEALS_PAGE_SIZE - 1) // DEALS_PAGE_SIZE)
    state["index"] = min(max(0, state.get("index", 0)), total_pages - 1)
    start = state["index"] * DEALS_PAGE_SIZE
    label = DEAL_GROUPS.get(filter_key, DEAL_GROUPS["all"])[0]
    lines = [
        f"📋 <b>Мои сделки</b> · {esc(label)}",
        f"Найдено: {len(filtered)} · страница {state['index'] + 1}/{total_pages}",
        "",
    ]
    page = filtered[start:start + DEALS_PAGE_SIZE]
    if not page:
        lines.append("Сделок не найдено.")
    for number, deal in enumerate(page, start=start + 1):
        name = str(getattr(getattr(deal, "item", None), "name", None) or "Без названия")
        if len(name) > 70:
            name = name[:67] + "…"
        lines.append(f"{number}. <b>{esc(name)}</b>\n      {esc(deal_status_text(deal))}")
    return "\n".join(lines), deals_keyboard(filter_key, state["index"], total_pages)


@dp.message(F.text == MENU_DEALS, F.func(is_admin))
@dp.message(Command("deals"), F.func(is_admin))
async def cmd_deals(message: Message):
    if acc is None:
        return await message.answer("PlayerOK ещё подключается. Попробуй чуть позже.")
    state = deals_state.setdefault(message.from_user.id, {"filter": "all", "index": 0})
    state["index"] = 0
    wait = await message.answer("⏳ Загружаю сделки…")
    try:
        text, markup = await render_deals(message.from_user.id)
        await wait.edit_text(text, reply_markup=markup)
    except Exception as e:
        log.exception("deals")
        await wait.edit_text(f"Не удалось загрузить сделки: {esc(e)}")


@dp.callback_query(F.data.startswith("dl:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def deals_navigation(call: CallbackQuery):
    if acc is None:
        return await call.answer("PlayerOK ещё не подключён", show_alert=True)
    parts = call.data.split(":", 2)
    action = parts[1] if len(parts) > 1 else ""
    state = deals_state.setdefault(call.from_user.id, {"filter": "all", "index": 0})
    force = False
    if action == "f" and len(parts) > 2 and parts[2] in DEAL_GROUPS:
        state["filter"] = parts[2]
        state["index"] = 0
    elif action == "n":
        state["index"] = state.get("index", 0) + 1
    elif action == "p":
        state["index"] = max(0, state.get("index", 0) - 1)
    elif action == "r":
        state["index"] = 0
        force = True
    await call.answer()
    try:
        text, markup = await render_deals(call.from_user.id, force=force)
        await call.message.edit_text(text, reply_markup=markup)
    except Exception as e:
        if "not modified" not in str(e).lower():
            log.exception("deals navigation")
            await call.message.answer(f"Не удалось загрузить сделки: {esc(e)}")


def summarize_deals(deals) -> dict:
    result = {"count": 0, "sum": 0.0, "done": [0, 0.0], "work": [0, 0.0], "refund": [0, 0.0], "problem": 0}
    for deal in deals:
        price, key = deal_price(deal), deal_status_key(deal)
        result["count"] += 1
        result["sum"] += price
        bucket = "done" if key in DONE_STATUSES else "work" if key in WORK_STATUSES else "refund" if key in REFUND_STATUSES else None
        if bucket:
            result[bucket][0] += 1
            result[bucket][1] += price
        if getattr(deal, "has_problem", False) and key not in REFUND_STATUSES:
            result["problem"] += 1
    return result


def stats_text(deals, now: datetime | None = None) -> str:
    now = now or datetime.now(LOCAL_TZ)
    periods = [
        ("Сегодня", now.replace(hour=0, minute=0, second=0, microsecond=0)),
        ("За 7 дней", now - timedelta(days=7)),
        ("За 30 дней", now - timedelta(days=30)),
        ("За всё время", None),
    ]
    blocks = [
        "📊 <b>Статистика продаж</b>\n"
        f"<i>Считается по дате создания сделки, время UTC{TZ_OFFSET_HOURS:+g}. "
        "Суммы — по цене лотов, без учёта комиссии.</i>"
    ]
    # Дату каждой сделки разбираем один раз, а не по разу на каждый из 4 периодов.
    dated = []
    for deal in deals:
        created = parse_dt(getattr(deal, "created_at", None))
        dated.append((deal, created.astimezone(LOCAL_TZ) if created is not None else None))
    for title, since in periods:
        selected = [deal for deal, created in dated if since is None or (created is not None and created >= since)]
        s = summarize_deals(selected)
        lines = [
            f"<b>{title}</b>",
            f"Сделок: {s['count']} на {fmt_money(s['sum'])} ₽",
            f"✅ Завершено: {s['done'][0]} ({fmt_money(s['done'][1])} ₽)",
            f"⏳ В работе: {s['work'][0]} ({fmt_money(s['work'][1])} ₽)",
            f"↩️ Возвраты: {s['refund'][0]} ({fmt_money(s['refund'][1])} ₽)",
        ]
        if s["problem"]:
            lines.append(f"⚠️ С проблемой: {s['problem']}")
        blocks.append("\n".join(lines))
    return "\n\n".join(blocks)


def stats_keyboard() -> InlineKeyboardMarkup:
    return InlineKeyboardMarkup(inline_keyboard=[[InlineKeyboardButton(text="🔄 Обновить", callback_data="st:r")]])


@dp.message(F.text == MENU_STATS, F.func(is_admin))
@dp.message(Command("stats"), F.func(is_admin))
async def cmd_stats(message: Message):
    if acc is None:
        return await message.answer("PlayerOK ещё подключается. Попробуй чуть позже.")
    wait = await message.answer("⏳ Считаю статистику…")
    try:
        await wait.edit_text(stats_text(await load_all_deals()), reply_markup=stats_keyboard())
    except Exception as e:
        log.exception("stats")
        await wait.edit_text(f"Не удалось получить статистику: {esc(e)}")


@dp.callback_query(F.data == "st:r", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def stats_refresh(call: CallbackQuery):
    if acc is None:
        return await call.answer("PlayerOK ещё не подключён", show_alert=True)
    await call.answer("Обновляю…")
    try:
        await call.message.edit_text(stats_text(await load_all_deals(force=True)), reply_markup=stats_keyboard())
    except Exception as e:
        if "not modified" not in str(e).lower():
            log.exception("stats refresh")
            await call.message.answer(f"Не удалось получить статистику: {esc(e)}")


# ---------- Telegram -> Playerok ----------

@dp.message(Command("start"), F.func(is_admin))
async def cmd_start(message: Message):
    await message.answer(
        "🚀 <b>Панель управления PlayerOK</b>\n\n"
        "💬 Чаты — просмотр переписок и фильтры событий.\n"
        "📋 Сделки — список ваших сделок и их статусы.\n"
        "📊 Статистика — за сегодня, 7 дней, 30 дней и всё время.\n"
        "🔔 Уведомления — что присылать, а что игнорировать.\n"
        "✍️ Автоответы — добавление, изменение и удаление шаблонов.\n"
        "🎯 Ответы по товарам — свой автоответ под каждый товар.\n"
        "🛍 Мои товары — создание, публикация и редактирование объявлений PlayerOK.\n"
        "⭐ Избранное — быстрый доступ к сохранённым объявлениям.\n"
        "📋 Пресеты продаж — повторное создание объявлений по шаблонам.\n"
        "💰 Баланс — информация о средствах.\n\n"
        "Ответьте Reply на уведомление покупателя, чтобы написать ему в PlayerOK.",
        reply_markup=main_kb,
    )


@dp.message(F.text == MENU_CHATS, F.func(is_admin))
@dp.message(Command("chats"), F.func(is_admin))
async def cmd_chats(message: Message):
    await show_chats(message, reset=True)


@dp.callback_query(F.data.startswith("chats:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def chats_navigation(call: CallbackQuery):
    if acc is None:
        return await call.answer("PlayerOK ещё не подключён", show_alert=True)
    action = call.data.split(":", 1)[1]
    admin_id = call.from_user.id
    state = chat_page_state.setdefault(admin_id, {"cursors": [None], "index": 0, "filter": "all"})
    state.setdefault("filter", "all")
    if action.startswith("filter:"):
        filter_key = action.split(":", 1)[1]
        if filter_key in CHAT_FILTERS:
            state["filter"] = filter_key
        state["cursors"] = [None]
        state["index"] = 0
    elif action in ("back", "refresh"):
        state["index"] = 0
        state["all_chats"] = None
    elif action == "next":
        state["index"] = state.get("index", 0) + 1
    elif action == "prev":
        state["index"] = max(0, state.get("index", 0) - 1)
    await call.answer()
    try:
        await call.message.delete()
    except Exception:
        pass
    await show_chats(call.message, reset=False)


@dp.callback_query(F.data.startswith("open:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def open_chat_callback(call: CallbackQuery):
    chat_id = call.data.split(":", 1)[1]
    await call.answer()
    try:
        await call.message.delete()
    except Exception:
        pass
    await show_chat(call.message, chat_id)


@dp.callback_query(F.data.startswith("write:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def begin_chat_message(call: CallbackQuery):
    chat_id = call.data.split(":", 1)[1]
    pending_chat_message[call.from_user.id] = chat_id
    await call.answer()
    await call.message.answer(
        "✍️ Напиши следующим сообщением текст для отправки в этот чат PlayerOK.\n"
        "Для отмены нажми кнопку ниже.",
        reply_markup=InlineKeyboardMarkup(inline_keyboard=[
            [InlineKeyboardButton(text="❌ Отмена", callback_data="write_cancel")]
        ]),
    )


@dp.callback_query(F.data == "write_cancel", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def cancel_chat_message(call: CallbackQuery):
    pending_chat_message.pop(call.from_user.id, None)
    draft_chat_message.pop(call.from_user.id, None)
    await call.answer("Отменено")
    try:
        await call.message.edit_text("Отправка сообщения отменена.")
    except Exception:
        pass


@dp.message(F.text, F.func(lambda m: m.from_user is not None and m.from_user.id == ADMIN_ID and m.from_user.id in pending_chat_message and not _is_listing_text_stage(m.from_user.id)))
async def prepare_custom_chat_message(message: Message):
    chat_id = pending_chat_message.pop(message.from_user.id, None)
    if not chat_id:
        return
    draft_chat_message[message.from_user.id] = message.text
    # Сохраняем выбранный чат до подтверждения отправки.
    pending_chat_message[message.from_user.id] = chat_id
    await message.answer(
        f"Отправить это сообщение в выбранный чат PlayerOK?\n\n{esc(message.text)}",
        reply_markup=InlineKeyboardMarkup(inline_keyboard=[
            [InlineKeyboardButton(text="✅ Отправить", callback_data="send_draft")],
            [InlineKeyboardButton(text="❌ Отмена", callback_data="write_cancel")],
        ]),
    )


@dp.callback_query(F.data == "send_draft", F.func(lambda c: c.from_user.id == ADMIN_ID))
async def send_draft_chat_message(call: CallbackQuery):
    chat_id = pending_chat_message.pop(call.from_user.id, None)
    text = draft_chat_message.pop(call.from_user.id, None)
    if not chat_id or text is None:
        return await call.answer("Нет сообщения для отправки", show_alert=True)
    if acc is None:
        await call.answer("PlayerOK ещё не подключён", show_alert=True)
        return
    try:
        await asyncio.to_thread(acc.send_message, chat_id=chat_id, text=text)
        await call.answer("Отправлено ✅")
        await call.message.edit_text("✅ Сообщение отправлено в выбранный чат PlayerOK.")
    except Exception as e:
        log.exception("custom chat message")
        await call.answer("Ошибка отправки", show_alert=True)
        await call.message.answer(f"❌ Не удалось отправить сообщение: {esc(e)}")

@dp.callback_query(F.data.startswith("q:"), F.func(lambda c: c.from_user.id == ADMIN_ID))
async def quick_reply(call: CallbackQuery):
    try:
        _, idx, chat_id = call.data.split(":", 2)
        if acc is None:
            return await call.answer("PlayerOK ещё не подключён", show_alert=True)
        text = AUTOREPLIES[int(idx)]
        await asyncio.to_thread(acc.send_message, chat_id=chat_id, text=text)
        await call.answer("Отправлено ✅")
        await call.message.reply(f"✅ Отправлено покупателю:\n{esc(text)}")
    except Exception as e:
        log.exception("quick_reply")
        await call.answer("Ошибка отправки", show_alert=True)
        await call.message.reply(f"Не отправилось: {esc(e)}")


@dp.message(F.text == MENU_BALANCE, F.func(is_admin))
@dp.message(Command("balance"), F.func(is_admin))
async def cmd_balance(message: Message):
    if acc is None:
        return await message.answer("PlayerOK ещё не подключён.")
    try:
        profile = await asyncio.to_thread(lambda: acc.get().profile)
        b = profile.balance
        await message.answer(
            f"💰 Баланс: <b>{b.value}</b> ₽\n"
            f"Доступно: {b.available} ₽\nЗаморожено: {b.frozen} ₽\n"
            f"К выводу: {b.withdrawable} ₽\nОжидается: {b.pending_income} ₽"
        )
    except Exception as e:
        log.exception("balance")
        await message.answer(f"Не удалось получить баланс: {esc(e)}")


@dp.message(F.reply_to_message, F.text, F.func(is_admin))
async def reply_to_buyer(message: Message):
    chat_id = reply_map.get(message.reply_to_message.message_id)
    if not chat_id:
        return await message.answer("Не нашёл чат PlayerOK для этого сообщения. Ответьте на свежее уведомление.")
    if acc is None:
        return await message.answer("PlayerOK ещё не подключён.")
    try:
        await asyncio.to_thread(acc.send_message, chat_id=chat_id, text=message.text)
        await message.answer("✅ Отправлено на PlayerOK")
    except Exception as e:
        log.exception("send_message")
        await message.answer(f"Не отправилось: {esc(e)}")


async def main():
    loop = asyncio.get_running_loop()
    log.info(
        "Запуск экземпляра бота: файл=%s pid=%s; файл настроек уведомлений=%s; настройки=%s",
        os.path.abspath(__file__), os.getpid(), os.path.abspath(NOTIFY_FILE), NOTIFY_SETTINGS,
    )
    threading.Thread(target=listener_thread, args=(loop,), daemon=True).start()
    threading.Thread(target=balance_watch_thread, args=(loop,), daemon=True).start()
    try:
        await _send_to_admin("🚀 Бот запущен", None)
    except Exception:
        # Раньше сбой этого сообщения (сеть, прокси) ронял весь бот ещё до начала работы.
        log.exception("Не удалось отправить приветственное сообщение; продолжаю запуск")
    try:
        await dp.start_polling(bot)
    finally:
        await bot.session.close()


if __name__ == "__main__":
    asyncio.run(main())