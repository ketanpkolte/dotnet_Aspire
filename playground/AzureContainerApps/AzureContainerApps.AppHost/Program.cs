// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;

var builder = DistributedApplication.CreateBuilder(args);

var customDomain = builder.AddParameter("customDomain");
var certificateName = builder.AddParameter("certificateName");

// Testing secret parameters
var param = builder.AddParameter("secretparam", "fakeSecret", secret: true);

// Testing volumes
var redis = builder.AddRedis("cache")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume();

// Testing secret outputs
var cosmosDb = builder.AddAzureCosmosDB("account")
                      .RunAsEmulator(c => c.WithLifetime(ContainerLifetime.Persistent))
                      .AddDatabase("db");

// Testing a connection string
var blobs = builder.AddAzureStorage("storage")
                   .RunAsEmulator(c => c.WithLifetime(ContainerLifetime.Persistent))
                   .AddBlobs("blobs");

builder.AddProject<Projects.AzureContainerApps_ApiService>("api")
       .WithExternalHttpEndpoints()
       .WithReference(blobs)
       .WithReference(redis)
       .WithReference(cosmosDb)
       .WithEnvironment("VALUE", param)
       .PublishAsAzureContainerApp((module, app) =>
       {
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

           app.Configuration.Value!.Ingress!.Value!.CustomDomains = new Azure.Provisioning.BicepList<ContainerAppCustomDomain>()
           {
                new ContainerAppCustomDomain()
                {
                    BindingType = bindingTypeConditional,
                    Name = new IdentifierExpression(customDomainParameter.IdentifierName),
                    CertificateId = certificateOrEmpty
                }
           };

           // Scale to 0
           app.Template.Value!.Scale.Value!.MinReplicas = 0;
       });

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();

