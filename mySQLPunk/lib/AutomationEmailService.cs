using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using Newtonsoft.Json;

namespace mySQLPunk.lib
{
    /// <summary>自動執行作業共用的 SMTP 設定；密碼不寫入這個檔案，而是存在 Windows 認證管理員。</summary>
    public sealed class AutomationSmtpSettings
    {
        public string Host { get; set; }
        public int Port { get; set; } = 587;
        /// <summary>STARTTLS；只有寄到本機（localhost）的測試伺服器可以關閉。</summary>
        public bool UseTls { get; set; } = true;
        public string UserName { get; set; }
        public string From { get; set; }
    }

    /// <summary>
    /// 作業完成後寄出純文字通知信。只寫入作業名稱、類型、狀態、列數、嘗試次數、時間與訊息，不含 SQL 或認證。
    /// </summary>
    public static class AutomationEmailService
    {
        public const string CredentialTarget = "mySQLPunk:automation:smtp";
        public const int MaximumRecipients = 10;

        public static string SettingsPath(ScheduledJobStore store)
        {
            return Path.Combine(store.RootDirectory, "smtp.json");
        }

        public static AutomationSmtpSettings Load(ScheduledJobStore store)
        {
            string path = SettingsPath(store);
            if (!File.Exists(path)) return null;
            return JsonConvert.DeserializeObject<AutomationSmtpSettings>(File.ReadAllText(path, Encoding.UTF8));
        }

        /// <summary>儲存設定；password 為 null 代表沿用已存的密碼，空字串代表清除。</summary>
        public static void Save(ScheduledJobStore store, AutomationSmtpSettings settings, string password)
        {
            Validate(settings);
            Directory.CreateDirectory(store.RootDirectory);
            File.WriteAllText(SettingsPath(store), JsonConvert.SerializeObject(settings, Formatting.Indented), new UTF8Encoding(false));
            if (password == null) return;
            if (password.Length == 0)
            {
                WindowsCredentialService.TryDeletePassword(CredentialTarget);
                return;
            }
            if (!WindowsCredentialService.TryWritePassword(CredentialTarget, settings.UserName ?? string.Empty, password))
            {
                throw new InvalidOperationException(Localization.T("Automation.SmtpCredentialWriteFailed"));
            }
        }

        public static void Validate(AutomationSmtpSettings settings)
        {
            if (settings == null) throw new InvalidOperationException(Localization.T("Automation.SmtpNotConfigured"));
            settings.Host = (settings.Host ?? string.Empty).Trim();
            if (settings.Host.Length == 0) throw new InvalidOperationException(Localization.T("Automation.SmtpHostRequired"));
            if (settings.Port < 1 || settings.Port > 65535) throw new InvalidOperationException(Localization.T("Automation.SmtpInvalidPort"));
            if (!settings.UseTls && !IsLoopback(settings.Host)) throw new InvalidOperationException(Localization.T("Automation.SmtpTlsRequired"));
            try
            {
                settings.From = new MailAddress((settings.From ?? string.Empty).Trim()).Address;
            }
            catch (FormatException)
            {
                throw new InvalidOperationException(Localization.T("Automation.SmtpInvalidFrom"));
            }
            settings.UserName = (settings.UserName ?? string.Empty).Trim();
        }

        public static List<MailAddress> ParseRecipients(string text)
        {
            List<MailAddress> recipients = new List<MailAddress>();
            foreach (string item in (text ?? string.Empty).Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string value = item.Trim();
                if (value.Length == 0) continue;
                try
                {
                    MailAddress address = new MailAddress(value);
                    if (!string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase)) throw new FormatException();
                    recipients.Add(address);
                }
                catch (FormatException)
                {
                    throw new InvalidOperationException(Localization.Format("Automation.InvalidRecipient", value));
                }
            }
            if (recipients.Count > MaximumRecipients) throw new InvalidOperationException(Localization.Format("Automation.TooManyRecipients", MaximumRecipients));
            return recipients;
        }

        public static void Send(AutomationSmtpSettings settings, string password, IList<MailAddress> recipients, string subject, string body)
        {
            Validate(settings);
            if (recipients == null || recipients.Count == 0) throw new InvalidOperationException(Localization.T("Automation.RecipientsRequired"));
            using (MailMessage message = new MailMessage())
            using (SmtpClient client = new SmtpClient(settings.Host, settings.Port))
            {
                message.From = new MailAddress(settings.From);
                foreach (MailAddress recipient in recipients) message.To.Add(recipient);
                message.Subject = subject.Replace('\r', ' ').Replace('\n', ' ');
                message.SubjectEncoding = Encoding.UTF8;
                message.Body = body;
                message.BodyEncoding = Encoding.UTF8;
                message.IsBodyHtml = false;
                client.EnableSsl = settings.UseTls;
                client.Timeout = 15000;
                client.DeliveryMethod = SmtpDeliveryMethod.Network;
                client.UseDefaultCredentials = false;
                if (!string.IsNullOrEmpty(settings.UserName))
                {
                    if (string.IsNullOrEmpty(password)) throw new InvalidOperationException(Localization.T("Automation.SmtpPasswordMissing"));
                    client.Credentials = new NetworkCredential(settings.UserName, password);
                }
                client.Send(message);
            }
        }

        /// <summary>寄出作業結果；回傳寫入執行紀錄的狀態文字。失敗不影響作業本身的結果。</summary>
        public static string Notify(ScheduledJobStore store, ScheduledJobDefinition job, ScheduledJobRunRecord record, Func<string> passwordProvider = null)
        {
            if (string.IsNullOrWhiteSpace(job.EmailTo)) return null;
            bool success = string.Equals(record.Status, "Success", StringComparison.OrdinalIgnoreCase);
            if (job.NotifyOnlyOnFailure && success) return Localization.T("Automation.EmailSkipped");
            try
            {
                AutomationSmtpSettings settings = Load(store);
                if (settings == null) throw new InvalidOperationException(Localization.T("Automation.SmtpNotConfigured"));
                string password = null;
                if (!string.IsNullOrEmpty(settings.UserName))
                {
                    if (passwordProvider != null) password = passwordProvider();
                    else WindowsCredentialService.TryReadPassword(CredentialTarget, out password);
                }
                List<MailAddress> recipients = ParseRecipients(job.EmailTo);
                Send(settings, password, recipients, BuildSubject(record), BuildBody(record));
                return Localization.Format("Automation.EmailSent", recipients.Count);
            }
            catch (Exception exception)
            {
                return Localization.Format("Automation.EmailFailed", ExceptionMessageService.GetReason(exception));
            }
        }

        public static string BuildSubject(ScheduledJobRunRecord record)
        {
            return "[mySQLPunk] " + record.JobName + " — " + record.Status;
        }

        public static string BuildBody(ScheduledJobRunRecord record)
        {
            StringBuilder body = new StringBuilder();
            body.AppendLine(Localization.Format("Automation.Email.Job", record.JobName, record.JobType));
            body.AppendLine(Localization.Format("Automation.Email.Status", record.Status, record.Attempts));
            body.AppendLine(Localization.Format("Automation.Email.Rows", record.Rows < 0 ? "-" : record.Rows.ToString("N0", CultureInfo.InvariantCulture)));
            body.AppendLine(Localization.Format("Automation.Email.Time", record.StartedUtc, record.FinishedUtc, record.ElapsedMilliseconds));
            if (!string.IsNullOrWhiteSpace(record.OutputPath)) body.AppendLine(Localization.Format("Automation.Email.Path", record.OutputPath));
            body.AppendLine();
            body.AppendLine(record.Message ?? string.Empty);
            return body.ToString();
        }

        private static bool IsLoopback(string host)
        {
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            IPAddress address;
            return IPAddress.TryParse(host, out address) && IPAddress.IsLoopback(address);
        }
    }
}
