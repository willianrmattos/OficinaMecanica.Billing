namespace OficinaMecanica.Billing.Tests.Common;

public class RelogioFixo : TimeProvider
{
    public static readonly DateTimeOffset InstanteBase = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private DateTimeOffset _agora;

    public RelogioFixo() : this(InstanteBase) { }

    public RelogioFixo(DateTimeOffset agora)
    {
        _agora = agora;
    }

    public void Avancar(TimeSpan intervalo) => _agora += intervalo;

    public override DateTimeOffset GetUtcNow() => _agora;
}
