using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;

namespace OficinaMecanica.Billing.Infrastructure.Security;

// A biblioteca Microsoft.IdentityModel.Protocols só traz retriever para o documento OIDC completo, e o
// Seguranca expõe um JWKS cru (apenas o array de chaves). Optei por baixar o JSON com o IDocumentRetriever
// padrão e desserializar direto como JsonWebKeySet.
public class JsonWebKeySetRetriever : IConfigurationRetriever<JsonWebKeySet>
{
    public async Task<JsonWebKeySet> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        var documento = await retriever.GetDocumentAsync(address, cancel).ConfigureAwait(false);
        return new JsonWebKeySet(documento);
    }
}
