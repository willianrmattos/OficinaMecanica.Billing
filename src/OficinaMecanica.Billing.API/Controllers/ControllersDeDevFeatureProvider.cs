using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace OficinaMecanica.Billing.API.Controllers;

// Tira o DevPagamentosController do conjunto de controllers quando a simulação não está habilitada: a rota não
// existe (404) e também não aparece no Swagger, em vez de existir e responder "proibido". A decisão é uma função
// avaliada quando o MVC descobre os controllers (depois da configuração final do host), não no registro.
public sealed class ControllersDeDevFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
{
    private readonly Func<bool> _habilitado;

    public ControllersDeDevFeatureProvider(Func<bool> habilitado)
    {
        _habilitado = habilitado;
    }

    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        if (_habilitado())
            return;

        var dev = feature.Controllers.Where(c => c.AsType() == typeof(DevPagamentosController)).ToList();
        foreach (var controller in dev)
            feature.Controllers.Remove(controller);
    }
}
