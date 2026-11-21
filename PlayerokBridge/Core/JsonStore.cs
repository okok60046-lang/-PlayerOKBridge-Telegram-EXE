using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlayerokBridge.Core;

/// <summary>Атомарное чтение/запись JSON: сначала временный файл, потом замена оригинала.</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // кириллица и эмодзи читаемыми
        PropertyNameCaseInsensitive = true,
    };

    public static string DataDir { get; private set; } = "";

    public static void Init(string? overrideDir = null)
    {
        DataDir = overrideDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PlayerokBridge");
        Directory.CreateDirectory(DataDir);
        SeedProductReplies();
    }

    public static string PathOf(string fileName) => Path.Combine(DataDir, fileName);

    // На первом запуске переносим русские и остальные шаблоны из комплекта в
    // пользовательскую папку. Уже существующие ответы не перезаписываем.
    private static void SeedProductReplies()
    {
        var marker = PathOf(".product_replies_seed_v1");
        if (File.Exists(marker)) return;

        var samplePath = Path.Combine(AppContext.BaseDirectory, "data-sample", "product_replies.json");
        if (!File.Exists(samplePath)) return;

        try
        {
            var sampleNode = JsonNode.Parse(File.ReadAllText(samplePath));
            if (sampleNode is not JsonArray sample) return;

            var targetPath = PathOf("product_replies.json");
            JsonArray existing;
            if (File.Exists(targetPath))
            {
                var existingNode = JsonNode.Parse(File.ReadAllText(targetPath));
                if (existingNode is not JsonArray parsed) return; // не затираем повреждённый/неожиданный файл
                existing = parsed;
            }
            else
            {
                existing = new JsonArray();
            }

            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in existing)
            {
                var keyword = item?["keyword"]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(keyword)) keywords.Add(keyword);
            }

            foreach (var item in sample)
            {
                var keyword = item?["keyword"]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(keyword) && keywords.Add(keyword))
                    existing.Add(item?.DeepClone());
            }

            File.WriteAllText(targetPath, existing.ToJsonString(Options));
            File.WriteAllText(marker, "v1");
        }
        catch
        {
            // Ошибка импорта не должна мешать запуску приложения; следующий запуск повторит попытку.
        }
    }

    public static T? Load<T>(string fileName)
    {
        var path = PathOf(fileName);
        if (!File.Exists(path)) return default;
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Options);
    }

    // Все записи идут через общий временный файл «имя.tmp»: без замка два потока, сохраняющие один файл
    // одновременно (монитор и окно), получали IOException «файл занят», и настройка не сохранялась.
    private static readonly object SaveLock = new();

    public static void Save<T>(string fileName, T data)
    {
        var path = PathOf(fileName);
        var temp = path + ".tmp";
        lock (SaveLock)
        {
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, data, Options);
                    stream.Flush(true);
                }
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch { /* ignore */ }
                throw;
            }
        }
    }
}
