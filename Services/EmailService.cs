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
            // Khởi tạo HttpClient để gọi API
            _httpClient = new HttpClient();
        }

        public async Task SendEmailAsync(string to, string subject, string htmlContent)
        {
            var emailSettings = _config.GetSection("EmailSettings");
            var apiKey = emailSettings["ResendApiKey"];
            var senderEmail = emailSettings["SenderEmail"]; 

            // 1. Chuẩn bị dữ liệu theo đúng chuẩn của Resend API
            var requestBody = new
            {
                from = $"Plagiarism Checker <{senderEmail}>",
                to = new[] { to },
                subject = subject,
                html = htmlContent
            };

            // 2. Chuyển đổi dữ liệu sang chuỗi JSON
            var jsonContent = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            // 3. Đính kèm API Key vào Header để xác thực
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            // 4. Gửi Request tới Resend
            var response = await _httpClient.PostAsync("https://api.resend.com/emails", jsonContent);

            // 5. Kiểm tra kết quả
            if (!response.IsSuccessStatusCode)
            {
                var errorDetails = await response.Content.ReadAsStringAsync();
                throw new System.Exception($"Lỗi khi gửi email qua Resend: {errorDetails}");
            }
        }
    }
}
