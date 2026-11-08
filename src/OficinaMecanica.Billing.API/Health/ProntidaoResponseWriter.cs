using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace OficinaMecanica.Billing.API.Health;

// Corpo de /health/ready: o status agregado e o de cada check, para quem monitora ver o broker Degraded mesmo com o
// HTTP em 200. Escreve só a descrição de cada check, nunca a exceção, que traz host e detalhes de conexão.
public static class ProntidaoResponseWriter
{
    private static readonly JsonSerializerOptions Opcoes = new(JsonSerializerDefaults.Web)
    {
        // Mantém os acentos das descrições em português legíveis (o padrão os escapa em hexadecimal); é JSON puro, nunca HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static Task EscreverAsync(HttpContext context, HealthReport relatorio)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var corpo = new
        {
            status = relatorio.Status.ToString(),
            checks = relatorio.Entries.ToDictionary(
                e => e.Key,
                e => new { status = e.Value.Status.ToString(), descricao = e.Value.Description })
        };

        return JsonSerializer.SerializeAsync(context.Response.Body, corpo, Opcoes, context.RequestAborted);
    }
}
