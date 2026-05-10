using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using PlagiarismChecker.API.Services;
using PlagiarismChecker.API.Models;

namespace PlagiarismChecker.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Roles = "admin")]
    public class AdminController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly IEmailService _emailService;
        public AdminController(IConfiguration config, IEmailService emailService)
        {
            _config = config;
            _emailService = emailService;
        }

        [HttpGet("stats")]
        public async Task<IActionResult> GetSystemStats()
        {
            using var conn = new SqlConnection(_config.GetConnectionString("DefaultConnection"));
            var stats = await conn.QueryFirstOrDefaultAsync<dynamic>("sp_GetSystemStats", commandType: System.Data.CommandType.StoredProcedure);
            return Ok(stats);
        }

        [HttpGet("users")]
        public async Task<IActionResult> GetAllUsers()
        {
            using var conn = new SqlConnection(_config.GetConnectionString("DefaultConnection"));
            var users = await conn.QueryAsync("sp_GetAllAccounts", commandType: System.Data.CommandType.StoredProcedure);
            return Ok(users);
        }

        [HttpGet("users/{id}/history")]
        public async Task<IActionResult> GetUserHistory(Guid id)
        {
            using var conn = new SqlConnection(_config.GetConnectionString("DefaultConnection"));
            var history = await conn.QueryAsync("sp_GetHistoryByAdmin", new { UserId = id }, commandType: System.Data.CommandType.StoredProcedure);
            return Ok(history);
        }

        [HttpDelete("users/{id}")]
        public async Task<IActionResult> DeleteUser(Guid id)
        {
            using var conn = new SqlConnection(_config.GetConnectionString("DefaultConnection"));
            await conn.ExecuteAsync("sp_DeleteUser", new { UserId = id }, commandType: System.Data.CommandType.StoredProcedure);
            return Ok(new { message = "Xóa người dùng thành công" });
        }

        [HttpPost("users/{id}/approve")]
        public async Task<IActionResult> ApproveUser(Guid id)
        {
            using var conn = new SqlConnection(_config.GetConnectionString("DefaultConnection"));
            await conn.ExecuteAsync("sp_ApproveUser", new { UserId = id }, commandType: System.Data.CommandType.StoredProcedure);
            // Lấy thông tin user vừa duyệt
            var user = await conn.QueryFirstOrDefaultAsync<User>("SELECT * FROM Accounts WHERE Id = @Id", new { Id = id });
            if (user != null)
            {
                await _emailService.SendEmailAsync(user.Email, "Tài khoản đã được duyệt",
                    $@"<p>Xin chào {user.Username},</p>
<p>Tài khoản của bạn đã được admin duyệt và sẵn sàng sử dụng.</p>
<p>Trân trọng,<br>Plagiarism Checker</p>");
            }
            return Ok(new { message = "Đã duyệt tài khoản" });
        }

        [HttpPost("users/{id}/reset-password")]
        public async Task<IActionResult> ResetPassword(Guid id)
        {
            var newHash = BCrypt.Net.BCrypt.HashPassword("123456");
            using var conn = new SqlConnection(_config.GetConnectionString("DefaultConnection"));
            await conn.ExecuteAsync("sp_ResetPassword", new { UserId = id, NewPasswordHash = newHash }, commandType: System.Data.CommandType.StoredProcedure);
            return Ok(new { message = "Mật khẩu đã đặt lại thành 123456" });
        }
    }
}