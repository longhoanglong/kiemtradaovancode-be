using System.Threading.Tasks;

namespace PlagiarismChecker.API.Services
{
    public interface IEmailService
    {
        Task SendEmailAsync(string to, string subject, string htmlContent);
    }
}
