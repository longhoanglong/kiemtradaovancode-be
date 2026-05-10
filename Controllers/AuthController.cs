using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Tokens;
using PlagiarismChecker.API.Models;
using PlagiarismChecker.API.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace PlagiarismChecker.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly IEmailService _emailService;
        public AuthController(IConfiguration config, IEmailService emailService)
        {
            _config = config;
            _emailService = emailService;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register(UserRegister req)
        {
            using var conn = new SqlConnection(_config.GetConnectionString("DefaultConnection"));
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(req.Password);
            var result = await conn.ExecuteScalarAsync<int>("sp_RegisterUser",
                new { req.Username, PasswordHash = passwordHash, req.Email },
                commandType: System.Data.CommandType.StoredProcedure);
            if (result != 1)
                return BadRequest("Tài khoản đã tồn tại");

            // Sinh OTP và gửi email xác nhận
            var otp = new Random().Next(100000, 999999).ToString();
            var otpExpiry = DateTime.UtcNow.AddMinutes(10);
            await conn.ExecuteAsync("UPDATE Accounts SET OTP = @OTP, OTPExpiry = @OTPExpiry WHERE Email = @Email", new { OTP = otp, OTPExpiry = otpExpiry, Email = req.Email });

            await _emailService.SendEmailAsync(req.Email, "Xác nhận đăng ký tài khoản",
                $@"<p>Xin chào {req.Username},</p>
<p>Mã xác thực tài khoản của bạn là: <b>{otp}</b></p>
<p>Mã này có hiệu lực trong 10 phút.</p>
<p>Trân trọng,<br>Plagiarism Checker</p>");
            return Ok("Đăng ký thành công, vui lòng kiểm tra email để xác thực tài khoản.");
        }

        [HttpPost("verify-otp")]
        public async Task<IActionResult> VerifyOTP([FromBody] VerifyOtpRequest req)
        {
            using var conn = new SqlConnection(_config.GetConnectionString("DefaultConnection"));
            var user = await conn.QueryFirstOrDefaultAsync<User>("SELECT * FROM Accounts WHERE Email = @Email", new { req.Email });

            if (user == null) return BadRequest(new { message = "Không tìm thấy tài khoản." });

            if (user.OTP != req.OTP || user.OTPExpiry < DateTime.UtcNow)
                return BadRequest(new { message = "Mã OTP không hợp lệ hoặc đã hết hạn." });

            // 1. Cập nhật Database (Xác thực thành công)
            await conn.ExecuteAsync("UPDATE Accounts SET IsEmailConfirmed = 1, OTP = NULL, OTPExpiry = NULL WHERE Email = @Email", new { req.Email });

            // 2. Gửi email cho Admin (Bọc try-catch để tránh làm sập API nếu gửi mail lỗi)
            try
            {
                await _emailService.SendEmailAsync(_config["EmailSettings:AdminEmail"], "Tài khoản mới chờ duyệt",
                    $@"<p>Có tài khoản mới: <b>{req.Email}</b> đã xác thực email và đang chờ duyệt.</p>");
            }
            catch (Exception ex)
            {
                // Chỉ in lỗi ra console của server, không làm gián đoạn quá trình của user
                Console.WriteLine("LỖI GỬI MAIL CHO ADMIN: " + ex.Message);
            }

            // 3. Trả về kết quả thành công cho Frontend
            return Ok(new { message = "Xác thực email thành công, vui lòng chờ admin duyệt." });
        }
        public class VerifyOtpRequest
        {
            public string Email { get; set; }
            public string OTP { get; set; }
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login(UserLogin req)
        {
            using var conn = new SqlConnection(_config.GetConnectionString("DefaultConnection"));
            var user = await conn.QueryFirstOrDefaultAsync<dynamic>("sp_GetUserByUsername",
                new { req.Username }, commandType: System.Data.CommandType.StoredProcedure);

            if (user == null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                return Unauthorized("Sai thông tin đăng nhập");

            bool isApproved = user.IsApproved ?? false;
            string role = user.Role ?? "user";

            if (!isApproved && role != "admin")
            {
                return StatusCode(403, "Tài khoản của bạn đang chờ quản trị viên phê duyệt.");
            }

            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(_config["JwtSettings:Key"]);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new Claim(ClaimTypes.Role, role)
                }),
                Expires = DateTime.UtcNow.AddDays(1),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            return Ok(new
            {
                Token = tokenHandler.WriteToken(token),
                Username = user.Username,
                Role = role
            });
        }

        [Authorize]
        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePassword(ChangePasswordRequest req)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null) return Unauthorized("Không tìm thấy thông tin định danh người dùng");
            var userId = Guid.Parse(userIdClaim.Value);

            using var conn = new SqlConnection(_config.GetConnectionString("DefaultConnection"));

            var userInDb = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT PasswordHash FROM Accounts WHERE Id = @Id", new { Id = userId });

            if (userInDb == null || !BCrypt.Net.BCrypt.Verify(req.OldPassword, userInDb.PasswordHash))
            {
                return BadRequest("Mật khẩu hiện tại không chính xác");
            }

            var newHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
            await conn.ExecuteAsync("sp_ChangePassword",
                new { UserId = userId, NewPasswordHash = newHash },
                commandType: System.Data.CommandType.StoredProcedure);

            return Ok("Thay đổi mật khẩu thành công");
        }
    }
}