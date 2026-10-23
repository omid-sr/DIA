namespace Api.DIA.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string VirtualHost { get; set; } = "/";

    public string User { get; set; } = "guest";

    public string Password { get; set; } = "guest";

    public ushort PrefetchCount { get; set; } = 1;
}
