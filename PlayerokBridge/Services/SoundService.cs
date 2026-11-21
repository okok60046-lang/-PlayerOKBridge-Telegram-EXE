using System.Runtime.InteropServices;
using System.Text;
using PlayerokBridge.Core;

namespace PlayerokBridge.Services;

public enum SoundKind { Purchase, Message, Notification }

/// <summary>
/// Звуки событий и фоновая музыка. Для событий есть встроенные синтезированные сигналы
/// и пользовательские аудиофайлы; Windows: MCI (winmm) играет короткие сигналы и выбранную музыку.
/// На других ОС воспроизведение ничего не делает.
/// </summary>
public sealed class SoundService : IDisposable
{
    public SoundSettings Settings { get; }

    private readonly object _lock = new();
    private readonly Dictionary<SoundKind, DateTime> _lastPlayed = new();
    private int _alias;
    private string? _theme;
    private int _musicVersion;
    private IntPtr _musicBuffer = IntPtr.Zero;
    private short[]? _musicSamples;
    private string? _musicSamplesKey;
    private bool _customMusicOpen;
    private string? _customMusicPath;
    private bool _disposed;
    private volatile bool _musicSuspended; // окно свёрнуто: музыка на паузе
    private volatile bool _musicClosed; // приложение закрывается: музыку больше не запускаем

    public SoundService(SoundSettings settings) => Settings = settings;

    public static bool Supported => OperatingSystem.IsWindows();

    /// <summary>Описание музыки для каждого фона (ключ — ключ шаблона фона).</summary>
    public static readonly IReadOnlyDictionary<string, string> MusicTitles = new Dictionary<string, string>
    {
        ["lava"] = "тёплый ambient с мягкими колокольчиками",
        ["flow"] = "бодрое переливающееся арпеджио",
        ["sunset"] = "неспешный закатный лаундж",
        ["fire"] = "тёмный ритмичный драйв с басом",
        ["snow"] = "зимняя музыкальная шкатулка",
        ["bubbles"] = "водяные «пузырьки» на мягком фоне",
        ["aurora"] = "эфирные длинные пэды и мерцание",
        ["stars"] = "глубокий космос и редкие звёзды",
        ["solid"] = "спокойный минимализм",
        ["neon"] = "синтвейв с мягким басом и неоновым арпеджио",
        ["rain"] = "дождливый lo-fi с тёплыми клавишами",
        ["dream"] = "воздушный dream-ambient с длинными переливами",
    };

    // ---------- Звуки событий ----------

    /// <summary>
    /// Проигрывает звук события. force = true — для кнопок проверки (игнорирует выключатели).
    /// Возвращает false, если звук не проигран.
    /// </summary>
    public bool Play(SoundKind kind, bool force = false)
    {
        if (!Supported || _disposed) return false;
        if (!force)
        {
            if (!Settings.Enabled) return false;
            var on = kind switch
            {
                SoundKind.Purchase => Settings.Purchase,
                SoundKind.Message => Settings.Message,
                _ => Settings.Notification,
            };
            if (!on) return false;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                // Защита от «очереди» одинаковых звуков, когда события приходят пачкой.
                if (_lastPlayed.TryGetValue(kind, out var last) && now - last < TimeSpan.FromMilliseconds(150)) return false;
                _lastPlayed[kind] = now;
            }
        }
        var volume = Math.Clamp(Settings.Volume, 0, 100);
        if (volume == 0) return false;
        var customPath = kind switch
        {
            SoundKind.Purchase => Settings.PurchaseSoundPath,
            SoundKind.Message => Settings.MessageSoundPath,
            _ => Settings.NotificationSoundPath,
        };
        var path = !string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath)
            ? customPath
            : EnsureFile(kind);
        _ = Task.Run(() => PlayFile(path, volume));
        return true;
    }

    private static string? EnsureFile(SoundKind kind)
    {
        try
        {
            var dir = Path.Combine(JsonStore.DataDir, "sounds");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{kind.ToString().ToLowerInvariant()}_v1.wav");
            if (!File.Exists(path))
            {
                var wav = Synth.WrapWav(Synth.Sfx(kind), Synth.SfxRate, 1.0);
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, wav);
                File.Move(temp, path, overwrite: true);
            }
            return path;
        }
        catch { return null; }
    }

    private void PlayFile(string? path, int volume)
    {
        if (path is null) return;
        var alias = "pbsfx" + Interlocked.Increment(ref _alias);
        try
        {
            if (!OpenMciFile(path, alias)) return;
            Mci($"setaudio {alias} volume to {volume * 10}");
            Mci($"play {alias} wait");
        }
        catch { /* нет звуковой подсистемы — молча пропускаем */ }
        finally
        {
            try { Mci($"close {alias}"); } catch { }
        }
    }

    private static int Mci(string command) => mciSendString(command, null, 0, IntPtr.Zero);

    private static string MciDeviceType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".wav" => "waveaudio",
        ".mid" or ".midi" => "sequencer",
        ".mp3" or ".wma" => "mpegvideo",
        _ => "waveaudio",
    };

    private static bool OpenMciFile(string path, string alias)
    {
        if (Mci($"open \"{path}\" type {MciDeviceType(path)} alias {alias}") == 0) return true;
        // Запасной вариант: пусть Windows выберет MCI-устройство по расширению файла.
        return Mci($"open \"{path}\" alias {alias}") == 0;
    }

    // ---------- Фоновая музыка ----------

    /// <summary>Выбирает музыку под фон (ключ шаблона) и запускает её, если музыка включена.</summary>
    public void SetTheme(string themeKey)
    {
        _theme = themeKey;
        ApplyMusic();
    }

    /// <summary>Применяет текущие настройки музыки (вкл/выкл, громкость, тема).</summary>
    public void ApplyMusic()
    {
        if (!Supported || _disposed || _musicClosed) return;
        var version = Interlocked.Increment(ref _musicVersion);
        var key = _theme;
        var volume = Math.Clamp(Settings.MusicVolume, 0, 100);
        var customPath = Settings.CustomMusicPath;
        if (!Settings.Music || _musicSuspended || volume == 0)
        {
            StopMusic();
            return;
        }

        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            StartCustomMusic(customPath, volume, version);
            return;
        }

        StopCustomMusic();
        if (key is null)
        {
            StopMusic();
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                short[]? samples;
                lock (_lock) samples = _musicSamplesKey == key ? _musicSamples : null;
                if (samples is null)
                {
                    samples = Synth.Music(key);
                    lock (_lock) { _musicSamples = samples; _musicSamplesKey = key; }
                }
                if (version != Volatile.Read(ref _musicVersion)) return; // уже выбрали другую тему / выключили
                StartLoop(samples, volume, version);
            }
            catch { /* музыка не должна ронять приложение */ }
        });
    }

    /// <summary>Запускает выбранный пользователем трек в цикле средствами Windows MCI.</summary>
    private void StartCustomMusic(string path, int volume, int version)
    {
        _ = Task.Run(() =>
        {
            lock (_lock)
            {
                if (_disposed || _musicClosed || _musicSuspended || version != Volatile.Read(ref _musicVersion)) return;
                try
                {
                    if (_customMusicOpen && string.Equals(_customMusicPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        // При движении ползунка громкости не перезапускаем трек с начала.
                        Mci($"setaudio pb_custom_music volume to {Math.Clamp(volume, 0, 100) * 10}");
                        return;
                    }

                    StopPcmMusicLocked();
                    StopCustomMusicLocked();
                    if (!OpenMciFile(path, "pb_custom_music")) return;
                    _customMusicOpen = true;
                    _customMusicPath = path;
                    Mci($"setaudio pb_custom_music volume to {Math.Clamp(volume, 0, 100) * 10}");
                    if (Mci("play pb_custom_music repeat") != 0)
                        StopCustomMusicLocked();
                }
                catch
                {
                    StopCustomMusicLocked();
                }
            }
        });
    }

    private void StopCustomMusic()
    {
        if (!Supported) return;
        lock (_lock) StopCustomMusicLocked();
    }

    private void StopCustomMusicLocked()
    {
        if (!_customMusicOpen) return;
        try { Mci("stop pb_custom_music"); } catch { }
        try { Mci("close pb_custom_music"); } catch { }
        _customMusicOpen = false;
        _customMusicPath = null;
    }

    private void StopPcmMusicLocked()
    {
        try { PlaySound(IntPtr.Zero, IntPtr.Zero, 0); } catch { }
        if (_musicBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_musicBuffer);
            _musicBuffer = IntPtr.Zero;
        }
    }

    private void StartLoop(short[] samples, int volume, int version)
    {
        var gain = Math.Pow(volume / 100.0, 1.5); // ближе к восприятию громкости, чем линейная шкала
        var wav = Synth.WrapWav(samples, Synth.MusicRate, gain);
        var ptr = Marshal.AllocHGlobal(wav.Length);
        Marshal.Copy(wav, 0, ptr, wav.Length);
        lock (_lock)
        {
            if (_disposed || _musicClosed || version != Volatile.Read(ref _musicVersion))
            {
                Marshal.FreeHGlobal(ptr);
                return;
            }
            var ok = PlaySound(ptr, IntPtr.Zero, SndMemory | SndAsync | SndLoop | SndNoDefault);
            var old = _musicBuffer;
            _musicBuffer = ok ? ptr : IntPtr.Zero;
            if (!ok) Marshal.FreeHGlobal(ptr);
            if (old != IntPtr.Zero) Marshal.FreeHGlobal(old); // новая запись уже заменила старую
        }
    }

    /// <summary>Пауза музыки, пока окно свёрнуто; при разворачивании музыка продолжается. Звуки событий не затрагиваются.</summary>
    public void SuspendMusic(bool suspended)
    {
        if (_musicSuspended == suspended) return;
        _musicSuspended = suspended;
        ApplyMusic();
    }

    /// <summary>Останавливает музыку при закрытии приложения и не даёт ей запуститься снова. Звуки событий продолжают работать.</summary>
    public void StopMusicForExit()
    {
        _musicClosed = true;
        Interlocked.Increment(ref _musicVersion); // отменяет музыку, которая ещё генерируется в фоне
        StopMusic();
    }

    public void StopMusic()
    {
        if (!Supported) return;
        lock (_lock)
        {
            StopPcmMusicLocked();
            StopCustomMusicLocked();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _musicVersion);
        StopMusic();
    }

    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;
    private const uint SndLoop = 0x0008;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnValue, int returnLength, IntPtr callback);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}

/// <summary>Синтезатор: короткие звуки и зацикленные музыкальные темы (моно, 16 бит).</summary>
internal static class Synth
{
    public const int SfxRate = 44100;
    public const int MusicRate = 22050;
    private const double Tau = Math.PI * 2;

    // ---------- WAV ----------

    public static byte[] WrapWav(short[] pcm, int rate, double gain)
    {
        var dataLen = pcm.Length * 2;
        var data = new byte[44 + dataLen];
        void W32(int o, int v) { data[o] = (byte)v; data[o + 1] = (byte)(v >> 8); data[o + 2] = (byte)(v >> 16); data[o + 3] = (byte)(v >> 24); }
        void W16(int o, int v) { data[o] = (byte)v; data[o + 1] = (byte)(v >> 8); }
        Encoding.ASCII.GetBytes("RIFF").CopyTo(data, 0);
        W32(4, 36 + dataLen);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(data, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(data, 12);
        W32(16, 16); W16(20, 1); W16(22, 1); W32(24, rate); W32(28, rate * 2); W16(32, 2); W16(34, 16);
        Encoding.ASCII.GetBytes("data").CopyTo(data, 36);
        W32(40, dataLen);
        for (var i = 0; i < pcm.Length; i++)
        {
            var v = (int)Math.Round(pcm[i] * gain);
            if (v > short.MaxValue) v = short.MaxValue;
            if (v < short.MinValue) v = short.MinValue;
            data[44 + i * 2] = (byte)v;
            data[45 + i * 2] = (byte)(v >> 8);
        }
        return data;
    }

    private static short[] ToPcm(float[] buf, double peak)
    {
        float max = 0;
        foreach (var s in buf) { var a = Math.Abs(s); if (a > max) max = a; }
        var k = max > 1e-6f ? peak * 32767.0 / max : 0;
        var pcm = new short[buf.Length];
        for (var i = 0; i < buf.Length; i++) pcm[i] = (short)Math.Clamp(Math.Round(buf[i] * k), short.MinValue, short.MaxValue);
        return pcm;
    }

    // ---------- Инструменты: функция времени (сек) -> значение ----------

    private static double Hz(double midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);

    private static Func<double, double> Bell(double f, double decay) => t =>
    {
        var env = Math.Min(1.0, t / 0.004);
        var w = Math.Sin(Tau * f * t) * Math.Exp(-t * decay)
              + 0.45 * Math.Sin(Tau * 2.01 * f * t) * Math.Exp(-t * decay * 1.8)
              + 0.20 * Math.Sin(Tau * 3.0 * f * t) * Math.Exp(-t * decay * 2.6)
              + 0.10 * Math.Sin(Tau * 4.2 * f * t) * Math.Exp(-t * decay * 3.5);
        return env * w;
    };

    private static Func<double, double> Pluck(double f, double decay) => t =>
    {
        var env = Math.Min(1.0, t / 0.003) * Math.Exp(-t * decay);
        return env * (Math.Sin(Tau * f * t) + 0.5 * Math.Sin(Tau * 2 * f * t) + 0.25 * Math.Sin(Tau * 3 * f * t) + 0.1 * Math.Sin(Tau * 4 * f * t));
    };

    private static Func<double, double> Pad(double f, double dur, double att, double rel) => t =>
    {
        var env = Math.Min(Math.Min(t / att, 1.0), Math.Max(0.0, (dur - t) / rel));
        return env * (Math.Sin(Tau * f * t) + 0.6 * Math.Sin(Tau * f * 1.004 * t) + 0.18 * Math.Sin(Tau * 2 * f * t));
    };

    private static Func<double, double> Blip(double f) => t =>
    {
        // Частота быстро растёт на 0.09 с: «пузырёк».
        const double sweep = 0.09;
        var phase = f * (t + 0.6 * (t < sweep ? t * t / (2 * sweep) : t - sweep / 2));
        return Math.Min(1.0, t / 0.003) * Math.Exp(-t * 14) * Math.Sin(Tau * phase);
    };

    private static Func<double, double> Thump() => t =>
    {
        var phase = 40 * t + 45 * (1 - Math.Exp(-18 * t)) / 18;
        return Math.Exp(-t * 7) * Math.Sin(Tau * phase);
    };

    private static void Add(float[] buf, int rate, double start, double dur, double amp, Func<double, double> f, bool wrap)
    {
        var n = (int)(dur * rate);
        var len = buf.Length;
        var s0 = (int)Math.Round(start * rate);
        if (wrap) s0 = ((s0 % len) + len) % len;
        for (var i = 0; i < n; i++)
        {
            var idx = s0 + i;
            if (idx >= len)
            {
                if (!wrap) break;
                idx %= len;
            }
            if (idx < 0) continue;
            buf[idx] += (float)(amp * f(i / (double)rate));
        }
    }

    // ---------- Звуки событий ----------

    public static short[] Sfx(SoundKind kind)
    {
        const int r = SfxRate;
        switch (kind)
        {
            case SoundKind.Message:
            {
                // Один и тот же звук для входящих и исходящих: короткое «поп-динь» вверх.
                var b = new float[(int)(0.75 * r)];
                Add(b, r, 0.00, 0.55, 0.9, Bell(880.0, 7), false);
                Add(b, r, 0.09, 0.60, 0.8, Bell(1318.51, 7), false);
                return ToPcm(b, 0.8);
            }
            case SoundKind.Purchase:
            {
                // Монетка и восходящее искристое арпеджио с аккордом на конце — «покупка!».
                var b = new float[(int)(1.9 * r)];
                Add(b, r, 0.00, 0.45, 0.45, Bell(523.25, 6), false);
                Add(b, r, 0.00, 0.60, 0.80, Bell(1046.50, 5), false);
                Add(b, r, 0.10, 0.60, 0.80, Bell(1318.51, 5), false);
                Add(b, r, 0.20, 0.70, 0.80, Bell(1567.98, 4.5), false);
                Add(b, r, 0.32, 1.50, 0.90, Bell(2093.00, 2.6), false);
                Add(b, r, 0.32, 1.50, 0.45, Bell(1567.98, 2.6), false);
                Add(b, r, 0.32, 1.50, 0.35, Bell(1318.51, 2.6), false);
                return ToPcm(b, 0.85);
            }
            default:
            {
                // Уведомление: мягкое «динь-донг» вниз.
                var b = new float[(int)(1.3 * r)];
                Add(b, r, 0.00, 0.90, 0.85, Bell(1318.51, 4.5), false);
                Add(b, r, 0.22, 1.05, 0.90, Bell(1046.50, 3.5), false);
                Add(b, r, 0.22, 1.05, 0.35, Bell(523.25, 3.5), false);
                return ToPcm(b, 0.8);
            }
        }
    }

    // ---------- Музыкальные темы ----------

    private sealed class Theme
    {
        public int Root = 50;                 // MIDI-нота основания
        public int[][] Chords = Array.Empty<int[]>();
        public double BarSec = 8;
        public int Repeat = 1;                // сколько раз повторить прогрессию в петле
        public double PadAmp = 0.5, BassAmp = 0.5;
        public string ArpInst = "none";       // pluck | bell | none
        public int ArpPerBar;
        public double ArpAmp = 0.4, ArpDecay = 4;
        public int ArpOct = 1;
        public string MelodyInst = "bell";    // bell | pluck | blip
        public int MelodySlots = 8;
        public double MelodyProb, MelodyAmp = 0.3;
        public int MelodyOct = 2;
        public int[] Scale = { 0, 2, 4, 7, 9 };
        public double Pulse;                  // громкость «ударов» (огонь)
        public int Seed = 1;
    }

    private static readonly int[] Min7 = { 0, 3, 7, 10 }, Maj7 = { 0, 4, 7, 11 }, Dom7 = { 0, 4, 7, 10 },
        Min = { 0, 3, 7, 12 }, Maj = { 0, 4, 7, 12 }, Add9 = { 0, 4, 7, 14 }, Min9 = { 0, 3, 7, 14 }, Sus = { 0, 7, 12, 14 };

    private static int[] Ch(int root, int[] intervals) => intervals.Select(i => root + i).ToArray();

    private static readonly int[] MinPent = { 0, 3, 5, 7, 10 }, MajPent = { 0, 2, 4, 7, 9 }, HarmMinor = { 0, 2, 3, 5, 7, 8, 11 };

    private static readonly Dictionary<string, Theme> Themes = new()
    {
        ["lava"] = new Theme
        {
            Root = 50, Chords = new[] { Ch(0, Min9), Ch(8, Maj7), Ch(3, Maj7), Ch(10, Dom7) }, BarSec = 8,
            PadAmp = 0.5, BassAmp = 0.55, ArpInst = "pluck", ArpPerBar = 4, ArpAmp = 0.45, ArpDecay = 3.5, ArpOct = 1,
            MelodyInst = "bell", MelodySlots = 8, MelodyProb = 0.3, MelodyAmp = 0.35, MelodyOct = 2, Scale = MinPent, Seed = 11,
        },
        ["flow"] = new Theme
        {
            Root = 48, Chords = new[] { Ch(0, Add9), Ch(7, Maj), Ch(9, Min7), Ch(5, Maj7) }, BarSec = 4, Repeat = 2,
            PadAmp = 0.35, BassAmp = 0.5, ArpInst = "pluck", ArpPerBar = 8, ArpAmp = 0.45, ArpDecay = 5, ArpOct = 1,
            MelodyInst = "bell", MelodySlots = 8, MelodyProb = 0.3, MelodyAmp = 0.3, MelodyOct = 2, Scale = MajPent, Seed = 21,
        },
        ["sunset"] = new Theme
        {
            Root = 53, Chords = new[] { Ch(0, Maj7), Ch(4, Min7), Ch(5, Maj7), Ch(7, Dom7) }, BarSec = 8,
            PadAmp = 0.6, BassAmp = 0.5, ArpInst = "bell", ArpPerBar = 4, ArpAmp = 0.35, ArpDecay = 1.6, ArpOct = 1,
            MelodyInst = "bell", MelodySlots = 8, MelodyProb = 0.22, MelodyAmp = 0.3, MelodyOct = 2, Scale = MajPent, Seed = 31,
        },
        ["fire"] = new Theme
        {
            Root = 45, Chords = new[] { Ch(0, Min), Ch(8, Maj), Ch(10, Maj), Ch(0, Min) }, BarSec = 2, Repeat = 4,
            PadAmp = 0.3, BassAmp = 0.75, ArpInst = "pluck", ArpPerBar = 8, ArpAmp = 0.4, ArpDecay = 7, ArpOct = 1,
            MelodyInst = "pluck", MelodySlots = 8, MelodyProb = 0.15, MelodyAmp = 0.3, MelodyOct = 2, Scale = HarmMinor,
            Pulse = 0.8, Seed = 41,
        },
        ["snow"] = new Theme
        {
            Root = 60, Chords = new[] { Ch(0, Maj7), Ch(9, Min7), Ch(5, Maj7), Ch(7, Sus) }, BarSec = 4, Repeat = 2,
            PadAmp = 0.25, BassAmp = 0.3, ArpInst = "bell", ArpPerBar = 4, ArpAmp = 0.4, ArpDecay = 2.2, ArpOct = 1,
            MelodyInst = "bell", MelodySlots = 8, MelodyProb = 0.25, MelodyAmp = 0.3, MelodyOct = 1, Scale = MajPent, Seed = 51,
        },
        ["bubbles"] = new Theme
        {
            Root = 53, Chords = new[] { Ch(0, Add9), Ch(7, Maj), Ch(9, Min7), Ch(5, Add9) }, BarSec = 8,
            PadAmp = 0.4, BassAmp = 0.3, ArpInst = "none", ArpPerBar = 0,
            MelodyInst = "blip", MelodySlots = 16, MelodyProb = 0.45, MelodyAmp = 0.35, MelodyOct = 1, Scale = MajPent, Seed = 61,
        },
        ["aurora"] = new Theme
        {
            Root = 45, Chords = new[] { Ch(0, Sus), Ch(5, Sus), Ch(3, Sus), Ch(7, Sus) }, BarSec = 8,
            PadAmp = 0.7, BassAmp = 0.4, ArpInst = "none", ArpPerBar = 0,
            MelodyInst = "bell", MelodySlots = 16, MelodyProb = 0.12, MelodyAmp = 0.3, MelodyOct = 3, Scale = MajPent, Seed = 71,
        },
        ["stars"] = new Theme
        {
            Root = 40, Chords = new[] { Ch(0, new[] { 0, 7, 12, 15 }), Ch(8, Maj7), Ch(5, new[] { 0, 7, 12, 14 }), Ch(7, Sus) }, BarSec = 8,
            PadAmp = 0.6, BassAmp = 0.55, ArpInst = "none", ArpPerBar = 0,
            MelodyInst = "bell", MelodySlots = 16, MelodyProb = 0.2, MelodyAmp = 0.28, MelodyOct = 3, Scale = MinPent, Seed = 81,
        },
        ["solid"] = new Theme
        {
            Root = 55, Chords = new[] { Ch(0, Maj7), Ch(5, Add9), Ch(9, Min7), Ch(7, Dom7) }, BarSec = 8,
            PadAmp = 0.5, BassAmp = 0.4, ArpInst = "pluck", ArpPerBar = 2, ArpAmp = 0.35, ArpDecay = 3, ArpOct = 1,
            MelodyProb = 0, Seed = 91,
        },
        ["neon"] = new Theme
        {
            Root = 48, Chords = new[] { Ch(0, Min7), Ch(3, Maj7), Ch(8, Add9), Ch(5, Dom7) }, BarSec = 4, Repeat = 3,
            PadAmp = 0.38, BassAmp = 0.62, ArpInst = "pluck", ArpPerBar = 8, ArpAmp = 0.52, ArpDecay = 4.2, ArpOct = 2,
            MelodyInst = "blip", MelodySlots = 16, MelodyProb = 0.32, MelodyAmp = 0.22, MelodyOct = 2, Scale = MinPent, Seed = 101,
        },
        ["rain"] = new Theme
        {
            Root = 50, Chords = new[] { Ch(0, Min9), Ch(5, Maj7), Ch(3, Add9), Ch(10, Min7) }, BarSec = 8, Repeat = 2,
            PadAmp = 0.55, BassAmp = 0.28, ArpInst = "pluck", ArpPerBar = 4, ArpAmp = 0.22, ArpDecay = 7, ArpOct = 1,
            MelodyInst = "bell", MelodySlots = 8, MelodyProb = 0.16, MelodyAmp = 0.2, MelodyOct = 2, Scale = MinPent, Seed = 111,
        },
        ["dream"] = new Theme
        {
            Root = 45, Chords = new[] { Ch(0, Add9), Ch(7, Sus), Ch(5, Maj7), Ch(9, Min7) }, BarSec = 10, Repeat = 2,
            PadAmp = 0.72, BassAmp = 0.25, ArpInst = "bell", ArpPerBar = 3, ArpAmp = 0.24, ArpDecay = 3.8, ArpOct = 2,
            MelodyInst = "bell", MelodySlots = 16, MelodyProb = 0.18, MelodyAmp = 0.2, MelodyOct = 3, Scale = MajPent, Seed = 121,
        },
    };

    /// <summary>Рисует зацикленную музыкальную тему. Ноты «заворачиваются» через границу петли — стык не слышен.</summary>
    public static short[] Music(string key)
    {
        var th = Themes.TryGetValue(key, out var found) ? found : Themes["lava"];
        const int rate = MusicRate;
        var bars = th.Chords.Length * th.Repeat;
        var n = (int)Math.Round(bars * th.BarSec * rate);
        var dry = new float[n];
        var rnd = new Random(th.Seed);

        for (var bar = 0; bar < bars; bar++)
        {
            var chord = th.Chords[bar % th.Chords.Length];
            var t0 = bar * th.BarSec;
            var padDur = th.BarSec * 1.16;

            foreach (var tone in chord)
                Add(dry, rate, t0 - th.BarSec * 0.08, padDur, th.PadAmp * 0.5,
                    Pad(Hz(th.Root + tone + 12), padDur, th.BarSec * 0.3, th.BarSec * 0.45), true);

            Add(dry, rate, t0, th.BarSec * 1.02, th.BassAmp,
                Pad(Hz(th.Root + chord[0] - 12), th.BarSec * 1.02, 0.3, 0.6), true);

            if (th.ArpPerBar > 0 && th.ArpInst != "none")
            {
                var step = th.BarSec / th.ArpPerBar;
                var seq = PingPong(chord.Length);
                for (var k = 0; k < th.ArpPerBar; k++)
                {
                    var midi = th.Root + chord[seq[k % seq.Length]] + 12 * th.ArpOct;
                    var accent = k % 4 == 0 ? 1.0 : 0.75;
                    var f = Hz(midi);
                    Func<double, double> inst = th.ArpInst == "bell" ? Bell(f, th.ArpDecay) : Pluck(f, th.ArpDecay);
                    Add(dry, rate, t0 + k * step, Math.Min(2.0, step * 5), th.ArpAmp * accent, inst, true);
                }
            }

            if (th.MelodyProb > 0)
            {
                var step = th.BarSec / th.MelodySlots;
                for (var k = 0; k < th.MelodySlots; k++)
                {
                    if (rnd.NextDouble() >= th.MelodyProb) continue;
                    var note = th.Scale[rnd.Next(th.Scale.Length)];
                    var midi = th.Root + chord[0] + note + 12 * th.MelodyOct;
                    var f = Hz(midi);
                    Func<double, double> inst = th.MelodyInst switch
                    {
                        "blip" => Blip(f),
                        "pluck" => Pluck(f, 5),
                        _ => Bell(f, 2.4),
                    };
                    Add(dry, rate, t0 + k * step, 2.0, th.MelodyAmp, inst, true);
                }
            }

            if (th.Pulse > 0)
                for (var beat = 0; beat < 4; beat++)
                    Add(dry, rate, t0 + beat * th.BarSec / 4, 0.5, th.Pulse * (beat % 2 == 0 ? 1.0 : 0.6), Thump(), true);
        }

        // Лёгкое эхо (через границу петли), чтобы звучало объёмнее.
        var wet = new float[n];
        var taps = new[] { (Delay: (int)(0.29 * rate), Gain: 0.30f), (Delay: (int)(0.47 * rate), Gain: 0.20f), (Delay: (int)(0.71 * rate), Gain: 0.12f) };
        for (var i = 0; i < n; i++)
        {
            var v = dry[i];
            foreach (var (delay, gain) in taps) v += gain * dry[((i - delay) % n + n) % n];
            wet[i] = v;
        }
        return ToPcm(wet, 0.9);
    }

    private static int[] PingPong(int length)
    {
        if (length <= 2) return Enumerable.Range(0, Math.Max(1, length)).ToArray();
        var seq = new List<int>();
        for (var i = 0; i < length; i++) seq.Add(i);
        for (var i = length - 2; i >= 1; i--) seq.Add(i);
        return seq.ToArray();
    }
}
