using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PlagiarismChecker.API.Services
{
    public class EmailService : IEmailService
    {
        private readonly IConfiguration _config;
        private readonly HttpClient _httpClient;

        public EmailService(IConfiguration config)
        {
            _config = config;
            _httpClient = new HttpClient();
        }

        public async Task SendEmailAsync(string to, string subject, string htmlContent)
        {
            var emailSettings = _config.GetSection("EmailSettings");
            string apiKey = "";

            // --- ĐOẠN CODE "LÁCH LUẬT" LÚC DEMO ---
            if (to == "nguyentrungnghia.lhu@gmail.com")
            {
                // Nếu gửi cho Admin -> Lấy chìa khóa của Admin
                apiKey = emailSettings["ResendApiKey_Admin"];
            }
            else if (to == "quhoa2004@gmail.com")
            {
                // Nếu gửi cho User -> Lấy chìa khóa của User
                apiKey = emailSettings["ResendApiKey_User"];
            }
            else
            {
                // Chặn lỗi nếu ai đó nhập email khác vào web
                throw new System.Exception($"Báo với quản trị viên để được duyệt !");
            }
            // -------------------------------------

            // Đóng gói nội dung thư
            var requestBody = new
            {
                from = "Plagiarism Checker <onboarding@resend.dev>", // Bắt buộc dùng mail mặc định này để test
                to = new[] { to },
                subject = subject,
                html = htmlContent
            };

            var jsonContent = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            // Gắn API Key tương ứng vào Header
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            // Gửi đi
            var response = await _httpClient.PostAsync("https://api.resend.com/emails", jsonContent);

            if (!response.IsSuccessStatusCode)
            {
                var errorDetails = await response.Content.ReadAsStringAsync();
                throw new System.Exception($"Lỗi khi gửi email qua Resend: {errorDetails}");
            }
        }
    }
}
