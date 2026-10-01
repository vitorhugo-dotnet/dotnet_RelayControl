using Microsoft.FeatureManagement;

namespace SonicRelay.Api.Features;

public sealed class FeatureEndpointFilter(string featureName) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var features = context.HttpContext.RequestServices.GetRequiredService<IVariantFeatureManager>();
        return await features.IsEnabledAsync(featureName) ? await next(context) : Results.NotFound();
    }
}
