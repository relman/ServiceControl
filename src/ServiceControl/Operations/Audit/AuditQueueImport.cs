﻿namespace ServiceControl.Operations
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using Metrics;
    using NServiceBus;
    using NServiceBus.Logging;
    using NServiceBus.ObjectBuilder;
    using NServiceBus.Satellites;
    using NServiceBus.Transports;
    using NServiceBus.Unicast;
    using NServiceBus.Unicast.Transport;
    using Raven.Client;
    using ServiceBus.Management.Infrastructure.Settings;
    using ServiceControl.Operations.Audit;
    using ServiceControl.Operations.BodyStorage;
    using Timer = Metrics.Timer;

    public class AuditQueueImport : IAdvancedSatellite, IDisposable
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(AuditQueueImport));
        private readonly CriticalError criticalError;
        private readonly ISendMessages forwarder;
        private readonly IDocumentStore store;
        private readonly LoggingSettings loggingSettings;

        private readonly Settings settings;
        private readonly Timer timer = Metric.Timer("Audit messages dequeued", Unit.Custom("Messages"));
        private SatelliteImportFailuresHandler satelliteImportFailuresHandler;

        private MessageBodyFactory messageBodyFactory;
        private IMessageBodyStore messageBodyStore;
        private IMessageBodyStoragePolicy auditMessageBodyStoragePolicy;
        private RavenBatchOptimizer optimizer;
        private ProcessedMessageFactory processedMessageFactory;

        public AuditQueueImport(IBuilder builder, ISendMessages forwarder, IDocumentStore store, CriticalError criticalError, LoggingSettings loggingSettings, Settings settings, IMessageBodyStore messageBodyStore)
        {
            this.forwarder = forwarder;
            this.store = store;

            this.criticalError = criticalError;
            this.loggingSettings = loggingSettings;
            this.settings = settings;
            this.messageBodyStore = messageBodyStore;

            auditMessageBodyStoragePolicy = new AuditMessageBodyStoragePolicy(settings);
            messageBodyFactory = new MessageBodyFactory();
            
            processedMessageFactory = new ProcessedMessageFactory(
                builder.BuildAll<IEnrichImportedMessages>().Where(x => x.EnrichAudits).ToArray(),
                auditMessageBodyStoragePolicy,
                messageBodyStore);
        }

        public bool Handle(TransportMessage message)
        {
            using (timer.NewContext())
            {
                InnerHandle(message);

                if (settings.ForwardAuditMessages)
                {
                    TransportMessageCleaner.CleanForForwarding(message);
                    forwarder.Send(message, new SendOptions(settings.AuditLogQueue));
                }
            }

            return true;
        }

        public void Start()
        {
            if (!TerminateIfForwardingIsEnabledButQueueNotWritable())
            {
                Logger.Info($"Audit import is now started, feeding audit messages from: {InputAddress}");
            }

            optimizer = new RavenBatchOptimizer(store, CancellationToken.None);
        }

        public void Stop()
        {
            optimizer.Dispose();
        }

        public Address InputAddress => settings.AuditQueue;

        public bool Disabled => false;

        public Action<TransportReceiver> GetReceiverCustomization()
        {
            satelliteImportFailuresHandler = new SatelliteImportFailuresHandler(store,
                Path.Combine(loggingSettings.LogPath, @"FailedImports\Audit"), tm => new FailedAuditImport
                {
                    Message = tm
                },
                criticalError);

            return receiver =>
            {
                receiver.FailureManager = satelliteImportFailuresHandler;
                receiver.ChangeMaximumConcurrencyLevel(30);
            };
        }

        public void Dispose()
        {
            satelliteImportFailuresHandler?.Dispose();
        }

        private void InnerHandle(TransportMessage message)
        {
            var metadata = messageBodyFactory.Create(message.Id, message);
            var claimCheck = messageBodyStore.Store(BodyStorageTags.Audit, message.Body, metadata, auditMessageBodyStoragePolicy);

            var processedMessage = processedMessageFactory.Create(message.Headers);

            processedMessageFactory.AddBodyDetails(processedMessage, claimCheck);
            
            optimizer.Write(processedMessage);
        }

        private bool TerminateIfForwardingIsEnabledButQueueNotWritable()
        {
            if (!settings.ForwardAuditMessages)
            {
                return false;
            }

            try
            {
                //Send a message to test the forwarding queue
                var testMessage = new TransportMessage(Guid.Empty.ToString("N"), new Dictionary<string, string>());
                forwarder.Send(testMessage, new SendOptions(settings.AuditLogQueue));
                return false;
            }
            catch (Exception messageForwardingException)
            {
                criticalError.Raise("Audit Import cannot start", messageForwardingException);
                return true;
            }
        }
    }
}