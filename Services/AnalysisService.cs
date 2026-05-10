using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using PlagiarismChecker.API.Models;
using Microsoft.Data.SqlClient;
using Dapper;
using Microsoft.Extensions.Configuration;

namespace PlagiarismChecker.API.Services
{
    public class AnalysisService
    {
        private readonly string _connectionString;

        // Kích thước N-Gram (gom 3 token liên tiếp để so sánh cấu trúc)
        private const int NGramSize = 3;

        // Các đuôi file code hợp lệ
        private readonly HashSet<string> _allowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".py"
        };

        // Các thư mục rác cần bỏ qua
        private readonly HashSet<string> _blacklistedFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "properties", ".git", ".vs", ".idea", "packages", "node_modules", "debug", "release"
        };

        // Các file rác cần bỏ qua
        private readonly HashSet<string> _ignoredFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "AssemblyInfo.cs", "GlobalSuppressions.cs", "AssemblyAttributes.cs"
        };

        public AnalysisService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        // ============================================================
        // LOGIC TRÍCH XUẤT
        // ============================================================
        public Dictionary<string, string> ExtractFiles(IFormFile zipFile)
        {
            var files = new Dictionary<string, string>();

            using (var stream = zipFile.OpenReadStream())
            using (var archive = new ZipArchive(stream))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    var fullPath = entry.FullName.Replace("\\", "/");
                    var pathSegments = fullPath.Split('/');

                    // 1. Lọc rác (bin, obj, properties...)
                    if (pathSegments.Any(segment => _blacklistedFolders.Contains(segment))) continue;
                    if (_ignoredFiles.Contains(entry.Name) || entry.Name.EndsWith(".g.cs") || entry.Name.EndsWith(".Designer.cs")) continue;

                    // 2. Kiểm tra đuôi file
                    var ext = Path.GetExtension(entry.Name).ToLower();
                    if (!_allowedExtensions.Contains(ext)) continue;

                    // 3. TÌM MSSV TỪ ĐƯỜNG DẪN (Logic Linh Hoạt)
                    string studentId = "";

                    foreach (var segment in pathSegments)
                    {
                        // Regex: Tìm chuỗi số có từ 5 chữ số trở lên đứng đầu tên folder
                        var match = Regex.Match(segment, @"^(\d{5,})");

                        if (match.Success)
                        {
                            studentId = match.Groups[1].Value;
                            break;
                        }
                    }

                    // 4. ĐỌC VÀ ĐỔI TÊN FILE
                    using (var reader = new StreamReader(entry.Open()))
                    {
                        var content = reader.ReadToEnd();

                        var fileName = entry.Name;
                        string nameNoExt = Path.GetFileNameWithoutExtension(fileName);

                        // Nếu tìm thấy MSSV thì gắn vào, nếu không thì giữ nguyên
                        if (!string.IsNullOrEmpty(studentId))
                        {
                            fileName = $"{nameNoExt}_{studentId}{ext}";
                        }

                        // Xử lý trùng tên (Thêm _1, _2 nếu cần)
                        string baseFileName = Path.GetFileNameWithoutExtension(fileName);
                        string finalFileName = fileName;
                        int count = 1;

                        while (files.ContainsKey(finalFileName))
                        {
                            finalFileName = $"{baseFileName}_{count}{ext}";
                            count++;
                        }

                        files.Add(finalFileName, content);
                    }
                }
            }

            return files;
        }

        // ============================================================
        // LOGIC TÍNH TOÁN
        // ============================================================
        public (double Percentage, string ExplanationJson) AnalyzeCode(string codeA, string codeB)
        {
            // 1. Chuẩn hóa và cắt Token
            var rawTokensA = TokenizeAndNormalize(codeA);
            var rawTokensB = TokenizeAndNormalize(codeB);

            if (rawTokensA.Count == 0 || rawTokensB.Count == 0) return (0, "{}");

            // 2. Chuyển đổi thành N-Grams (Bắt lỗi tráo đổi vị trí code)
            var ngramsA = GetNGrams(rawTokensA, NGramSize);
            var ngramsB = GetNGrams(rawTokensB, NGramSize);

            // 3. Đếm tần suất các N-Grams
            var mapA = GetFrequencyMap(ngramsA);
            var mapB = GetFrequencyMap(ngramsB);

            var sharedTokens = new List<SharedTokenInfo>();
            int totalSharedCount = 0;

            foreach (var item in mapA)
            {
                if (mapB.TryGetValue(item.Key, out int countB))
                {
                    int minCount = Math.Min(item.Value, countB);
                    totalSharedCount += minCount;
                    sharedTokens.Add(new SharedTokenInfo { Token = item.Key, CountMin = minCount });
                }
            }

            int totalA = ngramsA.Count;
            int totalB = ngramsB.Count;
            double similarity = 0;

            if (totalA + totalB > 0)
            {
                similarity = (2.0 * totalSharedCount) / (totalA + totalB) * 100;
            }

            var explanationData = new ExplanationData
            {
                TokensA = ngramsA,
                TokensB = ngramsB,
                FrequencyMapA = mapA.Select(x => new TokenInfo { Token = x.Key, Count = x.Value }).ToList(),
                FrequencyMapB = mapB.Select(x => new TokenInfo { Token = x.Key, Count = x.Value }).ToList(),
                SharedTokens = sharedTokens.OrderByDescending(x => x.CountMin).ToList(),
                TotalSharedCount = totalSharedCount,
                TotalTokensA = totalA,
                TotalTokensB = totalB,
                FinalSimilarity = Math.Round(similarity, 2)
            };

            return (Math.Round(similarity, 2), JsonSerializer.Serialize(explanationData));
        }

        // Hàm chuẩn hóa mới: Loại bỏ comment, quy chuẩn chuỗi và số
        private List<string> TokenizeAndNormalize(string code)
        {
            if (string.IsNullOrEmpty(code)) return new List<string>();

            // 1. Xóa comment (// hoặc /* */)
            var cleanCode = Regex.Replace(code, @"\/\/.*|\/\*[\s\S]*?\*\/", " ");

            // 2. Chuẩn hóa chuỗi (string literals) thành token <STR>
            cleanCode = Regex.Replace(cleanCode, @"\"".*?\""|\'.*?\'", "<STR>");

            // 3. Chuẩn hóa các con số thành token <NUM>
            cleanCode = Regex.Replace(cleanCode, @"\b\d+(\.\d+)?\b", "<NUM>");

            // 4. Tách token (bao gồm cả các toán tử quan trọng)
            var matches = Regex.Matches(cleanCode, @"\w+|[{};(),.=<>\+\-\*\/]");
            return matches.Select(m => m.Value).ToList();
        }

        // Hàm tạo N-grams để bắt cấu trúc code
        private List<string> GetNGrams(List<string> tokens, int n)
        {
            var ngrams = new List<string>();
            for (int i = 0; i <= tokens.Count - n; i++)
            {
                // Ghép n token liên tiếp lại với nhau
                ngrams.Add(string.Join(" ", tokens.Skip(i).Take(n)));
            }

            // Nếu code quá ngắn không đủ tạo n-gram, fallback về token gốc
            return ngrams.Count > 0 ? ngrams : tokens;
        }

        private Dictionary<string, int> GetFrequencyMap(List<string> tokens)
        {
            return tokens.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        }

        public async Task<bool> DeleteSessionAsync(string sessionId, string userId)
        {
            using var conn = new SqlConnection(_connectionString);
            var sql = @"DELETE FROM AnalysisSessions WHERE Id = @Id AND UserId = @UserId";
            var rowsAffected = await conn.ExecuteAsync(sql, new { Id = sessionId, UserId = userId });
            return rowsAffected > 0;
        }

        public async Task<bool> ClearAllHistoryAsync(string userId)
        {
            using var conn = new SqlConnection(_connectionString);
            var sql = @"DELETE FROM AnalysisSessions WHERE UserId = @UserId";
            await conn.ExecuteAsync(sql, new { UserId = userId });
            return true;
        }
    }
}