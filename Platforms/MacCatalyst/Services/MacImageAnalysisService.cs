using System.Diagnostics;
using System.Text.Json;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacImageAnalysisService : IImageAnalysisService
{
    private static readonly Dictionary<string, (string TagType, string DisplayName)> ClassifierMap =
        BuildClassifierMap();

    public async Task<ImageAnalysisResult> AnalyzeImageAsync(
        string filePath,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return new ImageAnalysisResult();

        var helperPath = Path.Combine(AppContext.BaseDirectory, "MacExplorer.ImageAnalysis");
        if (!File.Exists(helperPath))
            return new ImageAnalysisResult();

        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(filePath);

        using var process = Process.Start(startInfo);
        if (process == null) return new ImageAnalysisResult();

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        var json = await outputTask;
        _ = await errorTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            return new ImageAnalysisResult();

        try
        {
            var native = JsonSerializer.Deserialize<NativeAnalysisResult>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (native == null) return new ImageAnalysisResult();

            return new ImageAnalysisResult
            {
                Faces = native.Faces.Select(face => new DetectedFace
                {
                    BoundingBoxX = face.BoundingBoxX,
                    BoundingBoxY = face.BoundingBoxY,
                    BoundingBoxW = face.BoundingBoxW,
                    BoundingBoxH = face.BoundingBoxH,
                    FeaturePrint = string.IsNullOrEmpty(face.FeaturePrint)
                        ? null
                        : Convert.FromBase64String(face.FeaturePrint)
                }).ToList(),
                RecognizedTexts = native.RecognizedTexts.Select(text => new RecognizedText
                {
                    Text = text.Text,
                    Confidence = text.Confidence,
                    Keywords = text.Keywords
                }).ToList(),
                Classifications = native.Classifications.Select(classification =>
                {
                    var mapped = ClassifierMap.GetValueOrDefault(
                        classification.Identifier,
                        ("object", classification.Identifier.Replace('_', ' ')));
                    return new ClassificationLabel
                    {
                        Identifier = classification.Identifier,
                        TagType = mapped.Item1,
                        DisplayName = mapped.Item2,
                        Confidence = classification.Confidence
                    };
                }).ToList(),
                Location = native.Location,
                DateInfo = native.DateInfo,
                CameraInfo = native.CameraInfo
            };
        }
        catch (JsonException)
        {
            return new ImageAnalysisResult();
        }
        catch (FormatException)
        {
            return new ImageAnalysisResult();
        }
    }

    public float ComputeFaceDistance(byte[] print1, byte[] print2)
    {
        if (print1.Length != print2.Length || print1.Length % sizeof(float) != 0)
            return float.MaxValue;

        var sum = 0f;
        for (var offset = 0; offset < print1.Length; offset += sizeof(float))
        {
            var difference = BitConverter.ToSingle(print1, offset)
                - BitConverter.ToSingle(print2, offset);
            sum += difference * difference;
        }
        return MathF.Sqrt(sum);
    }

    private static Dictionary<string, (string, string)> BuildClassifierMap()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        Add(map, "animal", "animal_cat", "猫");
        Add(map, "animal", "animal_dog", "狗");
        Add(map, "animal", "animal_bird", "鸟");
        Add(map, "animal", "animal_fish", "鱼");
        Add(map, "animal", "animal_horse", "马");
        Add(map, "animal", "animal_butterfly", "蝴蝶");
        Add(map, "animal", "animal_insect", "昆虫");
        Add(map, "animal", "animal_reptile", "爬行动物");
        Add(map, "animal", "animal_rabbit", "兔子");
        Add(map, "animal", "animal_bear", "熊");
        Add(map, "animal", "animal_cow", "牛");
        Add(map, "animal", "animal_sheep", "羊");
        Add(map, "animal", "animal_pig", "猪");
        Add(map, "animal", "animal_elephant", "大象");
        Add(map, "animal", "animal_lion", "狮子");
        Add(map, "animal", "animal_tiger", "老虎");
        Add(map, "animal", "animal_monkey", "猴子");
        Add(map, "animal", "animal_deer", "鹿");
        Add(map, "animal", "animal_penguin", "企鹅");
        Add(map, "scene", "outdoor_beach", "海滩");
        Add(map, "scene", "outdoor_mountain", "山脉");
        Add(map, "scene", "outdoor_forest", "森林");
        Add(map, "scene", "outdoor_city", "城市");
        Add(map, "scene", "outdoor_garden", "花园");
        Add(map, "scene", "outdoor_lake", "湖泊");
        Add(map, "scene", "outdoor_ocean", "海洋");
        Add(map, "scene", "outdoor_river", "河流");
        Add(map, "scene", "outdoor_snow", "雪景");
        Add(map, "scene", "outdoor_sunset", "日落");
        Add(map, "scene", "outdoor_sky", "天空");
        Add(map, "scene", "outdoor_field", "田野");
        Add(map, "scene", "outdoor_park", "公园");
        Add(map, "scene", "outdoor_street", "街道");
        Add(map, "scene", "outdoor_bridge", "桥梁");
        Add(map, "scene", "indoor_kitchen", "厨房");
        Add(map, "scene", "indoor_bedroom", "卧室");
        Add(map, "scene", "indoor_bathroom", "浴室");
        Add(map, "scene", "indoor_office", "办公室");
        Add(map, "scene", "indoor_restaurant", "餐厅");
        Add(map, "scene", "indoor_gym", "健身房");
        Add(map, "scene", "indoor_classroom", "教室");
        Add(map, "scene", "indoor_library", "图书馆");
        Add(map, "object", "food", "食物");
        Add(map, "object", "food_cake", "蛋糕");
        Add(map, "object", "food_fruit", "水果");
        Add(map, "object", "food_vegetable", "蔬菜");
        Add(map, "object", "vehicle_car", "汽车");
        Add(map, "object", "vehicle_bicycle", "自行车");
        Add(map, "object", "vehicle_motorcycle", "摩托车");
        Add(map, "object", "vehicle_bus", "公共汽车");
        Add(map, "object", "vehicle_train", "火车");
        Add(map, "object", "vehicle_airplane", "飞机");
        Add(map, "object", "vehicle_boat", "船");
        Add(map, "object", "flower", "花朵");
        Add(map, "object", "plant", "植物");
        Add(map, "object", "tree", "树木");
        Add(map, "object", "document", "文档");
        Add(map, "object", "document_receipt", "收据");
        Add(map, "object", "document_letter", "信件");
        Add(map, "object", "screenshot", "截图");
        Add(map, "object", "book", "书籍");
        Add(map, "object", "building", "建筑");
        Add(map, "object", "toy", "玩具");
        Add(map, "object", "furniture", "家具");
        Add(map, "object", "electronic", "电子产品");
        Add(map, "object", "clothing", "服装");
        Add(map, "object", "sports", "运动");
        Add(map, "object", "music", "音乐");
        Add(map, "object", "art", "艺术");
        return map;
    }

    private static void Add(
        Dictionary<string, (string, string)> map,
        string tagType,
        string identifier,
        string displayName) =>
        map[identifier] = (tagType, displayName);

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private sealed class NativeAnalysisResult
    {
        public List<NativeFace> Faces { get; set; } = [];
        public List<NativeText> RecognizedTexts { get; set; } = [];
        public List<NativeClassification> Classifications { get; set; } = [];
        public LocationInfo? Location { get; set; }
        public PhotoDateInfo? DateInfo { get; set; }
        public string? CameraInfo { get; set; }
    }

    private sealed class NativeFace
    {
        public float BoundingBoxX { get; set; }
        public float BoundingBoxY { get; set; }
        public float BoundingBoxW { get; set; }
        public float BoundingBoxH { get; set; }
        public string? FeaturePrint { get; set; }
    }

    private sealed class NativeText
    {
        public string Text { get; set; } = "";
        public float Confidence { get; set; }
        public List<string> Keywords { get; set; } = [];
    }

    private sealed class NativeClassification
    {
        public string Identifier { get; set; } = "";
        public float Confidence { get; set; }
    }
}
