﻿namespace ServiceControl.MessageAuditing
{
    using System;
    using System.Collections.Generic;
    using NServiceBus;
    using Contracts.Operations;

    public class ProcessedMessage
    {
        public ProcessedMessage(ImportSuccessfullyProcessedMessage message)
        {

            Id = message.UniqueMessageId;
            
            ReceivingEndpoint = message.ReceivingEndpoint;

            SendingEndpoint = message.SendingEndpoint;

            MessageMetadata = message.Metadata;

            string processedAt;

            if (message.PhysicalMessage.Headers.TryGetValue(Headers.ProcessingEnded, out processedAt))
            {
                ProcessedAt = DateTimeExtensions.ToUtcDateTime(processedAt);
            }
            else
            {
                ProcessedAt = DateTime.UtcNow;//best guess    
            }
        }

        public Dictionary<string, MessageMetadata> MessageMetadata { get; set; }

        public string Id { get; set; }

        public DateTime ProcessedAt { get; set; }

        public EndpointDetails SendingEndpoint { get; set; }

        public EndpointDetails ReceivingEndpoint { get; set; }


    }
}