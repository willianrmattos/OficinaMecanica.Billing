using System.Text.Json;
using System.Text.Json.Serialization;

namespace OficinaMecanica.Billing.API.Models;

/// <summary>Corpo opcional do reprocessamento manual de um estorno.</summary>
/// <param name="Motivo">Motivo informado pelo staff (até 500 caracteres).</param>
public record RefazerEstornoRequest(string? Motivo);

/// <summary>Notificação de webhook enviada pelo Mercado Pago.</summary>
/// <param name="Type">Tópico da notificação; só <c>payment</c> é processado.</param>
/// <param name="Action">Ação que gerou a notificação (por exemplo <c>payment.updated</c>).</param>
/// <param name="Data">Dados do recurso notificado.</param>
public record NotificacaoMercadoPagoRequest(string? Type, string? Action, DadosDaNotificacaoMercadoPago? Data);

/// <summary>Identificador do recurso notificado.</summary>
/// <param name="Id">Identificador do pagamento no Mercado Pago.</param>
public record DadosDaNotificacaoMercadoPago([property: JsonConverter(typeof(TextoOuNumeroConverter))] string? Id);

// O Mercado Pago manda data.id como texto na produção e como número em algumas simulações; aceito os dois
// para a desserialização do corpo nunca ser o motivo de uma notificação ser rejeitada.
public sealed class TextoOuNumeroConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => throw new JsonException("O identificador deve ser texto ou número.")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
