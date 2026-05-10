using Dapper; // Thư viện thao tác Database
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using PlagiarismChecker.API.Services;
using System.Security.Claims;

namespace PlagiarismChecker.API.Controllers
{
    [Authorize] // Bắt buộc đăng nhập mới được gọi API
    [Route("api/[controller]")]
    [ApiController]
    public class AnalysisController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly AnalysisService _service;

        public AnalysisController(IConfiguration config)
        {
            _config = config;
            // Khởi tạo Service và truyền config để kết nối DB
            _service = new AnalysisService(config);
        }

        // ==========================================
        // API 1: UPLOAD VÀ PHÂN TÍCH
        // ==========================================
        [HttpPost("upload")]
        public async Task<IActionResult> UploadAndAnalyze(IFormFile file, [FromForm] double threshold = 50)
        {
            // 1. Kiểm tra đầu vào
            if (file == null || !file.FileName.EndsWith(".zip"))
                return BadRequest("Vui lòng upload file định dạng .zip");

            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdString)) return Unauthorized("Không tìm thấy thông tin người dùng.");
            var userId = Guid.Parse(userIdString);

            // 2. Giải nén file zip
            var filesContent = _service.ExtractFiles(file);

            if (filesContent.Count < 2)
                return BadRequest("File zip cần chứa ít nhất 2 file code hợp lệ để so sánh.");

            // 3. Chuẩn bị Session
            var sessionId = Guid.NewGuid();
            int suspiciousCount = 0;
            int totalComparisons = 0;

            using var conn = new SqlConnection(_config.GetConnectionString("DefaultConnection"));
            await conn.OpenAsync();

            var sqlCreateSession = @"
                INSERT INTO AnalysisSessions 
                (Id, UserId, ZipFileName, UploadDate, Threshold, TotalSubmissions, TotalComparisons, SuspiciousPairsCount)
                VALUES 
                (@Id, @UserId, @ZipFileName, GETDATE(), @Threshold, @TotalSubmissions, 0, 0)";

            await conn.ExecuteAsync(sqlCreateSession, new
            {
                Id = sessionId,
                UserId = userId,
                ZipFileName = file.FileName,
                Threshold = threshold,
                TotalSubmissions = filesContent.Count
            });

            // 4. Phân nhóm theo đuôi file (Extension) để tối ưu so sánh
            var filesByExtension = filesContent
                .GroupBy(x => Path.GetExtension(x.Key).ToLower())
                .ToList();

            // Danh sách chờ để Bulk Insert
            var resultsToInsert = new List<object>();

            // 5. So sánh chéo trong từng nhóm ngôn ngữ
            foreach (var group in filesByExtension)
            {
                var fileNames = group.Select(x => x.Key).ToList();

                // Bỏ qua nếu nhóm ngôn ngữ này chỉ có 1 file
                if (fileNames.Count < 2) continue;

                for (int i = 0; i < fileNames.Count; i++)
                {
                    for (int j = i + 1; j < fileNames.Count; j++)
                    {
                        totalComparisons++;
                        var nameA = fileNames[i];
                        var nameB = fileNames[j];
                        var codeA = filesContent[nameA];
                        var codeB = filesContent[nameB];

                        var result = _service.AnalyzeCode(codeA, codeB);

                        if (result.Percentage >= threshold)
                        {
                            suspiciousCount++;
                            resultsToInsert.Add(new
                            {
                                Id = Guid.NewGuid(),
                                SessionId = sessionId,
                                FileA = nameA,
                                FileB = nameB,
                                Similarity = result.Percentage,
                                SourceA = codeA,
                                SourceB = codeB,
                                ExplanationJson = result.ExplanationJson
                            });
                        }
                    }
                }
            }

            // 6. Bulk Insert kết quả vi phạm
            if (resultsToInsert.Any())
            {
                var sqlSaveResult = @"
                    INSERT INTO ComparisonResults 
                    (Id, SessionId, FileA, FileB, Similarity, SourceA, SourceB, ExplanationJson)
                    VALUES 
                    (@Id, @SessionId, @FileA, @FileB, @Similarity, @SourceA, @SourceB, @ExplanationJson)";

                await conn.ExecuteAsync(sqlSaveResult, resultsToInsert);
            }

            // 7. Cập nhật lại Session
            var sqlUpdateSession = @"
                UPDATE AnalysisSessions 
                SET TotalComparisons = @Total, SuspiciousPairsCount = @Suspicious
                WHERE Id = @Id";

            await conn.ExecuteAsync(sqlUpdateSession, new
            {
                Total = totalComparisons,
                Suspicious = suspiciousCount,
                Id = sessionId
            });

            return Ok(new
            {
                SessionId = sessionId,
                Message = "Phân tích hoàn tất",
                TotalFiles = filesContent.Count,
                SuspiciousPairs = suspiciousCount
            });
        }

        // ==========================================
        // API 2: LẤY LỊCH SỬ (HISTORY)
        // ==========================================
        [HttpGet("history")]
        public async Task<IActionResult> GetHistory()
        {
            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdString)) return Unauthorized();
            var userId = Guid.Parse(userIdString);

            using var conn = new SqlConnection(_config.GetConnectionString("DefaultConnection"));

            // Lấy danh sách, sắp xếp mới nhất lên đầu
            var sql = "SELECT * FROM AnalysisSessions WHERE UserId = @UserId ORDER BY UploadDate DESC";
            var history = await conn.QueryAsync(sql, new { UserId = userId });

            return Ok(history);
        }

        // ==========================================
        // API 3: LẤY DANH SÁCH CÁC CẶP TRÙNG CỦA 1 SESSION
        // ==========================================
        [HttpGet("session/{sessionId}")]
        public async Task<IActionResult> GetSessionResult(Guid sessionId)
        {
            using var conn = new SqlConnection(_config.GetConnectionString("DefaultConnection"));

            var sql = @"SELECT Id, FileA, FileB, Similarity 
                        FROM ComparisonResults 
                        WHERE SessionId = @SessionId 
                        ORDER BY Similarity DESC";

            var results = await conn.QueryAsync(sql, new { SessionId = sessionId });
            return Ok(results);
        }

        // ==========================================
        // API 4: LẤY CHI TIẾT 1 CẶP SO SÁNH
        // ==========================================
        [HttpGet("detail/{comparisonId}")]
        public async Task<IActionResult> GetDetail(Guid comparisonId)
        {
            using var conn = new SqlConnection(_config.GetConnectionString("DefaultConnection"));

            var sql = "SELECT * FROM ComparisonResults WHERE Id = @Id";
            var detail = await conn.QueryFirstOrDefaultAsync(sql, new { Id = comparisonId });

            if (detail == null) return NotFound("Không tìm thấy dữ liệu so sánh này.");

            return Ok(detail);
        }

        // ==========================================
        // API 5: XÓA 1 MỤC LỊCH SỬ (CÓ BẮT LỖI CHI TIẾT)
        // ==========================================
        [HttpDelete("history/{sessionId}")]
        public async Task<IActionResult> DeleteSession(string sessionId)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Không tìm thấy thông tin người dùng.");

            try
            {
                // Gọi Service để xóa
                var success = await _service.DeleteSessionAsync(sessionId, userId);

                if (!success)
                    return NotFound(new { message = "Không tìm thấy phiên làm việc hoặc bạn không có quyền xóa." });

                return Ok(new { message = "Xóa thành công" });
            }
            catch (Exception ex)
            {
                // Trả về lỗi chi tiết từ Database để Frontend hiển thị
                return BadRequest(new { message = "Lỗi Database: " + ex.Message });
            }
        }

        // ==========================================
        // API 6: XÓA TẤT CẢ LỊCH SỬ (CÓ BẮT LỖI CHI TIẾT)
        // ==========================================
        [HttpDelete("history")]
        public async Task<IActionResult> ClearAllHistory()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Không tìm thấy thông tin người dùng.");

            try
            {
                await _service.ClearAllHistoryAsync(userId);
                return Ok(new { message = "Đã xóa toàn bộ lịch sử" });
            }
            catch (Exception ex)
            {
                // Trả về lỗi chi tiết
                return BadRequest(new { message = "Lỗi Database: " + ex.Message });
            }
        }
    }
}