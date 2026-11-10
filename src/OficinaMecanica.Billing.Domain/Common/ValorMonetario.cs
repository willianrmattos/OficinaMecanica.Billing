namespace OficinaMecanica.Billing.Domain.Common;

internal static class ValorMonetario
{
    public static bool TemMaisDeDuasCasas(decimal valor) => decimal.Round(valor, 2) != valor;
}
