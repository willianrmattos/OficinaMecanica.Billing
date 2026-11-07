using System.Security.Cryptography;
using System.Text;

namespace OficinaMecanica.Billing.Application.Mensagens;

internal static class IdentificadorDeMensagem
{
    // Optei por derivar o MessageId do agregado de origem e do tipo da mensagem: reprocessar o mesmo
    // comando republica a mesma mensagem com o mesmo id, e o inbox do consumidor a descarta como duplicata.
    public static Guid Deterministico(Guid origem, string tipo)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{origem:N}:{tipo}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
