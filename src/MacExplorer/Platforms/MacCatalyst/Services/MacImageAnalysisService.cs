using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using CoreGraphics;
using MacExplorer.Models;
using MacExplorer.Services;
using Foundation;
using Vision;
using CoreLocation;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacImageAnalysisService : IImageAnalysisService
{
    // Geocoding cache: rounded coordinates → place name
    private readonly ConcurrentDictionary<(int lat, int lng), string> _geocodeCache = new();
    private readonly CLGeocoder _geocoder = new();

    // VNClassifyImageRequest label → (tag_type, display_name) mapping
    private static readonly Dictionary<string, (string tagType, string displayName)> ClassifierMap = BuildClassifierMap();

    public async Task<ImageAnalysisResult> AnalyzeImageAsync(string filePath, CancellationToken ct = default)
    {
        var result = new ImageAnalysisResult();

        var visionResult = await Task.Run(() => RunVisionAnalysis(filePath), ct);
        ct.ThrowIfCancellationRequested();

        var exifResult = await Task.Run(() => ExtractExifMetadata(filePath), ct);
        ct.ThrowIfCancellationRequested();

        // Merge results
        var faces = visionResult.faces;
        var texts = visionResult.texts;
        var classifications = visionResult.classifications;

        // Reverse geocode if GPS available
        LocationInfo? location = null;
        if (exifResult.latitude != 0 || exifResult.longitude != 0)
        {
            var placeName = await ReverseGeocodeAsync(exifResult.latitude, exifResult.longitude);
            if (placeName != null)
            {
                location = new LocationInfo
                {
                    Latitude = exifResult.latitude,
                    Longitude = exifResult.longitude,
                    PlaceName = placeName
                };
            }
        }

        // Build date info
        PhotoDateInfo? dateInfo = null;
        if (exifResult.takenDate.HasValue)
        {
            var dt = exifResult.takenDate.Value;
            dateInfo = new PhotoDateInfo
            {
                TakenAt = dt,
                YearMonth = dt.ToString("yyyy-MM"),
                Day = dt.ToString("yyyy-MM-dd")
            };
        }

        // Build camera info
        string? cameraInfo = null;
        if (!string.IsNullOrEmpty(exifResult.cameraMake) || !string.IsNullOrEmpty(exifResult.cameraModel))
        {
            var make = exifResult.cameraMake?.Trim() ?? "";
            var model = exifResult.cameraModel?.Trim() ?? "";
            // Avoid duplicate make in model (e.g. "Apple iPhone 16 Pro" when make is "Apple")
            if (!string.IsNullOrEmpty(make) && model.StartsWith(make, StringComparison.OrdinalIgnoreCase))
                cameraInfo = model;
            else
                cameraInfo = string.IsNullOrEmpty(make) ? model : $"{make} {model}";
        }

        return new ImageAnalysisResult
        {
            Faces = faces,
            RecognizedTexts = texts,
            Classifications = classifications,
            Location = location,
            DateInfo = dateInfo,
            CameraInfo = string.IsNullOrWhiteSpace(cameraInfo) ? null : cameraInfo.Trim()
        };
    }

    public float ComputeFaceDistance(byte[] print1, byte[] print2)
    {
        if (print1.Length != print2.Length) return float.MaxValue;
        int floatCount = print1.Length / sizeof(float);
        float sum = 0;
        for (int i = 0; i < floatCount; i++)
        {
            float a = BitConverter.ToSingle(print1, i * sizeof(float));
            float b = BitConverter.ToSingle(print2, i * sizeof(float));
            float diff = a - b;
            sum += diff * diff;
        }
        return MathF.Sqrt(sum);
    }

    // ── Vision Framework Analysis ──

    private static (List<DetectedFace> faces, List<RecognizedText> texts, List<ClassificationLabel> classifications)
        RunVisionAnalysis(string filePath)
    {
        var faces = new List<DetectedFace>();
        var texts = new List<RecognizedText>();
        var classifications = new List<ClassificationLabel>();

        try
        {
            var url = NSUrl.FromFilename(filePath);
            if (url == null) return (faces, texts, classifications);

            using var handler = new VNImageRequestHandler(url, new NSDictionary());

            // Prepare requests
            var faceRequest = new VNDetectFaceRectanglesRequest(null);
            var textRequest = new VNRecognizeTextRequest(null);
            textRequest.RecognitionLevel = VNRequestTextRecognitionLevel.Accurate;
            textRequest.RecognitionLanguages = ["zh-Hans", "zh-Hant", "en"];

            var classifyRequest = new VNClassifyImageRequest(null);

            var featurePrintRequest = new VNGenerateImageFeaturePrintRequest(null);

            // Execute requests
            handler.Perform(new VNRequest[] { faceRequest, textRequest, classifyRequest }, out var error);

            if (error != null)
            {
                Debug.WriteLine($"Vision analysis error for {filePath}: {error.LocalizedDescription}");
            }

            // Process face results
            if (faceRequest.Results is VNFaceObservation[] faceResults)
            {
                foreach (var face in faceResults)
                {
                    var bb = face.BoundingBox;
                    byte[]? featurePrint = null;

                    // Extract feature print for this face region
                    try
                    {
                        var fpRequest = new VNGenerateImageFeaturePrintRequest(null);
                        fpRequest.RegionOfInterest = face.BoundingBox;
                        using var fpHandler = new VNImageRequestHandler(url, new NSDictionary());
                        fpHandler.Perform(new VNRequest[] { fpRequest }, out var fpError);

                        if (fpRequest.Results is VNFeaturePrintObservation[] fpResults && fpResults.Length > 0)
                        {
                            var fpObs = fpResults[0];
                            if (fpObs.Data != null)
                            {
                                featurePrint = fpObs.Data.ToArray();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Feature print extraction failed: {ex.Message}");
                    }

                    faces.Add(new DetectedFace
                    {
                        BoundingBoxX = (float)bb.X,
                        BoundingBoxY = (float)bb.Y,
                        BoundingBoxW = (float)bb.Width,
                        BoundingBoxH = (float)bb.Height,
                        FeaturePrint = featurePrint
                    });
                }
            }

            // Process text results
            if (textRequest.Results is VNRecognizedTextObservation[] textResults)
            {
                foreach (var obs in textResults)
                {
                    var topCandidate = obs.TopCandidates(1);
                    if (topCandidate.Length > 0)
                    {
                        var text = topCandidate[0].String;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            var keywords = ExtractKeywords(text);
                            texts.Add(new RecognizedText
                            {
                                Text = text,
                                Confidence = topCandidate[0].Confidence,
                                Keywords = keywords
                            });
                        }
                    }
                }
            }

            // Process classification results
            if (classifyRequest.Results is VNClassificationObservation[] classResults)
            {
                foreach (var obs in classResults)
                {
                    if (obs.Confidence < 0.3f) continue;

                    var identifier = obs.Identifier;
                    if (ClassifierMap.TryGetValue(identifier, out var mapped))
                    {
                        classifications.Add(new ClassificationLabel
                        {
                            Identifier = identifier,
                            TagType = mapped.tagType,
                            DisplayName = mapped.displayName,
                            Confidence = obs.Confidence
                        });
                    }
                    else
                    {
                        // Default: classify as object
                        classifications.Add(new ClassificationLabel
                        {
                            Identifier = identifier,
                            TagType = "object",
                            DisplayName = identifier.Replace('_', ' '),
                            Confidence = obs.Confidence
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Vision analysis failed for {filePath}: {ex.Message}");
        }

        return (faces, texts, classifications);
    }

    // ── EXIF Metadata Extraction ──

    private static (double latitude, double longitude, DateTime? takenDate, string? cameraMake, string? cameraModel)
        ExtractExifMetadata(string filePath)
    {
        double lat = 0, lng = 0;
        DateTime? takenDate = null;
        string? make = null, model = null;

        try
        {
            var output = RunMdls(filePath);
            if (string.IsNullOrWhiteSpace(output)) return (lat, lng, takenDate, make, model);

            foreach (var line in output.Split('\n'))
            {
                var eqIdx = line.IndexOf('=');
                if (eqIdx < 0) continue;

                var key = line[..eqIdx].Trim();
                var value = line[(eqIdx + 1)..].Trim().Trim('"');
                if (value == "(null)") continue;

                switch (key)
                {
                    case "kMDItemLatitude":
                        double.TryParse(value, CultureInfo.InvariantCulture, out lat);
                        break;
                    case "kMDItemLongitude":
                        double.TryParse(value, CultureInfo.InvariantCulture, out lng);
                        break;
                    case "kMDItemContentCreationDate":
                        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                            takenDate = dt;
                        break;
                    case "kMDItemAcquisitionMake":
                        make = value;
                        break;
                    case "kMDItemAcquisitionModel":
                        model = value;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"EXIF extraction failed for {filePath}: {ex.Message}");
        }

        return (lat, lng, takenDate, make, model);
    }

    private static string RunMdls(string filePath)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "mdls",
                    Arguments = $"-name kMDItemLatitude -name kMDItemLongitude -name kMDItemContentCreationDate -name kMDItemAcquisitionMake -name kMDItemAcquisitionModel \"{filePath}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return output;
        }
        catch { return ""; }
    }

    // ── Reverse Geocoding ──

    private async Task<string?> ReverseGeocodeAsync(double latitude, double longitude)
    {
        // Cache key: round to ~1km precision
        var cacheKey = ((int)(latitude * 100), (int)(longitude * 100));
        if (_geocodeCache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var location = new CLLocation(latitude, longitude);
            var placemarks = await _geocoder.ReverseGeocodeLocationAsync(location);
            if (placemarks?.Length > 0)
            {
                var pm = placemarks[0];
                // Build place name: city + subLocality or administrativeArea
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(pm.Country)) parts.Add(pm.Country);
                if (!string.IsNullOrEmpty(pm.AdministrativeArea)) parts.Add(pm.AdministrativeArea);
                if (!string.IsNullOrEmpty(pm.Locality)) parts.Add(pm.Locality);
                if (!string.IsNullOrEmpty(pm.SubLocality)) parts.Add(pm.SubLocality);

                // Use most specific 2 parts
                var placeName = parts.Count switch
                {
                    0 => null,
                    1 => parts[0],
                    2 => string.Join(", ", parts.TakeLast(2)),
                    _ => string.Join(", ", parts.Skip(parts.Count - 2))
                };

                if (placeName != null)
                    _geocodeCache[cacheKey] = placeName;

                return placeName;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reverse geocoding failed: {ex.Message}");
        }

        return null;
    }

    // ── OCR Keyword Extraction ──

    private static List<string> ExtractKeywords(string text)
    {
        var keywords = new List<string>();
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return keywords;

        // Split by whitespace, punctuation, and symbols; keep words >= 2 chars
        var words = System.Text.RegularExpressions.Regex.Split(text, @"[\s\p{P}\p{S}]+");
        foreach (var word in words)
        {
            if (word.Length >= 2 && keywords.Count < 50 && !keywords.Contains(word))
                keywords.Add(word);
        }

        return keywords;
    }

    // ── Classifier Mapping ──

    private static Dictionary<string, (string tagType, string displayName)> BuildClassifierMap()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        // Animal categories
        AddMapping(map, "animal", "animal_cat", "\u732b");
        AddMapping(map, "animal", "animal_dog", "\u72d7");
        AddMapping(map, "animal", "animal_bird", "\u9e1f");
        AddMapping(map, "animal", "animal_fish", "\u9c7c");
        AddMapping(map, "animal", "animal_horse", "\u9a6c");
        AddMapping(map, "animal", "animal_butterfly", "\u8774\u8776");
        AddMapping(map, "animal", "animal_insect", "\u6606\u866b");
        AddMapping(map, "animal", "animal_reptile", "\u722c\u884c\u52a8\u7269");
        AddMapping(map, "animal", "animal_rabbit", "\u5154\u5b50");
        AddMapping(map, "animal", "animal_bear", "\u718a");
        AddMapping(map, "animal", "animal_cow", "\u725b");
        AddMapping(map, "animal", "animal_sheep", "\u7f8a");
        AddMapping(map, "animal", "animal_pig", "\u732a");
        AddMapping(map, "animal", "animal_elephant", "\u5927\u8c61");
        AddMapping(map, "animal", "animal_lion", "\u72ee\u5b50");
        AddMapping(map, "animal", "animal_tiger", "\u8001\u864e");
        AddMapping(map, "animal", "animal_monkey", "\u7334\u5b50");
        AddMapping(map, "animal", "animal_deer", "\u9e7f");
        AddMapping(map, "animal", "animal_penguin", "\u4f01\u9e45");

        // Scene categories
        AddMapping(map, "scene", "outdoor_beach", "\u6d77\u6ee9");
        AddMapping(map, "scene", "outdoor_mountain", "\u5c71\u8109");
        AddMapping(map, "scene", "outdoor_forest", "\u68ee\u6797");
        AddMapping(map, "scene", "outdoor_city", "\u57ce\u5e02");
        AddMapping(map, "scene", "outdoor_garden", "\u82b1\u56ed");
        AddMapping(map, "scene", "outdoor_lake", "\u6e56\u6cca");
        AddMapping(map, "scene", "outdoor_ocean", "\u6d77\u6d0b");
        AddMapping(map, "scene", "outdoor_river", "\u6cb3\u6d41");
        AddMapping(map, "scene", "outdoor_snow", "\u96ea\u666f");
        AddMapping(map, "scene", "outdoor_sunset", "\u65e5\u843d");
        AddMapping(map, "scene", "outdoor_sky", "\u5929\u7a7a");
        AddMapping(map, "scene", "outdoor_field", "\u7530\u91ce");
        AddMapping(map, "scene", "outdoor_park", "\u516c\u56ed");
        AddMapping(map, "scene", "outdoor_street", "\u8857\u9053");
        AddMapping(map, "scene", "outdoor_bridge", "\u6865\u6881");
        AddMapping(map, "scene", "indoor_kitchen", "\u53a8\u623f");
        AddMapping(map, "scene", "indoor_bedroom", "\u5367\u5ba4");
        AddMapping(map, "scene", "indoor_bathroom", "\u6d74\u5ba4");
        AddMapping(map, "scene", "indoor_office", "\u529e\u516c\u5ba4");
        AddMapping(map, "scene", "indoor_restaurant", "\u9910\u5385");
        AddMapping(map, "scene", "indoor_gym", "\u5065\u8eab\u623f");
        AddMapping(map, "scene", "indoor_classroom", "\u6559\u5ba4");
        AddMapping(map, "scene", "indoor_library", "\u56fe\u4e66\u9986");

        // Object categories
        AddMapping(map, "object", "food", "\u98df\u7269");
        AddMapping(map, "object", "food_cake", "\u86cb\u7cd5");
        AddMapping(map, "object", "food_fruit", "\u6c34\u679c");
        AddMapping(map, "object", "food_vegetable", "\u852c\u83dc");
        AddMapping(map, "object", "vehicle_car", "\u6c7d\u8f66");
        AddMapping(map, "object", "vehicle_bicycle", "\u81ea\u884c\u8f66");
        AddMapping(map, "object", "vehicle_motorcycle", "\u6469\u6258\u8f66");
        AddMapping(map, "object", "vehicle_bus", "\u5de5\u5171\u6c7d\u8f66");
        AddMapping(map, "object", "vehicle_train", "\u706b\u8f66");
        AddMapping(map, "object", "vehicle_airplane", "\u98de\u673a");
        AddMapping(map, "object", "vehicle_boat", "\u8239");
        AddMapping(map, "object", "flower", "\u82b1\u6735");
        AddMapping(map, "object", "plant", "\u690d\u7269");
        AddMapping(map, "object", "tree", "\u6811\u6728");
        AddMapping(map, "object", "building", "\u5efa\u7b51");
        AddMapping(map, "object", "document", "\u6587\u6863");
        AddMapping(map, "object", "document_receipt", "\u6536\u636e");
        AddMapping(map, "object", "document_letter", "\u4fe1\u4ef6");
        AddMapping(map, "object", "screenshot", "\u622a\u56fe");
        AddMapping(map, "object", "book", "\u4e66\u7c4d");
        AddMapping(map, "object", "toy", "\u73a9\u5177");
        AddMapping(map, "object", "furniture", "\u5bb6\u5177");
        AddMapping(map, "object", "electronic", "\u7535\u5b50\u4ea7\u54c1");
        AddMapping(map, "object", "clothing", "\u670d\u88c5");
        AddMapping(map, "object", "sports", "\u8fd0\u52a8");
        AddMapping(map, "object", "music", "\u97f3\u4e50");
        AddMapping(map, "object", "art", "\u827a\u672f");

        return map;
    }

    private static void AddMapping(Dictionary<string, (string, string)> map, string tagType, string identifier, string displayName)
    {
        map[identifier] = (tagType, displayName);
    }
}
