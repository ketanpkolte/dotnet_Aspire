// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;
using Azure.Provisioning;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for customizing Azure Container App resource.
/// </summary>
public static class ContainerAppExtensions
{
    /// <summary>
    /// Configures the custom domain for the container app.
    /// </summary>
    /// <param name="app">The container app resource to configure for custom domain usage.</param>
    /// <param name="customDomain">A resource builder for a parameter resource capturing the name of the custom domain.
    /// <param name="certificateName">A resource builder for a parameter resource capturing the name of the certficate configured in the Azure Portal.</param>
    /// <exception cref="ArgumentException">Throws if the container app resource is not parented to a <see cref="ResourceModuleConstruct"/>.</exception>
    public static void ConfigureCustomDomain(this ContainerApp app, IResourceBuilder<ParameterResource> customDomain, IResourceBuilder<ParameterResource> certificateName)
    {
        if (app.ParentInfrastructure is not ResourceModuleConstruct module)
        {
            throw new ArgumentException("Cannot configure custom domain when resource is not parented by ResourceModuleConstruct.", nameof(app));
        }

        var containerAppManagedEnvironmentIdParameter = module.GetResources().OfType<ProvisioningParameter>().Single(
            p => p.IdentifierName == "outputs_azure_container_apps_environment_id");
        var certificatNameParameter = certificateName.AsProvisioningParameter(module);
        var customDomainParameter = customDomain.AsProvisioningParameter(module);

        var bindingTypeConditional = new ConditionalExpression(
            new BinaryExpression(
                new IdentifierExpression(certificatNameParameter.IdentifierName),
                BinaryOperator.NotEqual,
                new StringLiteral(string.Empty)),
            new StringLiteral("SniEnabled"),
            new StringLiteral("Disabled")
            );

        var certificateOrEmpty = new ConditionalExpression(
            new BinaryExpression(
                new IdentifierExpression(certificatNameParameter.IdentifierName),
                BinaryOperator.NotEqual,
                new StringLiteral(string.Empty)),
            new InterpolatedString(
                "{0}/managedCertificates/{1}",
                [
                 new IdentifierExpression(containerAppManagedEnvironmentIdParameter.IdentifierName),
                    new IdentifierExpression(certificatNameParameter.IdentifierName)
                 ]),
            new NullLiteral()
            );

        app.Configuration.Value!.Ingress!.Value!.CustomDomains = new BicepList<ContainerAppCustomDomain>()
           {
                new ContainerAppCustomDomain()
                {
                    BindingType = bindingTypeConditional,
                    Name = new IdentifierExpression(customDomainParameter.IdentifierName),
                    CertificateId = certificateOrEmpty
                }
           };
    }
}
