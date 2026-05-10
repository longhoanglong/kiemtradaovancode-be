namespace PlagiarismChecker.API.Models
{
    // Dùng để hiển thị Bước 1 (Danh sách) và Bước 2 (Bảng tần suất)
    public class TokenInfo
    {
        public string Token { get; set; }
        public int Count { get; set; } // Tần suất xuất hiện
    }

    // Dùng để hiển thị Bước 3 (Các token chung)
    public class SharedTokenInfo
    {
        public string Token { get; set; }
        public int CountMin { get; set; } // Số lượng tính (Tần suất nhỏ nhất giữa 2 bên)
    }

    // Cấu trúc tổng thể lưu vào Database
    public class ExplanationData
    {
        // Bước 1: Danh sách Token thô
        public List<string> TokensA { get; set; }
        public List<string> TokensB { get; set; }

        // Bước 2: Frequency Map (Thống kê tần suất)
        public List<TokenInfo> FrequencyMapA { get; set; }
        public List<TokenInfo> FrequencyMapB { get; set; }

        // Bước 3: Tính toán chi tiết
        public List<SharedTokenInfo> SharedTokens { get; set; }
        public int TotalSharedCount { get; set; }
        public int TotalTokensA { get; set; }
        public int TotalTokensB { get; set; }
        public double FinalSimilarity { get; set; }
    }
}