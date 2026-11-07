using System.Text.Json.Serialization;

namespace OficinaMecanica.Contracts.Common;

// Serializo como texto para o golden JSON continuar legivel e para reordenar/acrescentar
// valores no enum nunca mudar o significado de uma mensagem que ja esta em fila.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TipoPrazo
{
    Aprovacao = 1,
    Pagamento = 2
}
