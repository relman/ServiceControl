namespace Particular.ServiceControl
{
    using Autofac;
    using NServiceBus;
    using Particular.ServiceControl.DbMigrations;
    using Raven.Client;
    using Raven.Client.Embedded;
    using ServiceBus.Management.Infrastructure;
    using ServiceBus.Management.Infrastructure.Settings;

    public class SetupBootstrapper
    {
        private readonly Settings settings;

        public SetupBootstrapper(Settings settings)
        {
            this.settings = settings;
        }

        public void Run(string username)
        {
            var configuration = new BusConfiguration();
            configuration.AssembliesToScan(AllAssemblies.Except("ServiceControl.Plugin"));
            configuration.EnableInstallers(username);

            var containerBuilder = new ContainerBuilder();
            var loggingSettings = new LoggingSettings(settings.ServiceName);
            containerBuilder.RegisterInstance(loggingSettings);
            var documentStore = new EmbeddableDocumentStore();
            containerBuilder.RegisterInstance(documentStore).As<IDocumentStore>().ExternallyOwned();
            containerBuilder.RegisterInstance(settings);

            containerBuilder.RegisterModule<MigrationsModule>();

            using (documentStore)
            using (var container = containerBuilder.Build())
            using (NServiceBusFactory.Create(settings, container, null, documentStore, configuration))
            {
                container.Resolve<MigrationsManager>().ApplyMigrations();
            }
        }
    }
}