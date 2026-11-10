using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OficinaMecanica.Billing.Application.Interfaces;

namespace OficinaMecanica.Billing.Infrastructure.Security;

// Valida o cabeçalho x-signature do Mercado Pago ("ts=...,v1=..."): v1 é o HMAC-SHA256 (chave = segredo do
// webhook) do manifest "id:<data.id em minúsculas>;request-id:<x-request-id>;ts:<ts>;". O ";" final faz
// parte do manifest e um campo ausente simplesmente sai dele. É uma função pura do segredo, dos cabeçalhos e
// do relógio injetado, então os testes a exercitam com vetores conhecidos.
public sealed class AssinaturaWebhookMercadoPago : IAssinaturaWebhookValidator
{
    // ts em milissegundos tem 13 dígitos e em segundos, 10: o limite separa as duas escalas sem ambiguidade
    // para qualquer data realista.
    private const long LimiteEntreSegundosEMilissegundos = 100_000_000_000;

    private readonly byte[] _segredo;
    private readonly TimeSpan _tolerancia;
    private readonly TimeProvider _timeProvider;

    public AssinaturaWebhookMercadoPago(string segredo, TimeSpan tolerancia, TimeProvider timeProvider)
    {
        if (string.IsNullOrWhiteSpace(segredo))
            throw new ArgumentException("O segredo do webhook é obrigatório.", nameof(segredo));

        if (tolerancia <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(tolerancia), "A tolerância da assinatura deve ser positiva.");

        _segredo = Encoding.UTF8.GetBytes(segredo);
        _tolerancia = tolerancia;
        _timeProvider = timeProvider;
    }

    public bool Validar(string? xSignature, string? xRequestId, string dataId)
    {
        if (string.IsNullOrWhiteSpace(xSignature))
            return false;

        if (!TentarLerCabecalho(xSignature, out var ts, out var v1))
            return false;

        if (!long.TryParse(ts, NumberStyles.None, CultureInfo.InvariantCulture, out var carimbo))
            return false;

        var emitidoEm = carimbo >= LimiteEntreSegundosEMilissegundos
            ? DateTimeOffset.FromUnixTimeMilliseconds(carimbo)
            : DateTimeOffset.FromUnixTimeSeconds(carimbo);

        if ((_timeProvider.GetUtcNow() - emitidoEm).Duration() > _tolerancia)
            return false;

        byte[] informada;
        try
        {
            informada = Convert.FromHexString(v1);
        }
        catch (FormatException)
        {
            return false;
        }

        var esperada = CalcularHmac(_segredo, MontarManifesto(dataId, xRequestId, ts));
        return CryptographicOperations.FixedTimeEquals(esperada, informada);
    }

    public static string MontarManifesto(string? dataId, string? xRequestId, string ts)
    {
        var manifesto = new StringBuilder();

        if (!string.IsNullOrEmpty(dataId))
            manifesto.Append("id:").Append(dataId.ToLowerInvariant()).Append(';');

        if (!string.IsNullOrEmpty(xRequestId))
            manifesto.Append("request-id:").Append(xRequestId).Append(';');

        if (!string.IsNullOrEmpty(ts))
            manifesto.Append("ts:").Append(ts).Append(';');

        return manifesto.ToString();
    }

    // Usado pelo endpoint de dev e pelos testes para produzir um cabeçalho que o validador de verdade aceita.
    public static string GerarCabecalho(string segredo, string dataId, string xRequestId, DateTimeOffset agora)
    {
        var ts = agora.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var hmac = CalcularHmac(Encoding.UTF8.GetBytes(segredo), MontarManifesto(dataId, xRequestId, ts));
        return $"ts={ts},v1={Convert.ToHexString(hmac).ToLowerInvariant()}";
    }

    private static byte[] CalcularHmac(byte[] segredo, string manifesto) =>
        HMACSHA256.HashData(segredo, Encoding.UTF8.GetBytes(manifesto));

    private static bool TentarLerCabecalho(string xSignature, out string ts, out string v1)
    {
        ts = string.Empty;
        v1 = string.Empty;

        foreach (var parte in xSignature.Split(','))
        {
            var separador = parte.IndexOf('=');
            if (separador <= 0)
                continue;

            var chave = parte[..separador].Trim();
            var valor = parte[(separador + 1)..].Trim();

            if (chave == "ts")
                ts = valor;
            else if (chave == "v1")
                v1 = valor;
        }

        return ts.Length > 0 && v1.Length > 0;
    }
}
