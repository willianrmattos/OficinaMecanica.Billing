namespace OficinaMecanica.Billing.Application.Common;

internal static class NormalizadorDeMotivo
{
    public const int TamanhoMaximo = 500;
    public const string MotivoNaoInformado = "Motivo não informado.";

    // Optei por normalizar o motivo aqui, e não rejeitá-lo no validator: o contrato não o restringe e uma
    // ValidationException não responderia à saga, que ficaria esperando o cancelamento ou estorno.
    public static string Normalizar(string? motivo)
    {
        if (string.IsNullOrWhiteSpace(motivo))
            return MotivoNaoInformado;

        var limpo = motivo.Trim();
        return limpo.Length > TamanhoMaximo ? limpo[..TamanhoMaximo] : limpo;
    }
}
