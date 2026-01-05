using System.Net;
using System.Net.Mail;

namespace LapTrinhMang.Services;

public class SendMailService
{
    private readonly IConfiguration _config;

    public SendMailService(IConfiguration config)
    {
        _config = config;
    }

    public async Task<bool> SendEmailAsync(string toEmail, string subject, string htmlBody)
    {
        try
        {
            var settings = _config.GetSection("MailSettings");
            string fromEmail = settings["Mail"] ?? "";
            string password = settings["Password"] ?? "";
            string host = settings["Host"] ?? "smtp.gmail.com";
            int port = int.Parse(settings["Port"] ?? "587");

            var message = new MailMessage
            {
                From = new MailAddress(fromEmail, settings["DisplayName"]),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };
            message.To.Add(new MailAddress(toEmail));

            using var client = new SmtpClient(host, port)
            {
                Credentials = new NetworkCredential(fromEmail, password),
                EnableSsl = true
            };

            await client.SendMailAsync(message);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Lỗi gửi mail: " + ex.Message);
            return false;
        }
    }
}