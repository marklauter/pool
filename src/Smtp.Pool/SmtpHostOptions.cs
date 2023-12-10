namespace Smtp.Pool;

public sealed class SmtpHostOptions
{
    public string Host { get; set; } = null!;
    public int Port { get; set; } = 25;
    public bool UseSsl { get; set; }
}
