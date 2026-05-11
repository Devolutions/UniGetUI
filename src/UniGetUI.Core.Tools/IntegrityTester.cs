using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.Core.Tools;

public static class IntegrityTester
{
    public class MismatchedHash
    {
        public string Got = string.Empty;
        public string Expected = string.Empty;
    }

    public struct Result
    {
        public bool Passed;
        public IReadOnlyList<string> MissingFiles;
        public Dictionary<string, MismatchedHash> CorruptedFiles;
    }

    private static string GetSHA256(string fullPath, bool canRetry)
    {
        try
        {
            using (var sha256 = SHA256.Create())
            {
                using (var stream = File.OpenRead(fullPath))
                {
                    var hashBytes = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hashBytes).Replace("-", "");
                }
            }
        }
        catch (Exception ex)
        {
            if (canRetry)
            {
                Task.Delay(1000).GetAwaiter().GetResult();
                return GetSHA256(fullPath, false);
            }

            return $"{ex.GetType()}: {ex.Message}";
        }
    }

    public static Result CheckIntegrity(bool allowRetry = true)
    {
        string integrityTreePath = Path.Join(
            CoreData.UniGetUIExecutableDirectory,
            "IntegrityTree.json"
        );
        if (!File.Exists(integrityTreePath))
        {
            Logger.Error(
                "/IntegrityTree.json does not exist, integrity check will not be performed!"
            );
            return new()
            {
                Passed = false,
                MissingFiles = ["/IntegrityTree.json"],
                CorruptedFiles = new Dictionary<string, MismatchedHash>(),
            };
        }

        string rawData = File.ReadAllText(integrityTreePath);
        Dictionary<string, string>? data = null;

        try
        {
            data = CoreToolsJson.DeserializeStringDictionary(rawData);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to deserialize JSON object");
            Logger.Error(ex);
        }

        if (data is null)
        {
            return new()
            {
                Passed = false,
                MissingFiles = [],
                CorruptedFiles = new()
                {
                    {
                        "",
                        new MismatchedHash() { Got = rawData, Expected = "A valid JSON" }
                    },
                },
            };
        }

        Dictionary<string, MismatchedHash> mismatches = new();
        List<string> misses = new();

        foreach (var (file, expectedHash) in data)
        {
            var fullPath = Path.Join(CoreData.UniGetUIExecutableDirectory, file);
            if (!File.Exists(fullPath))
            {
                misses.Add($"/{file}");
                Logger.Error($"File {file} expected but did not exist");
                continue;
            }

            var currentHash = GetSHA256(fullPath, allowRetry).ToLower();
            if (currentHash != expectedHash.ToLower())
            {
                mismatches.Add($"/{file}", new() { Expected = expectedHash, Got = currentHash });
                Logger.Error(
                    $"File {file} expected to have sha256 {expectedHash}, but had {currentHash} instead"
                );
            }
        }

        Result result = new()
        {
            Passed = !misses.Any() && !mismatches.Any(),
            MissingFiles = misses,
            CorruptedFiles = mismatches,
        };

        if (result.Passed)
        {
            Logger.ImportantInfo("Integrity check passed successfully!");
        }

        return result;
    }

    public static string GetReadableReport(Result result)
    {
        var Builder = new StringBuilder();
        if (result.Passed)
        {
            Builder.Append("No integrity violations were found.\n");
        }
        if (result.MissingFiles.Any())
        {
            Builder.Append("Missing files: ");
            foreach (var file in result.MissingFiles)
                Builder.Append($"\n - {file}");
            Builder.Append('\n');
        }
        if (result.CorruptedFiles.Any())
        {
            Builder.Append("Corrupted files: ");
            foreach (var (file, hashes) in result.CorruptedFiles)
                Builder.Append(
                    $"\n - {file} (sha256 mismatch, got {hashes.Got} but expected {hashes.Expected} "
                );
            Builder.Append('\n');
        }

        return Builder.ToString();
    }
}
