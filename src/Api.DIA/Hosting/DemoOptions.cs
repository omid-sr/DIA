using Dia.Contracts;

namespace Api.DIA.Hosting;

public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    public Guid UserId { get; set; } = DemoWallet.UserId;

    public decimal InitialBalance { get; set; } = DemoWallet.OpeningBalance;

    public bool AllowReset { get; set; }
}
