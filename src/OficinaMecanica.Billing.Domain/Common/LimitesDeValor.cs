namespace OficinaMecanica.Billing.Domain.Common;

// Optei por um teto explícito em vez de deixar o decimal estourar: o armazenamento é decimal(18,2) e uma
// mensagem com valor absurdo precisa virar OrcamentoNaoGerado (DomainException), não OverflowException/Fault.
public static class LimitesDeValor
{
    public const decimal PrecoUnitarioMaximo = 1_000_000.00m;
    public const int QuantidadeMaxima = 10_000;
    public const decimal ValorTotalMaximo = 1_000_000.00m;
}
